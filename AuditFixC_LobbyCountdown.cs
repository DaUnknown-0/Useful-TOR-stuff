// Useful TOR Stuff, GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-059: Auch Stop-Buttons aus dem Zustand statt dem englischen Text ableiten.
    [HarmonyPatch(typeof(TheOtherRoles.Patches.GameStartManagerPatch.GameStartManagerUpdatePatch), "Postfix")]
    internal static class AuditFixC_LobbyCountdown {
        private static bool IsCountdown(string text, string prefix, GameStartManager manager) =>
            manager != null && manager.startState == GameStartManager.StartingStates.Countdown;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var startsWith = AccessTools.Method(typeof(string), nameof(string.StartsWith), new[] { typeof(string) });
            bool startingLiteral = false;
            foreach (var code in instructions) {
                if (startingLiteral && code.Calls(startsWith)) {
                    var argument = new CodeInstruction(OpCodes.Ldarg_0);
                    argument.labels.AddRange(code.labels); argument.blocks.AddRange(code.blocks);
                    yield return argument;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AuditFixC_LobbyCountdown), nameof(IsCountdown)));
                } else yield return code;
                startingLiteral = code.opcode == OpCodes.Ldstr && Equals(code.operand, "Starting");
            }
        }
    }
}
