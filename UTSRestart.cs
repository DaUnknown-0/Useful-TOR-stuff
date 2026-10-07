// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSRestart - "restart now" for everything that only takes effect after a restart: downloads from
 * the Mod Manager, Mod Sync and modpacks, and mods switched on or off.
 *
 * Restarting = start a detached cmd that waits about two seconds and launches the game executable
 * again, then quit. The wait lets this process release its files (BepInEx log, the DLLs that were
 * just replaced) before the new one opens them. The child inherits this process' environment, so a
 * game started through "Among Us (no tiering).cmd" comes back with the same settings, minus
 * Doorstop's own DOORSTOP_* markers (with them the new game would start without mods). Before
 * quitting, the current lobby is remembered so the main menu can offer the way back (UTSRejoin).
 */

using System;
using System.Diagnostics;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace UsefulTORStuff {

    public static class UTSRestart {
        // set by changes this class cannot see from outside (modpacks applied, Submerged renamed)
        private static bool marked;

        public static void Mark() => marked = true;

        /// <summary>True when something waits for the next game start.</summary>
        public static bool Needed() {
            if (marked || UTSModSync.AnythingFetched) return true;
            try {
                var dl = UTSModDownloader.Instance;
                if (dl != null && dl.Jobs.Any(j => j.State == JobState.Done && !j.NoChange)) return true;
            } catch { }
            try {
                foreach (var m in ModManagerRegistry.GetAllMods()) {
                    if (m == null) continue;
                    int st = 0;
                    try { st = m.GetUpdateState?.Invoke() ?? 0; } catch { }
                    if (st == 2) return true;
                    if (m.Enabled != null && m.Enabled.Value != m.RuntimeEnabled) return true;
                }
            } catch { }
            return false;
        }

        /// <summary>A download is still running, so a restart now would cut it off.</summary>
        public static bool Busy() {
            try {
                var dl = UTSModDownloader.Instance;
                if (dl != null && dl.IsRunning) return true;
                foreach (var m in ModManagerRegistry.GetAllMods()) {
                    int st = 0;
                    try { st = m?.GetUpdateState?.Invoke() ?? 0; } catch { }
                    if (st == 1) return true;
                }
            } catch { }
            return false;
        }

        // Autotest: a file BepInEx\restart_test.flag makes the main menu restart once. The file is
        // deleted first, so the restarted game does not restart again.
        [HarmonyLib.HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class RestartTestPatch {
            private static void Postfix(MainMenuManager __instance) {
                string flag = System.IO.Path.Combine(Paths.BepInExRootPath, "restart_test.flag");
                if (!System.IO.File.Exists(flag)) return;
                try { System.IO.File.Delete(flag); } catch { return; }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[Restart] test: restarting from the main menu, TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "-"}");
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, Later());
            }

            private static System.Collections.IEnumerator Later() {
                yield return new WaitForSeconds(3f);
                Now();
            }
        }

        public static void Now() {
            try {
                try { UTSRejoin.RememberCurrentLobby(forRestart: true); } catch { }
                string exe = Paths.ExecutablePath;
                var psi = new ProcessStartInfo("cmd.exe",
                    $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exe}\"") {
                    WorkingDirectory = Paths.GameRootPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                // Doorstop (BepInEx' loader) marks this process with DOORSTOP_INITIALIZED and friends;
                // a child that inherits them is taken for a sub-process and starts WITHOUT mods (the
                // first "Restart now" opened plain Among Us, 2026-10-07). Everything else is kept, so
                // DOTNET_TieredCompilation from the no-tiering starter survives.
                var drop = new System.Collections.Generic.List<string>();
                foreach (string key in psi.EnvironmentVariables.Keys)
                    if (key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)) drop.Add(key);
                foreach (var key in drop) psi.EnvironmentVariables.Remove(key);
                Process.Start(psi);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[Restart] relaunching {exe} in about 2 s.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[Restart] relaunch failed, quitting only: {e.Message}");
            }
            Application.Quit();
        }
    }
}
