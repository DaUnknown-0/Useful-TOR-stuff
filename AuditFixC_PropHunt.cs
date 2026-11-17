// Useful TOR Stuff, GPL-3.0-or-later.
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-065: Nur den nullable Trefferzugriff ersetzen, Suchregeln beibehalten.
    [HarmonyPatch]
    internal static class AuditFixC_PropHunt {
        private static MethodBase TargetMethod() => AccessTools.Method(TorType.Get("TheOtherRoles.CustomGameModes.PropHunt"), "FindClosestDisguiseObject");
        private static GameObject ObjectOrNull(Component component) => component != null ? component.gameObject : null;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var getter = AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject));
            foreach (var code in instructions) {
                if (code.Calls(getter)) {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(AuditFixC_PropHunt), nameof(ObjectOrNull));
                }
                yield return code;
            }
        }
    }
}
