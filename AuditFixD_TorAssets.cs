using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Modules.CustomHats;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

namespace UsefulTORStuff {
    internal static class AuditFixD_TorAssets {
        private static readonly FieldInfo Running = AccessTools.Field(typeof(HatsLoader), "isRunning");
        private static readonly FieldInfo Pending = AccessTools.Field(typeof(CustomHatManager), "UnregisteredHats");
        private static readonly FieldInfo Views = AccessTools.Field(typeof(CustomHatManager), "ViewDataCache");
        private static readonly FieldInfo Extensions = AccessTools.Field(typeof(CustomHatManager), "ExtensionCache");
        private static string HatsDirectory => (string)AccessTools.Property(typeof(CustomHatManager), "HatsDirectory").GetValue(null);
        private const string HatRepository = "https://raw.githubusercontent.com/TheOtherRolesAU/TheOtherHats/master";

        // Audit 16.11. TOR-040: garantiertes Cleanup und begrenzte Wiederholung statt dauerhaftem Latch.
        [HarmonyPatch(typeof(HatsLoader), "CoFetchHats")]
        private static class FetchPatch {
            private static bool Prefix(HatsLoader __instance, ref IEnumerator __result) {
                __result = AuditFixD_TorDownloads.Guard(Fetch(__instance), () => Running.SetValue(__instance, false));
                return false;
            }
        }

        private static IEnumerator Fetch(HatsLoader loader) {
            Running.SetValue(loader, true);
            List<CustomHat> hats = null;
            for (int attempt = 0; attempt < 2 && hats == null; attempt++) {
                var request = UnityWebRequest.Get(HatRepository + "/CustomHats.json");
                using var cleanup = new AuditFixD_TorDownloads.RequestCleanup(request);
                request.timeout = 20;
                var operation = request.SendWebRequest();
                while (!operation.isDone) yield return null;
                if (request.isNetworkError || request.isHttpError) continue;
                try {
                    var manifest = JsonSerializer.Deserialize<SkinsConfigFile>(request.downloadHandler.text,
                        new JsonSerializerOptions { AllowTrailingCommas = true });
                    if (manifest?.Hats == null) continue;
                    hats = (List<CustomHat>)AccessTools.Method(typeof(CustomHatManager), "SanitizeHats").Invoke(null, new object[] { manifest });
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[AuditFixD] Hutmanifest ungültig: " + ex.GetBaseException().Message);
                }
            }
            if (hats == null) yield break;
            Directory.CreateDirectory(HatsDirectory);
            var downloads = (List<string>)AccessTools.Method(typeof(CustomHatManager), "GenerateDownloadList").Invoke(null, new object[] { hats });
            if (TheOtherRoles.Utilities.EventUtility.isEnabled) hats.AddRange(CustomHatManager.loadHorseHats());
            var pending = (List<CustomHat>)Pending.GetValue(null);
            var views = (Dictionary<string, HatViewData>)Views.GetValue(null);
            foreach (var hat in hats)
                if (!views.ContainsKey(hat.Name) && !pending.Any(p => p.Name == hat.Name)) pending.Add(hat);
            foreach (string file in downloads.Distinct()) {
                // Manifestdateinamen bleiben innerhalb des Hutordners.
                if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) continue;
                for (int attempt = 0; attempt < 2; attempt++) {
                    byte[] bytes;
                    var request = UnityWebRequest.Get(HatRepository + "/hats/" + Uri.EscapeDataString(file));
                    using (new AuditFixD_TorDownloads.RequestCleanup(request)) {
                        request.timeout = 30;
                        var operation = request.SendWebRequest();
                        while (!operation.isDone) yield return null;
                        if (request.isNetworkError || request.isHttpError) continue;
                        bytes = request.downloadHandler.data;
                    }
                    string path = Path.Combine(HatsDirectory, file);
                    var write = System.Threading.Tasks.Task.Run(() => {
                        string temp = path + ".part";
                        try {
                            File.WriteAllBytes(temp, bytes);
                            File.Move(temp, path, true);
                        } finally { try { File.Delete(temp); } catch { } }
                    });
                    while (!write.IsCompleted) yield return null;
                    if (write.IsCompletedSuccessfully) break;
                    UsefulTORStuffPlugin.Logger?.LogWarning("[AuditFixD] Hutdatei konnte nicht gespeichert werden: " + write.Exception?.GetBaseException().Message);
                }
            }
        }

        // Der erste TOR-Abruf beginnt vor UTS.Load. Dessen bereits erzeugten Iterator ebenfalls absichern.
        [HarmonyPatch]
        private static class InitialFetchPatch {
            private static readonly Dictionary<IntPtr, (UnityWebRequest Request, float Deadline)> requests = new();
            private static readonly HashSet<object> roots = new();
            private static IEnumerable<MethodBase> TargetMethods() => typeof(HatsLoader).GetNestedTypes(BindingFlags.NonPublic)
                .Where(t => t.Name.Contains("<CoFetchHats>") || t.Name.Contains("<CoDownloadHatAsset>"))
                .Select(t => (MethodBase)AccessTools.Method(t, "MoveNext"));

            private static void Prefix(object __instance) {
                foreach (var field in __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (field.GetValue(__instance) is HatsLoader) roots.Add(__instance);
                    if (field.GetValue(__instance) is UnityWebRequest request) {
                        // Der erste Request wurde vor UTS.Load bereits gesendet. Nicht nachträglich konfigurieren.
                        try {
                            if (!request.isDone && !requests.ContainsKey(request.Pointer))
                                requests[request.Pointer] = (request, Time.realtimeSinceStartup + 30f);
                        } catch { }
                    }
                }
            }

            internal static void ExpireRequests() {
                foreach (var pair in requests.ToArray()) {
                    try {
                        if (!pair.Value.Request.isDone && Time.realtimeSinceStartup < pair.Value.Deadline) continue;
                        if (!pair.Value.Request.isDone) pair.Value.Request.Abort();
                    } catch { }
                    requests.Remove(pair.Key);
                }
                // Der Abschluss eines bereits beobachteten Iterators bleibt auch bei Detour-Verlust erkennbar.
                foreach (var root in roots) {
                    var state = AccessTools.Field(root.GetType(), "<>1__state");
                    if (state?.GetValue(root) is not int value || value >= 0) continue;
                    foreach (var field in root.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (field.GetValue(root) is HatsLoader loader) Running.SetValue(loader, false);
                }
            }

            internal static void Clear() { requests.Clear(); roots.Clear(); }

            private static Exception Finalizer(object __instance, bool __result, Exception __exception) {
                if (__result && __exception == null) return null;
                foreach (var field in __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    var value = field.GetValue(__instance);
                    if (value is HatsLoader loader) Running.SetValue(loader, false);
                    if (value is UnityWebRequest request) { try { request.Dispose(); } catch { } }
                }
                return __exception;
            }
        }

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class InitialRetryPatch {
            private static bool started;
            private static void Postfix(MainMenuManager __instance) {
                if (started) return;
                started = true;
                __instance.StartCoroutine(RetryInitial());
            }
        }

        private static IEnumerator RetryInitial() {
            var loader = (HatsLoader)AccessTools.Field(typeof(CustomHatManager), "Loader").GetValue(null);
            if (loader == null) yield break;
            while ((bool)Running.GetValue(loader)) {
                InitialFetchPatch.ExpireRequests();
                yield return null;
            }
            InitialFetchPatch.Clear();
            // Nur wiederholen, wenn der erste Abruf nichts geliefert hat. Läuft dabei TORs eigenes CoFetchHats (Patch nicht
            // aktiv), hängt es alle Hüte ein zweites Mal an UnregisteredHats: jeder Hut stand doppelt im Hut-Tab (1.7.6.1 bis 1.7.7).
            var pending = (List<CustomHat>)Pending.GetValue(null);
            var views = (Dictionary<string, HatViewData>)Views.GetValue(null);
            if ((pending?.Count ?? 0) > 0 || (views?.Count ?? 0) > 0) yield break;
            loader.FetchHats();
        }

        // Audit 16.11. TOR-042: Pflichtgrafik vor Objektanlage prüfen; Fehler geben alles Angelegte frei.
        [HarmonyPatch(typeof(CustomHatManager), "CreateHatBehaviour")]
        private static class CreateHatPatch {
            private static bool Prefix(CustomHat ch, bool testOnly, ref HatData __result) {
                var sprites = new List<Sprite>();
                HatData hat = null;
                HatViewData view = null;
                Sprite Load(string path) {
                    if (path == null) return null;
                    var sprite = (Sprite)AccessTools.Method(typeof(CustomHatManager), "CreateHatSprite").Invoke(null, new object[] { path });
                    if (sprite != null) sprites.Add(sprite);
                    return sprite;
                }
                try {
                    var main = Load(ch.Resource);
                    if (main == null) throw new FileNotFoundException("Hutgrafik noch nicht heruntergeladen.");
                    view = ScriptableObject.CreateInstance<HatViewData>();
                    hat = ScriptableObject.CreateInstance<HatData>();
                    view.MainImage = view.FloorImage = main;
                    if (ch.BackResource != null) { view.BackImage = Load(ch.BackResource); ch.Behind = true; }
                    if (ch.ClimbResource != null) view.LeftClimbImage = view.ClimbImage = Load(ch.ClimbResource);
                    hat.name = ch.Name;
                    hat.displayOrder = 99;
                    hat.ProductId = "hat_" + ch.Name.Replace(' ', '_');
                    hat.InFront = !ch.Behind;
                    hat.NoBounce = !ch.Bounce;
                    hat.ChipOffset = new Vector2(0f, 0.2f);
                    hat.Free = true;
                    var extension = new HatExtension {
                        Author = ch.Author ?? "Unknown", Package = ch.Package ?? "Misc.", Condition = ch.Condition ?? "none",
                        Adaptive = ch.Adaptive, FlipImage = Load(ch.FlipResource), BackFlipImage = Load(ch.BackFlipResource)
                    };
                    hat.ViewDataRef = new AssetReference(view.Pointer);
                    hat.CreateAddressableAsset();
                    ((Dictionary<string, HatViewData>)Views.GetValue(null))[ch.Name] = view;
                    if (testOnly) {
                        extension.Condition = hat.name;
                        AccessTools.Property(typeof(CustomHatManager), "TestExtension").SetValue(null, extension);
                    } else ((Dictionary<string, HatExtension>)Extensions.GetValue(null))[ch.Name] = extension;
                    __result = hat;
                    return false;
                } catch {
                    if (view != null) UnityEngine.Object.Destroy(view);
                    if (hat != null) UnityEngine.Object.Destroy(hat);
                    foreach (var sprite in sprites) {
                        if (sprite.texture != null) UnityEngine.Object.Destroy(sprite.texture);
                        UnityEngine.Object.Destroy(sprite);
                    }
                    throw;
                }
            }
        }

        // Audit 16.11. TOR-044: geschützte AudioClips einmal pro Prozess laden.
        [HarmonyPatch(typeof(SoundEffectsManager), nameof(SoundEffectsManager.Load))]
        private static class SoundsPatch {
            private static bool loaded;
            private static bool Prefix() => !loaded;
            private static void Postfix() => loaded = true;
        }
    }
}
