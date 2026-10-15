// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * RestartShortcuts - work a game started by the Mod Manager's "Restart now" may skip or bring forward, because
 * the game before it did it minutes ago (UTSRestart.IsRestart; user, 15.10.: only for that restart, never for
 * a game opened by hand). Hat cache: HatTextureDiet.
 *
 *   - TOR checks the MD5 of every hat file against its manifest at each start (CustomHatManager
 *     .GenerateDownloadList, ~1000 files in one frame). The game that restarted us checked them already; a
 *     restart downloads nothing and the next normal start checks again.
 *   - TOR loads all hats in one frame on the session's first HatManager.GetHatById, which came about 8 s after
 *     the menu, right before the login finished (measured 15.10.: the login waited for it). A restart asks for
 *     a hat as soon as the menu is there and TOR's manifest is in, so that frame falls into the time the game
 *     waits for the login anyway.
 *   - The login flow waits about 5 s for the cloud write of player.prefs (EOSManager.WaitForStorageToSave);
 *     the read side reports that file NotFound at every start, so the game writes defaults each time. A
 *     restart skips that write while the login flow runs (found by Fable, 15.10.); the saves right after
 *     the login run as usual. Ending the wait coroutine from outside left the login hanging.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    internal static class RestartShortcuts {
        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[Restart] " + s);

        [HarmonyPatch]
        private static class SkipHatHashes {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(TorType.Get("TheOtherRoles.Modules.CustomHats.CustomHatManager"), "GenerateDownloadList");

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(ref List<string> __result) {
                if (!UTSRestart.IsRestart) return true;
                __result = new List<string>();
                Log("hat files: MD5 check skipped (checked by the game before the restart)");
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerStorageManager), nameof(PlayerStorageManager.SaveChanges))]
        private static class SkipLoginCloudSave {
            private static int skipped;

            private static bool Prefix() {
                if (!UTSRestart.IsRestart) return true;
                try {
                    if (!EOSManager.InstanceExists || EOSManager.Instance.HasFinishedLoginFlow()) return true;
                } catch { return true; }
                if (skipped++ == 0) Log("login: cloud write of player.prefs skipped, the login does not wait for it");
                return false;
            }
        }

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class LoadHatsEarly {
            private static bool started;

            private static void Postfix(MainMenuManager __instance) {
                if (started || !UTSRestart.IsRestart) return;
                started = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, Run());
            }

            private static IEnumerator Run() {
                FieldInfo pending = null;
                try { pending = AccessTools.Field(TorType.Get("TheOtherRoles.Modules.CustomHats.CustomHatManager"), "UnregisteredHats"); } catch { }
                if (pending == null) { Log("hats early: TOR's hat list not found"); yield break; }
                float t0 = Time.realtimeSinceStartup;
                // TOR's manifest coroutine fills the list; until then there is nothing to load
                while (Time.realtimeSinceStartup - t0 < 30f) {
                    int n = 0;
                    try { n = (pending.GetValue(null) as System.Collections.ICollection)?.Count ?? 0; } catch { }
                    if (n > 0 && HatManager.InstanceExists) break;
                    yield return null;
                }
                if (!HatManager.InstanceExists) { Log("hats early: no HatManager"); yield break; }
                long s = System.Diagnostics.Stopwatch.GetTimestamp();
                try { HatManager.Instance.GetHatById("hat_NoHat"); } catch (Exception e) { Log("hats early: " + e.Message); yield break; }
                Log($"hats early: loaded {(System.Diagnostics.Stopwatch.GetTimestamp() - s) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, "
                    + $"{Time.realtimeSinceStartup - t0:F1} s after the menu");
            }
        }
    }
}
