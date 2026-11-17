using HarmonyLib;

namespace UsefulTORStuff {
    // Audit 16.11. UTS-2-06/07: Abbrüche und neue Runden dürfen keine Zusatzwerte übernehmen.
    internal static class AuditFixD_RoundCounters {
        private static void Clear() { SessionStats.ClearRoundCounts(); ShieldNotice.ResetRound(); }

        [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
        private static class StartRound {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class Join {
            private static void Postfix() => Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        private static class End {
            private static void Finalizer() => Clear();
        }
    }
}
