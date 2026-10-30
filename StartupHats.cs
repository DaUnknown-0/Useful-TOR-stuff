using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles.Modules.CustomHats;
using UnityEngine;

namespace UsefulTORStuff {
    // Replace only TOR's synchronous registration prefix; GetHatById itself still runs normally.
    internal static class StartupHats {
        private static readonly FieldInfo PendingField = AccessTools.Field(typeof(CustomHatManager), "UnregisteredHats");
        private static readonly MethodInfo Create = AccessTools.Method(typeof(CustomHatManager), "CreateHatBehaviour");
        private static List<CustomHat> Pending => PendingField?.GetValue(null) as List<CustomHat>;
        private static bool active, busy;
        private static int registered;
        private static double worstMs;
        private static readonly Dictionary<CustomHat, float> retryAt = new();
        internal static bool Loading => active && (Pending?.Count ?? 0) > 0;

        [HarmonyPatch]
        private static class ReplaceBulkLoad {
            private static MethodBase TargetMethod() => AccessTools.Method(
                typeof(CustomHatManager).Assembly.GetType("TheOtherRoles.Modules.CustomHats.Patches.HatManagerPatches"), "GetHatByIdPrefix");
            private static bool Prepare() { active = PendingField != null && Create != null && TargetMethod() != null; return active; }
            private static bool Prefix() => !active;
        }

        // ---- fixed package order in the hats tab ----------------------------------------------------
        // TOR lists the packages in the order their first hat appears in GetUnlockedHats, i.e. registration order. Loading
        // step by step (and registering a requested hat at once) shuffled that from start to start, so Unknown's Collection
        // sometimes stood at the top (User 29.10.). Now: TOR's own packages in their original order (snapshot of the
        // pending list before the first registration), then Unknown's Collection, then other "Unknown's" packages, and
        // Unknown's Collective last. Hats inside a package keep TOR's sorting (by name).
        private static Dictionary<string, int> originalOrder;

        private static void Snapshot() {
            if (originalOrder != null) return;
            var pending = Pending;
            if (pending == null || pending.Count == 0) return;
            originalOrder = new Dictionary<string, int>();
            for (int i = 0; i < pending.Count; i++) {
                var h = pending[i];
                if (h?.Name == null) continue;
                string id = "hat_" + h.Name.Replace(' ', '_');
                if (!originalOrder.ContainsKey(id)) originalOrder[id] = i;
            }
        }

        // TOR's HatData.GetHatExtension and its ExtensionCache are internal; the cache is read by reflection.
        private static readonly FieldInfo ExtensionField = AccessTools.Field(typeof(CustomHatManager), "ExtensionCache");

        private static string PackageOf(HatData h) {
            try {
                if (h == null || ExtensionField?.GetValue(null) is not Dictionary<string, HatExtension> cache) return "";
                return cache.TryGetValue(h.name, out var ext) && ext != null ? ext.Package ?? "" : "";
            } catch { return ""; }
        }

        private static int Rank(string package) {
            if (package == "Unknown's Collection") return 1;
            if (package == "Unknown's Collective") return 3;
            if (package != null && package.StartsWith("Unknown's ")) return 2;
            return 0;
        }

        [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetUnlockedHats))]
        private static class PackageOrder {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<HatData> __result) {
                try {
                    if (__result == null || __result.Length < 2) return;
                    var hats = new List<HatData>(__result.Length);
                    foreach (var h in __result) hats.Add(h);
                    // package -> (rank, position of its first hat in the original order)
                    var first = new Dictionary<string, (int rank, int pos)>();
                    for (int i = 0; i < hats.Count; i++) {
                        var h = hats[i];
                        string pkg = PackageOf(h);
                        int pos = originalOrder != null && h != null && originalOrder.TryGetValue(h.ProductId ?? "", out int o) ? o : i + 100000;
                        if (!first.TryGetValue(pkg, out var f) || pos < f.pos) first[pkg] = (Rank(pkg), pos);
                    }
                    var sorted = hats.Select((h, i) => (h, i, key: first[PackageOf(h)]))
                        .OrderBy(x => x.key.rank).ThenBy(x => x.key.pos).ThenBy(x => x.i).Select(x => x.h).ToArray();
                    __result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<HatData>(sorted);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[StartupHats] package order failed: {e.Message}");
                }
            }
        }

        private static HatData Register(CustomHat hat) {
            Snapshot();
            try {
                var result = (HatData)Create.Invoke(null, new object[] { hat, false });
                Pending.Remove(hat);
                retryAt.Remove(hat);
                registered++;
                return result;
            } catch {
                // Downloads may still be in flight. Keep the descriptor and retry later.
                retryAt[hat] = Time.realtimeSinceStartup + 1f;
                return null;
            }
        }

        private static void Publish(HatManager manager, List<HatData> added) {
            if (added.Count == 0) return;
            var all = manager.allHats.ToList();
            all.AddRange(added);
            manager.allHats = all.ToArray();
            published++;
        }

        // ---- hats tab opened while hats are still coming in -------------------------------------------
        // User 30.10. (screenshot, "manche skins fehlen, wenn sie beim join nicht geladen waren"): TOR builds the hats tab
        // once in OnEnable from the hats registered at that moment. Opened during the incremental load (or before a mod's
        // late hats, e.g. Halloween costumes after their download), the missing hats never showed up until the tab was
        // opened again. Now an open tab rebuilds itself when new hats arrived: once the load is through, or at most every
        // 2 s while it still runs.
        private static int published, tabBuiltAt;
        private static float lastRebuild;

        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.OnEnable))]
        private static class TabBuilt {
            [HarmonyPriority(Priority.First)]
            private static void Postfix() { tabBuiltAt = published; lastRebuild = Time.realtimeSinceStartup; }
        }

        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.Update))]
        private static class TabRefresh {
            private static void Postfix(HatsTab __instance) {
                if (tabBuiltAt == published || __instance == null || !__instance.isActiveAndEnabled) return;
                bool done = (Pending?.Count ?? 0) == 0;
                if (!done && Time.realtimeSinceStartup - lastRebuild < 2f) return;
                try {
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[StartupHats] hats tab rebuilt: {published - tabBuiltAt} batch(es) of hats arrived while it was open");
                    __instance.OnEnable();
                } catch (Exception e) {
                    tabBuiltAt = published;
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[StartupHats] hats tab rebuild failed: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetHatById))]
        private static class RequestedHat {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(HatManager __instance, string __0) {
                if (!active || busy || __0 == null || !__0.StartsWith("hat_")) return;
                var hat = Pending?.FirstOrDefault(h => "hat_" + h.Name.Replace(' ', '_') == __0);
                if (hat == null) return;
                busy = true;
                try { var data = Register(hat); if (data != null) Publish(__instance, new List<HatData> { data }); }
                finally { busy = false; }
            }
        }

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class StartLoading {
            private static bool started;
            private static void Postfix(MainMenuManager __instance) {
                if (!active || started) return;
                started = true;
                var owner = DraftMusicDriver.Instance;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(owner != null ? owner : (MonoBehaviour)__instance, Run());
            }
        }

        private static IEnumerator Run() {
            float next = 0f, spacing = 0f;
            bool batch = false;
            while (true) {
                yield return null;
                if (!active || busy || !HatManager.InstanceExists) continue;
                var pending = Pending;
                if (pending == null || pending.Count == 0) {
                    if (batch) UsefulTORStuffPlugin.Logger?.LogInfo($"[StartupHats] loaded {registered} hats incrementally; largest batch {worstMs:F1} ms");
                    batch = false;
                    continue;
                }
                float now = Time.realtimeSinceStartup;
                if (!batch) { batch = true; spacing = 10f / pending.Count; next = now; }
                if (now < next) continue;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var added = new List<HatData>();
                busy = true;
                try {
                    // No catch-up burst after a stalled frame. At most 4 ms of work, then yield.
                    int count = Math.Max(1, (int)Math.Ceiling(Time.unscaledDeltaTime / spacing));
                    foreach (var hat in pending.ToArray()) {
                        if (retryAt.TryGetValue(hat, out float retry) && now < retry) continue;
                        var data = Register(hat);
                        if (data != null) added.Add(data);
                        if (--count <= 0 || timer.Elapsed.TotalMilliseconds >= 4) break;
                    }
                    Publish(HatManager.Instance, added);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[StartupHats] batch failed: {e.Message}");
                } finally { busy = false; }
                worstMs = Math.Max(worstMs, timer.Elapsed.TotalMilliseconds);
                next = now + spacing;
            }
        }
    }
}
