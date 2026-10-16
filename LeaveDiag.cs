// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    /// <summary>
    /// One log line per player who leaves, on every client: name, the reason the server gave, and in
    /// which phase of the game it happened (lobby, intro or Role Draft, round, meeting). Added on
    /// 2026-10-16 because a player kept dropping and the log only showed the gap afterwards (end
    /// screen 12 of 13, RoundReplay: he never left the spawn point). Also logs the local client's own
    /// exit reason, so a dropping player's own log names it. Logging only, nothing is changed.
    /// </summary>
    internal static class LeaveDiag {
        private static float roundStart = -1f;

        private static string Phase() {
            try {
                var client = AmongUsClient.Instance;
                if (client == null) return "menu";
                if (client.GameState != InnerNet.InnerNetClient.GameStates.Started) return client.GameState.ToString().ToLowerInvariant();
                if (DraftMusic.DraftRunning()) return "role draft";
                if (IntroScreen.Exists) return "intro";
                if (MeetingHud.Instance != null) return "meeting";
                if (ExileController.Instance != null) return "exile";
                return "round";
            } catch { return "?"; }
        }

        private static string Clock() {
            if (roundStart < 0f) return "";
            return $", {Time.realtimeSinceStartup - roundStart:F0} s after round start";
        }

        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
        static class RoundStartPatch {
            public static void Postfix() { roundStart = Time.realtimeSinceStartup; }
        }

        [HarmonyPatch(typeof(GameData), nameof(GameData.HandleDisconnect), new[] { typeof(PlayerControl), typeof(DisconnectReasons) })]
        static class HandleDisconnectPatch {
            public static void Postfix(PlayerControl player, DisconnectReasons reason) {
                try {
                    string name = "?";
                    try { name = player != null && player.Data != null ? player.Data.PlayerName : "?"; } catch { }
                    int left = 0;
                    try { foreach (var p in PlayerControl.AllPlayerControls) if (p != null && p.Data != null && !p.Data.Disconnected) left++; } catch { }
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[LeaveDiag] {name} left: reason {reason}, phase {Phase()}{Clock()}, {left} still connected.");
                } catch { }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
        static class LocalExitPatch {
            public static void Prefix([HarmonyArgument(0)] DisconnectReasons reason) {
                try { UsefulTORStuffPlugin.Logger?.LogInfo($"[LeaveDiag] this client leaves: reason {reason}, phase {Phase()}{Clock()}."); } catch { }
            }
        }
    }
}
