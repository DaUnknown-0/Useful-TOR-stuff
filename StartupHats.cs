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

        private static HatData Register(CustomHat hat) {
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
