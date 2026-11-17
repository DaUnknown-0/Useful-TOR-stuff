using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;

namespace UsefulTORStuff {
    // Audit 16.11. TOR-036: fremde Stringbereiche weder mit null noch per Dictionary-Indexer beantworten.
    [HarmonyPatch]
    internal static class AuditFixA_OptionStringIds {
        private static MethodBase TargetMethod() => AccessTools.Method(TorType.Get("TheOtherRoles.Patches.GetStringPatch"), "Prefix");
        private static bool Prefix(StringNames id, ref bool __result) {
            int key = (int)id - 6000;
            if ((int)id < 6000 || CustomOption.options.Exists(option => option != null && option.id == key)) return true;
            __result = true; // Ergebnis des TOR-Prefixes: nächste Übersetzer bzw. Vanilla dürfen auflösen.
            return false;
        }
    }
    [HarmonyPatch]
    internal static class AuditFixA_ColorStringIds {
        private static MethodBase TargetMethod() => AccessTools.Method(
            TorType.Get("TheOtherRoles.Modules.CustomColors+CustomColorPatches+ColorStringPatch"), "Prefix");
        private static bool Prefix(StringNames name, ref bool __result) {
            var colors = AuditFixA_RpcBoundary.Field("TheOtherRoles.Modules.CustomColors", "ColorStrings") as System.Collections.IDictionary;
            if ((int)name < 50000 || colors?.Contains((int)name) == true) return true;
            __result = true;
            return false;
        }
    }
}
