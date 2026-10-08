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

    // Which release a job fetches: the host's exact version (mod sync), the newest stable, or the
    // newest release of either channel (Mod Manager downloads and modpacks).
    public enum JobMode { Exact, Latest, LatestPrerelease }

    public sealed class SyncJob {
        public CatalogEntry Catalog;
        public JobMode Mode = JobMode.Exact;
        public Version TargetVersion;      // the exact target; filled in by the job for Latest modes
        public JobState State = JobState.Pending;
        public float Progress;
        public long SizeBytes;
        // Localization key describing why the job failed (shown in the panel), null while fine.
        public string ErrorKey;
        // Done without a download: the newest release is the version already on disk.
        public bool NoChange;
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

        /// <summary>Queue the newest release of a catalog entry (Mod Manager, modpacks). Same double-click guard.</summary>
        [HideFromIl2Cpp]
        public SyncJob EnqueueLatest(CatalogEntry entry, bool prerelease) {
            if (entry == null) return null;
            foreach (var j in jobs) {
                if (j.Catalog.Id != entry.Id) continue;
                if (j.State == JobState.Pending || j.State == JobState.Working) return j;
            }
            var job = new SyncJob { Catalog = entry, Mode = prerelease ? JobMode.LatestPrerelease : JobMode.Latest };
            jobs.Add(job);
            StartPump();
            return job;
        }

        /// <summary>The newest job of a catalog entry, or null.</summary>
        [HideFromIl2Cpp]
        public SyncJob JobOf(byte catalogId) {
            SyncJob newest = null;
            foreach (var j in jobs) if (j.Catalog.Id == catalogId) newest = j;
            return newest;
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
            if (AnySucceeded) UTSRejoin.RememberCurrentLobby(forRestart: true);
        }

        [HideFromIl2Cpp]
        private IEnumerator CoRunJob(SyncJob job) {
            job.State = JobState.Working;
            job.Progress = 0f;

            // Pinned entries (Submerged): never fetch any other version, whatever the row said. A
            // "latest" job on a pinned entry simply means the pinned version.
            if (job.Mode != JobMode.Exact && job.Catalog.PinnedVersion != null) {
                job.Mode = JobMode.Exact;
                job.TargetVersion = job.Catalog.PinnedVersion;
            }
            if (job.Mode == JobMode.Exact && !job.Catalog.AllowsVersion(job.TargetVersion)) {
                Fail(job, "uts.modsync.error_no_matching_release");
                yield break;
            }

            // ---- 1. release list (URL built from the catalog, never received) ----
            // Our own mods go without the REST API (60 requests per hour, see ReleaseFeed): an exact
            // version is one known download URL (tags are vX.Y.Z / vX.Y.Z.W), "the newest" comes from
            // the Atom feed. Submerged keeps the API: its tag scheme is not ours.
            List<GithubRelease> releases = null;
            if (!job.Catalog.External && job.Mode == JobMode.Exact) {
                releases = new List<GithubRelease> { Synthetic(job.Catalog, "v" + TagVersion(job.TargetVersion)) };
            } else if (!job.Catalog.External) {
                var feed = new UnityWebRequest();
                feed.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
                feed.SetUrl(ReleaseFeed.Url(job.Catalog.RepositoryOwner, job.Catalog.RepositoryName));
                feed.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
                feed.downloadHandler = new DownloadHandlerBuffer();
                feed.timeout = 15; // without a limit a hanging request kept the pump (running) busy for good
                var fop = feed.SendWebRequest();
                while (!fop.isDone) yield return new WaitForEndOfFrame();
                try {
                    if (!feed.isNetworkError && !feed.isHttpError) {
                        var entries = ReleaseFeed.Parse(feed.downloadHandler.text);
                        if (entries.Count > 0) releases = entries.Select(e => Synthetic(job.Catalog, e.Tag)).ToList();
                    }
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] {job.Catalog.DisplayName}: release feed unreadable ({ex.Message}), asking the API.");
                } finally {
                    feed.downloadHandler.Dispose(); feed.Dispose();
                }
            }

            if (releases == null) {
                var www = new UnityWebRequest();
                www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
                www.SetUrl(job.Catalog.ReleasesApiUrl);
                // GitHub rejects clients without a User-Agent (same fix as UsefulTORStuffUpdater).
                www.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
                www.downloadHandler = new DownloadHandlerBuffer();
                www.timeout = 15;
                var op = www.SendWebRequest();
                while (!op.isDone) yield return new WaitForEndOfFrame();

                if (www.isNetworkError || www.isHttpError) {
                    www.downloadHandler.Dispose(); www.Dispose();
                    Fail(job, "uts.modsync.error_network");
                    yield break;
                }

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
            }

            if (releases == null || releases.Count == 0) {
                Fail(job, "uts.modsync.error_releases");
                yield break;
            }

            // ---- 2. the release: the host's EXACT version, or the newest of the wanted channel ----
            // Channel from the tag format, as in UsefulTORStuffUpdater: stable = vX.Y.Z, test = vX.Y.Z.W.
            // "Latest prerelease" takes the newest of BOTH channels so it never falls behind a stable.
            GithubRelease target = null;
            foreach (var r in releases) {
                if (r == null || r.Draft) continue;
                Version v;
                try { v = r.Version; } catch { continue; }   // tags that are not versions at all
                if (r.Assets == null || !r.Assets.Any(a => a != null && a.Name == job.Catalog.AssetName)) continue;
                if (job.Mode == JobMode.Exact) {
                    if (UsefulTORStuffUpdater.SemCompare(v, job.TargetVersion) == 0) { target = r; break; }
                    continue;
                }
                if (job.Mode == JobMode.Latest && v.Revision > 0) continue;
                if (target == null || UsefulTORStuffUpdater.SemCompare(v, target.Version) > 0) target = r;
            }
            if (target == null) {
                Fail(job, "uts.modsync.error_no_matching_release");
                yield break;
            }
            if (job.Mode != JobMode.Exact) {
                job.TargetVersion = target.Version;
                // nothing to do when that version is already installed (running or switched off)
                var local = UTSModCatalog.StateOf(job.Catalog, out var localVersion);
                if (local != LocalModState.Missing && localVersion != null
                    && UsefulTORStuffUpdater.SemCompare(localVersion, job.TargetVersion) == 0) {
                    job.NoChange = true;
                    job.Progress = 1f;
                    job.State = JobState.Done;
                    yield break;
                }
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
            string filePath = job.Catalog.WriteTargetPath;
            byte[] data = dl.downloadHandler.data;
            dl.downloadHandler.Dispose(); dl.Dispose();

            // Nothing replaces a working DLL before the download is shown to be whole: the size GitHub
            // announced and a PE header ("MZ") (audit 04.10.).
            if (data == null || data.Length < 2 || data[0] != (byte)'M' || data[1] != (byte)'Z'
                || (asset.Size > 0 && data.Length != asset.Size)) {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[ModSync] {job.Catalog.DisplayName}: download incomplete or not a DLL ({data?.Length ?? 0} of {asset.Size} bytes).");
                Fail(job, "uts.modsync.error_download");
                yield break;
            }

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

            if (persist.IsFaulted || persist.IsCanceled) {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[ModSync] {job.Catalog.DisplayName}: write failed - {persist.Exception?.Message ?? "canceled"}");
                // Put the previous DLL back, otherwise a failed replace leaves the player with no mod.
                // A write that failed midway has already created a truncated file, which has to go
                // first; checking "no file there" alone kept the broken DLL and left the good one as .old.
                try {
                    if (File.Exists(filePath)) File.Delete(filePath);
                    if (moved) File.Move(filePath + ".old", filePath);
                } catch { }
                Fail(job, "uts.modsync.error_write");
                yield break;
            }

            job.State = JobState.Done;
            UTSModSync.MarkFetched(job.Catalog.Id);
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[ModSync] installed {job.Catalog.DisplayName} v{job.TargetVersion} -> {filePath} (restart required).");
        }

        // A release as the feed or a known version describes it: the tag and the catalog's file at
        // the github.com download URL (no size known; the MZ check below still guards the write).
        [HideFromIl2Cpp]
        private static GithubRelease Synthetic(CatalogEntry entry, string tag) => new GithubRelease {
            Tag = tag,
            Assets = new List<GithubAsset> { new GithubAsset {
                Name = entry.AssetName,
                DownloadUrl = ReleaseFeed.DownloadUrl(entry.RepositoryOwner, entry.RepositoryName, tag, entry.AssetName) } }
        };

        // our tags: three parts, the fourth only on test builds
        [HideFromIl2Cpp]
        private static string TagVersion(Version v) =>
            v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

        [HideFromIl2Cpp]
        private static void Fail(SyncJob job, string errorKey) {
            job.State = JobState.Failed;
            job.ErrorKey = errorKey;
            UsefulTORStuffPlugin.Logger?.LogWarning(
                $"[ModSync] {job.Catalog.DisplayName}: job failed ({errorKey}).");
        }
    }
}
