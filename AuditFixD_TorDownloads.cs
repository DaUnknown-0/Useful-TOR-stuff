using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using TheOtherRoles.Modules;
using Twitch;
using UnityEngine;
using UnityEngine.Networking;
using TorRelease = TheOtherRoles.Modules.GithubRelease;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-037/038: sichere Ersatz-Coroutinen, TOR-Quellen bleiben unverändert.
    internal static class AuditFixD_TorDownloads {
        private static readonly FieldInfo Busy = AccessTools.Field(typeof(ModUpdater), "_busy");

        private static bool Suitable(TorRelease r) => r != null && !r.Draft
            && Version.TryParse(r.Tag?.Replace("v", ""), out _)
            && r.Assets != null && r.Assets.Any(a => a?.Name == "TheOtherRoles.dll"
                && UTSModCatalog.IsTrustedAssetUrl(ModUpdater.RepositoryOwner, ModUpdater.RepositoryName, a.DownloadUrl));

        // Il2Cpp-UnityWebRequest implementiert kein verwaltetes IDisposable.
        internal sealed class RequestCleanup : IDisposable {
            private readonly UnityWebRequest request;
            internal RequestCleanup(UnityWebRequest value) { request = value; }
            public void Dispose() => request.Dispose();
        }

        private static UnityWebRequest Request(string url, int timeout) {
            var request = UnityWebRequest.Get(url);
            request.timeout = timeout;
            request.SetRequestHeader("User-Agent", "UsefulTORStuff/" + UsefulTORStuffPlugin.PluginVersion);
            return request;
        }

        // Auch Ausnahmen vor/nach einem yield und Dispose entsperren den Aufrufer.
        internal static IEnumerator Guard(IEnumerator inner, Action finish) {
            try {
                while (true) {
                    bool more;
                    try { more = inner.MoveNext(); }
                    catch (Exception ex) {
                        UsefulTORStuffPlugin.Logger?.LogWarning("[AuditFixD] Download abgebrochen: " + ex.GetBaseException().Message);
                        yield break;
                    }
                    if (!more) yield break;
                    yield return inner.Current;
                }
            } finally {
                try { (inner as IDisposable)?.Dispose(); }
                finally { finish(); }
            }
        }

        [HarmonyPatch(typeof(ModUpdater), "CoCheckForUpdate")]
        private static class CheckPatch {
            private static bool Prefix(ModUpdater __instance, ref IEnumerator __result) {
                __result = Guard(Check(__instance), () => Busy.SetValue(__instance, false));
                return false;
            }
        }

        private static IEnumerator Check(ModUpdater updater) {
            Busy.SetValue(updater, true);
            updater.Releases ??= new List<TorRelease>();
            var request = Request($"https://api.github.com/repos/{ModUpdater.RepositoryOwner}/{ModUpdater.RepositoryName}/releases", 20);
            using var cleanup = new RequestCleanup(request);
            var operation = request.SendWebRequest();
            while (!operation.isDone) yield return null;
            if (request.isNetworkError || request.isHttpError) yield break;
            // Ungültige Tags/Assets gelangen nicht in TORs ungeprüfte Menüauswertung.
            var list = JsonSerializer.Deserialize<List<TorRelease>>(request.downloadHandler.text);
            updater.Releases = (list ?? new List<TorRelease>()).Where(Suitable).OrderByDescending(r => r.Version).ToList();
        }

        [HarmonyPatch(typeof(ModUpdater), "OnSceneLoaded")]
        private static class MenuPatch {
            private static bool Prefix(ModUpdater __instance) => __instance.Releases?.Any(Suitable) == true;
        }

        private sealed class DownloadState {
            internal GenericPopup Popup;
            internal GameObject Button;
            internal bool Success;
        }

        [HarmonyPatch(typeof(ModUpdater), "CoDownloadRelease")]
        private static class DownloadPatch {
            private static bool Prefix(ModUpdater __instance, TorRelease release, ref IEnumerator __result) {
                var state = new DownloadState();
                __result = Guard(Download(__instance, release, state), () => {
                    Busy.SetValue(__instance, false);
                    if (state.Popup != null) state.Popup.TextAreaTMP.text = state.Success
                        ? "TOR wurde aktualisiert. Bitte das Spiel neu starten."
                        : "TOR-Update fehlgeschlagen. Die bisherige DLL bleibt erhalten. Erneut versuchen oder manuell aktualisieren.";
                    if (state.Button != null) state.Button.SetActive(true);
                });
                return false;
            }
        }

        private static IEnumerator Download(ModUpdater updater, TorRelease release, DownloadState state) {
            Busy.SetValue(updater, true);
            if (TwitchManager.Instance != null && TwitchManager.Instance.TwitchPopup != null) {
                state.Popup = UnityEngine.Object.Instantiate(TwitchManager.Instance.TwitchPopup);
                state.Popup.Show();
                state.Popup.TextAreaTMP.text = "TOR-Update wird heruntergeladen ...";
                state.Button = state.Popup.transform.GetChild(2).gameObject;
                state.Button.SetActive(false);
            }
            if (!Suitable(release)) yield break;
            var asset = release.Assets.First(a => a?.Name == "TheOtherRoles.dll"
                && UTSModCatalog.IsTrustedAssetUrl(ModUpdater.RepositoryOwner, ModUpdater.RepositoryName, a.DownloadUrl));
            byte[] bytes;
            var request = Request(asset.DownloadUrl, 300);
            using (new RequestCleanup(request)) {
                var operation = request.SendWebRequest();
                while (!operation.isDone) yield return null;
                if (request.isNetworkError || request.isHttpError) yield break;
                bytes = request.downloadHandler.data;
            }
            if (!UnknownsNet.IsPluginImage(bytes) || (asset.Size > 0 && bytes.Length != asset.Size)) yield break;
            string path = typeof(ModUpdater).Assembly.Location;
            string root = Path.GetFullPath(BepInEx.Paths.PluginPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.IsNullOrEmpty(path) || !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(BepInEx.Paths.PluginPath, "TheOtherRoles.dll");
            var persist = AuditFixD_PluginInstall.Install(path, bytes, release.Version);
            while (!persist.IsCompleted) yield return null;
            if (!persist.IsCompletedSuccessfully) throw persist.Exception?.GetBaseException() ?? new IOException("DLL konnte nicht geschrieben werden.");
            state.Success = true;
        }
    }
}
