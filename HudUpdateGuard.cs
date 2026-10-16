// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    /// <summary>
    /// A player who kept dropping at the step from the Role Draft into the round (2026-10-16) had the
    /// game's own HudManager.Update throw a NullReferenceException in EVERY frame: the trace only says
    /// "at HudManager.Update ()", inside the game's native code, so it does not name what is null.
    /// Each throw crosses the native/managed trampoline and writes ~10 log lines, so a frame-rate
    /// exception costs a lot of time on a weak PC and leaves the HUD half updated.
    ///
    /// This finalizer catches the exception (the frame continues as if Update had returned), writes
    /// ONE diagnostic line per round naming which HUD parts and which local-player parts are null or
    /// destroyed, and afterwards only counts. Logging and containment only; it changes nothing the
    /// game would otherwise have done, because the rest of that Update was already being skipped.
    /// </summary>
    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    internal static class HudUpdateGuard {
        private static int count;
        private static bool reported;
        private static float nextSummary;

        [HarmonyFinalizer]
        public static Exception Finalizer(HudManager __instance, Exception __exception) {
            if (__exception == null) return null;
            try {
                count++;
                if (!reported) {
                    reported = true;
                    UsefulTORStuffPlugin.Logger?.LogWarning("[HudUpdateGuard] HudManager.Update threw (" + __exception.GetType().Name
                        + "), contained from now on. Null parts: " + Describe(__instance) + " | " + First(__exception));
                } else if (Time.realtimeSinceStartup >= nextSummary) {
                    nextSummary = Time.realtimeSinceStartup + 30f;
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[HudUpdateGuard] still throwing: {count} time(s) so far. Null parts now: {Describe(__instance)}");
                }
            } catch { }
            return null;   // swallowed: no per-frame trampoline error and log spam
        }

        private static string First(Exception e) {
            string s = e.Message ?? "";
            int i = s.IndexOf('\n');
            return i > 0 ? s.Substring(0, Math.Min(i, 160)) : (s.Length > 160 ? s.Substring(0, 160) : s);
        }

        private static string Describe(HudManager h) {
            var sb = new StringBuilder();
            void Check(string name, Func<UnityEngine.Object> get) {
                try { if (get() == null) sb.Append(name).Append(' '); }
                catch { sb.Append(name).Append("(throws) "); }
            }
            if (h == null) return "HudManager itself";
            Check("UseButton", () => h.UseButton);
            Check("PetButton", () => h.PetButton);
            Check("KillButton", () => h.KillButton);
            Check("ReportButton", () => h.ReportButton);
            Check("SabotageButton", () => h.SabotageButton);
            Check("ImpostorVentButton", () => h.ImpostorVentButton);
            Check("AbilityButton", () => h.AbilityButton);
            Check("Chat", () => h.Chat);
            Check("TaskPanel", () => h.TaskPanel);
            Check("MapButton", () => h.MapButton);
            Check("FullScreen", () => h.FullScreen);
            Check("LocalPlayer", () => PlayerControl.LocalPlayer);
            try {
                var lp = PlayerControl.LocalPlayer;
                if (lp != null) {
                    if (lp.Data == null) sb.Append("LocalPlayer.Data ");
                    else if (lp.Data.Role == null) sb.Append("LocalPlayer.Data.Role ");
                    if (lp.myTasks == null) sb.Append("myTasks ");
                    else {
                        int dead = 0, total = 0;
                        foreach (var t in lp.myTasks) { total++; if (t == null) dead++; }
                        if (dead > 0) sb.Append($"myTasks({dead} of {total} destroyed) ");
                    }
                }
            } catch { sb.Append("LocalPlayer(throws) "); }
            string phase = "?";
            try {
                var c = AmongUsClient.Instance;
                phase = c == null ? "menu" : c.GameState != InnerNet.InnerNetClient.GameStates.Started ? c.GameState.ToString()
                      : DraftMusic.DraftRunning() ? "role draft" : IntroScreen.Exists ? "intro" : MeetingHud.Instance != null ? "meeting" : "round";
            } catch { }
            return (sb.Length > 0 ? sb.ToString().TrimEnd() : "none of the checked") + $" (phase {phase})";
        }

        // One full report per round: the next round may break differently.
        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
        static class RoundReset {
            public static void Postfix() { reported = false; count = 0; }
        }
    }
}
