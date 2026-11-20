// Useful TOR Stuff, GPL-3.0-or-later.
using HarmonyLib;
using TheOtherRoles;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-049: Nur TORs eigene Endgruende 10 bis 16 adaptieren.
    // Eine fremde Belegung derselben sieben Zahlen ist ohne Protokollkennung nicht unterscheidbar.
    [HarmonyPatch(typeof(TheOtherRoles.Patches.OnGameEndPatch), nameof(TheOtherRoles.Patches.OnGameEndPatch.Prefix))]
    internal static class AuditFixC_EndReason {
        private static bool Prefix(ref EndGameResult __1) {
            if ((int)__1.GameOverReason <= 16) return true;
            TheOtherRoles.Patches.OnGameEndPatch.gameOverReason = __1.GameOverReason;
            // Review 20.11. A3: Vanilla bekommt wie bisher einen ihm bekannten Grund. Rohwerte über 16
            // (Mod-Solosiege) sind im Vanilla-Spielende ungetestet (Endbildschirm, Statistik). Der echte
            // Grund steht für alle Mods in OnGameEndPatch.gameOverReason.
            __1.GameOverReason = GameOverReason.ImpostorByKill;
            Helpers.toggleZoom(reset: true);
            return false;
        }
    }
}
