// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SettingsChangeMarks - which modded settings differ from the last round (User 2026-11-13).
 *
 * At every round start (end of the intro, every client) the selection of every modded option is noted. Back
 * in the same lobby, SettingsOverlayView draws a value that differs from that note in amber with a "*", and
 * the page ends with a one-line legend. So everybody sees at a glance what the host changed between two
 * rounds, also the players who were not watching the change feed at that moment.
 * Before the first round of a lobby nothing is marked. Joining another lobby (other game id) drops the note.
 * Display only, local, nothing is sent.
 */

using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;

namespace UsefulTORStuff {

    public static class SettingsChangeMarks {
        private static Dictionary<int, int> lastRound;
        private static int gameId = int.MinValue;

        public static bool Changed(CustomOption option) =>
            lastRound != null && option != null && lastRound.TryGetValue(option.id, out int s) && s != option.selection;

        private static long anyAt;
        private static bool anyCached;

        // the overlay is rebuilt every frame while it is open: answer from a half-second cache
        public static bool AnyChanged() {
            if (lastRound == null) return false;
            long now = System.Environment.TickCount64;
            if (now < anyAt) return anyCached;
            anyAt = now + 500;
            anyCached = Scan();
            return anyCached;
        }

        private static bool Scan() {
            foreach (var o in CustomOption.options)
                if (o != null && o.id != 0 && lastRound.TryGetValue(o.id, out int s) && s != o.selection && SettingsOverlayView.IsVisible(o)) return true;
            return false;
        }

        private static void Snapshot() {
            var map = new Dictionary<int, int>();
            foreach (var o in CustomOption.options) if (o != null && o.id != 0) map[o.id] = o.selection;
            lastRound = map;
            try { gameId = AmongUsClient.Instance != null ? AmongUsClient.Instance.GameId : int.MinValue; } catch { }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        private static class RoundStartPatch {
            public static void Postfix() {
                try { Snapshot(); } catch (System.Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[ChangeMarks] {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class LobbyJoinPatch {
            public static void Postfix(AmongUsClient __instance) {
                try { if (__instance.GameId != gameId) lastRound = null; } catch { lastRound = null; }
            }
        }
    }
}
