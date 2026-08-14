// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UTSModDownloader - fetches catalogued mods, one job at a time.
 *
 * It reuses UsefulTORStuffUpdater's GithubRelease/GithubAsset DTOs and its hard-won details (the
 * User-Agent header GitHub demands, the try/catch around Deserialize so a 403 rate-limit body cannot
 * kill the coroutine and strand the busy flag), but keeps its OWN queue and busy state: the self-
 * updater's flag also covers its release checks, and a background check must not be able to block a
 * sync the player explicitly asked for.
 *
 * Everything security-relevant happens here, so it is spelled out:
 *   - The releases URL is BUILT from the catalog entry, never received (rule V1).
 *   - The asset is picked by the catalog's file name, not by whatever the release calls its files.
 *   - The download URL is validated against the catalog entry before a single byte is fetched (V5).
 *   - The target path is the catalog's, not the GitHub asset's name field (V4).
 *   - The target release must match the host's version EXACTLY. If that version has no release,
 *     the job fails with a message instead of quietly grabbing something similar.
 *
 * Jobs run strictly sequentially: GitHub allows 60 unauthenticated API calls per hour, and each job
 * costs one call plus one download.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Networking;

namespace UsefulTORStuff {

    public enum JobState { Pending, Working, Done, Failed }

    public sealed class SyncJob {
        public CatalogEntry Catalog;
        public Version TargetVersion;
        public JobState State = JobState.Pending;
        public float Progress;
        public long SizeBytes;
        // Localization key describing why the job failed (shown in the panel), null while fine.
        public string ErrorKey;
    }

    public class UTSModDownloader : MonoBehaviour {
        public static UTSModDownloader Instance { get; private set; }

        public UTSModDownloader(IntPtr ptr) : base(ptr) { }

        private readonly List<SyncJob> jobs = new List<SyncJob>();
        private bool running;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        [HideFromIl2Cpp]
        public IReadOnlyList<SyncJob> Jobs => jobs;

        [HideFromIl2Cpp]
        public bool IsRunning => running;

        [HideFromIl2Cpp]
        public bool AllDone => jobs.Count > 0 && !running
                            && jobs.TrueForAll(j => j.State == JobState.Done || j.State == JobState.Failed);

        [HideFromIl2Cpp]
        public bool AnySucceeded => jobs.Exists(j => j.State == JobState.Done);

        // Queue one row. Re-queuing a mod that is already pending/working is ignored so a double
        // click cannot download the same file twice.
        [HideFromIl2Cpp]
        public void Enqueue(SyncRow row) {
            if (row == null || !row.IsDownloadable || row.HostVersion == null) return;
            foreach (var j in jobs) {
                if (j.Catalog.Id != row.Catalog.Id) continue;
                if (j.State == JobState.Pending || j.State == JobState.Working) return;
            }
            jobs.Add(new SyncJob { Catalog = row.Catalog, TargetVersion = row.HostVersion });
            StartPump();
        }

        [HideFromIl2Cpp]
        public void EnqueueAll(List<SyncRow> rows) {
            if (rows == null) return;
            foreach (var r in rows) Enqueue(r);
        }

        [HideFromIl2Cpp]
        private void StartPump() {
            if (running) return;
            running = true;
            this.StartCoroutine(CoPump());
        }

        [HideFromIl2Cpp]
        private IEnumerator CoPump() {
            while (true) {
                SyncJob job = null;
                foreach (var j in jobs) { if (j.State == JobState.Pending) { job = j; break; } }
                if (job == null) break;
                yield return this.StartCoroutine(CoRunJob(job));
            }
            running = false;

            // Remember where we were BEFORE the player restarts, so the main menu can offer the way
            // back into this lobby. Only worth doing when something actually landed on disk.
            if (AnySucceeded) UTSRejoin.RememberCurrentLobby();
        }

        [HideFromIl2Cpp]
        private IEnumerator CoRunJob(SyncJob job) {
            job.State = JobState.Working;
            job.Progress = 0f;

            // ---- 1. release list (URL built from the catalog, never received) ----
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(job.Catalog.ReleasesApiUrl);
            // GitHub rejects clients without a User-Agent (same fix as UsefulTORStuffUpdater).
            www.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            var op = www.SendWebRequest();
            while (!op.isDone) yield return new WaitForEndOfFrame();

            if (www.isNetworkError || www.isHttpError) {
                www.downloadHandler.Dispose(); www.Dispose();
                Fail(job, "uts.modsync.error_network");
                yield break;
            }

            List<GithubRelease> releases = null;
            // No yield inside, so try/catch is allowed here. A rate-limited GitHub answers with a
            // JSON object instead of an array; that must not throw out of the coroutine.
            try {
                releases = JsonSerializer.Deserialize<List<GithubRelease>>(www.downloadHandler.text);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[ModSync] {job.Catalog.DisplayName}: release list unreadable ({ex.Message}).");
            } finally {
                www.downloadHandler.Dispose(); www.Dispose();
            }

            if (releases == null || releases.Count == 0) {
                Fail(job, "uts.modsync.error_releases");
                yield break;
            }

            // ---- 2. the release whose version matches the host EXACTLY ----
            GithubRelease target = null;
            foreach (var r in releases) {
                if (r == null || r.Draft) continue;
                Version v;
                try { v = r.Version; } catch { continue; }   // tags that are not versions at all
                if (UsefulTORStuffUpdater.SemCompare(v, job.TargetVersion) == 0) { target = r; break; }
            }
            if (target == null) {
                Fail(job, "uts.modsync.error_no_matching_release");
                yield break;
            }

            // ---- 3. the asset the CATALOG names ----
            GithubAsset asset = target.Assets?.FirstOrDefault(a => a != null && a.Name == job.Catalog.AssetName);
            if (asset == null) {
                Fail(job, "uts.modsync.error_no_asset");
                yield break;
            }

            // ---- 4. URL validation before anything is fetched ----
            if (!UTSModCatalog.IsTrustedAssetUrl(job.Catalog, asset.DownloadUrl)) {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[ModSync] {job.Catalog.DisplayName}: refusing untrusted download url '{asset.DownloadUrl}'.");
                Fail(job, "uts.modsync.error_untrusted_url");
                yield break;
            }
            job.SizeBytes = asset.Size;

            // ---- 5. download ----
            var dl = new UnityWebRequest();
            dl.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            dl.SetUrl(asset.DownloadUrl);
            dl.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            dl.downloadHandler = new DownloadHandlerBuffer();
            var dop = dl.SendWebRequest();
            while (!dop.isDone) {
                job.Progress = dl.downloadProgress;
                yield return new WaitForEndOfFrame();
            }
            if (dl.isNetworkError || dl.isHttpError) {
                dl.downloadHandler.Dispose(); dl.Dispose();
                Fail(job, "uts.modsync.error_download");
                yield break;
            }
            job.Progress = 1f;

            // ---- 6. write, keeping the previous file as .old (the updaters' convention) ----
            string filePath = job.Catalog.TargetPath;
            byte[] data = dl.downloadHandler.data;
            dl.downloadHandler.Dispose(); dl.Dispose();

            bool moved = false;
            try {
                if (File.Exists(filePath + ".old")) File.Delete(filePath + ".old");
                if (File.Exists(filePath)) { File.Move(filePath, filePath + ".old"); moved = true; }
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ModSync] {job.Catalog.DisplayName}: {ex.Message}");
                Fail(job, "uts.modsync.error_write");
                yield break;
            }

            var persist = File.WriteAllBytesAsync(filePath, data);
            while (!persist.IsCompleted) {
                if (persist.Exception != null) break;
                yield return new WaitForEndOfFrame();
            }

            if (persist.Exception != null) {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[ModSync] {job.Catalog.DisplayName}: write failed - {persist.Exception.Message}");
                // Put the previous DLL back, otherwise a failed replace leaves the player with no mod.
                try { if (moved && !File.Exists(filePath)) File.Move(filePath + ".old", filePath); } catch { }
                Fail(job, "uts.modsync.error_write");
                yield break;
            }

            job.State = JobState.Done;
            UTSModSync.MarkFetched(job.Catalog.Id);
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[ModSync] installed {job.Catalog.DisplayName} v{job.TargetVersion} -> {filePath} (restart required).");
        }

        [HideFromIl2Cpp]
        private static void Fail(SyncJob job, string errorKey) {
            job.State = JobState.Failed;
            job.ErrorKey = errorKey;
            UsefulTORStuffPlugin.Logger?.LogWarning(
                $"[ModSync] {job.Catalog.DisplayName}: job failed ({errorKey}).");
        }
    }
}
