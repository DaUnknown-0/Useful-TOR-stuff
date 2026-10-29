// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * StartupTiming - two log lines per game start, measured inside the game (the process knows its own start time
 * exactly; the BepInEx log has no timestamps):
 *   - when the main menu is on screen (the first rendered frame after MainMenuManager.Start), and after
 *     "Restart now" (UTSRestart) also how long since that click;
 *   - when the game really takes input: the end of the last frame stall over 150 ms, once the login has finished
 *     and five calm seconds followed, with every stall listed. Measured 15.10.: the menu appears after ~26 s, then
 *     the first hat lookup of the session loads TOR's whole hat pack in one frame (5.5 s), and the login only
 *     finishes after that.
 */

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    internal static class StartupTiming {
        private static bool done;
        internal static bool Ready { get; private set; }
        private const float Stall = 0.15f, Calm = 5f, Limit = 60f;

        private static void Postfix(MainMenuManager __instance) {
            if (done) return;
            done = true;
            BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, Measure());
        }

        private static double SinceStart() {
            try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds; } catch { return -1; }
        }

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[Startup] " + s);

        private static IEnumerator Measure() {
            yield return new WaitForEndOfFrame();     // the first frame with the menu on screen
            double menu = SinceStart();
            string restart = "";
            double clickToStart = -1;
            try {
                string f = UTSRestart.ClickFile;
                if (File.Exists(f)) {
                    long ticks = long.Parse(File.ReadAllText(f).Trim());
                    File.Delete(f);
                    double s = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
                    if (s > 0 && s < 600) { restart = $"; {s:F1} s after \"Restart now\" was clicked"; clickToStart = s - menu; }
                }
            } catch { }
            Log($"main menu on screen {menu:F1} s after Among Us.exe was started{restart}");

            float t0 = Time.realtimeSinceStartup, last = t0, lastStall = t0;
            double login = -1;
            var stalls = new StringBuilder();
            while (Time.realtimeSinceStartup - t0 < Limit) {
                yield return null;
                float now = Time.realtimeSinceStartup, dt = now - last;
                last = now;
                if (dt > Stall) { stalls.Append($" {SinceStart() - dt:F1}s+{dt * 1000:F0}ms"); lastStall = now; }
                if (login < 0) {
                    try { if (EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow()) login = SinceStart(); } catch { }
                }
                if (login >= 0 && !StartupHats.Loading && now - lastStall > Calm) break;
            }
            double ready = SinceStart() - (Time.realtimeSinceStartup - lastStall);
            Ready = true;
            string click = clickToStart >= 0 ? $" ({ready + clickToStart:F1} s after \"Restart now\")" : "";
            Log($"takes input {ready:F1} s after Among Us.exe was started{click}; login {(login >= 0 ? login.ToString("F1") + " s" : "not finished")}; "
                + $"stalls over {Stall * 1000:F0} ms:{(stalls.Length > 0 ? stalls.ToString() : " none")}");
        }
    }
}
