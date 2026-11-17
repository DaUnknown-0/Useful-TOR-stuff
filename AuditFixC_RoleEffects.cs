// Useful TOR Stuff, GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;

namespace UsefulTORStuff {
    internal static class AuditFixC_RoleEffects {
        // Audit 16.11. TOR-061: Zerstoerte Trickster-Vents zuvor aus dem Schiffsarray austragen.
        [HarmonyPatch(typeof(JackInTheBox), nameof(JackInTheBox.clearJackInTheBoxes))]
        private static class BoxVents {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() {
                var ship = TheOtherRoles.Utilities.MapUtilities.CachedShipStatus;
                if (ship == null || ship.AllVents == null || JackInTheBox.AllJackInTheBoxes.Count == 0) return;
                var owned = new HashSet<IntPtr>();
                foreach (var box in JackInTheBox.AllJackInTheBoxes)
                    if (box?.vent != null) owned.Add(box.vent.Pointer);
                var remaining = new List<Vent>();
                foreach (var vent in ship.AllVents)
                    if (vent != null && !owned.Contains(vent.Pointer)) remaining.Add(vent);
                ship.AllVents = remaining.ToArray();
            }
        }

        // Audit 16.11. TOR-064: Nur diese beiden Rollensuchen teilen einen kurzen Leichensnapshot.
        [HarmonyPatch]
        private static class BodySearch {
            private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<DeadBody> cached;
            private static float expires;
            private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<DeadBody> Bodies() {
                bool refresh = cached == null || Time.unscaledTime >= expires;
                if (!refresh)
                    foreach (var body in cached)
                        if (body == null) { refresh = true; break; }
                if (refresh) {
                    cached = UnityEngine.Object.FindObjectsOfType<DeadBody>();
                    expires = Time.unscaledTime + 0.1f;
                }
                return cached;
            }
            private static IEnumerable<MethodBase> TargetMethods() {
                yield return AccessTools.Method(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "ninjaUpdate");
                yield return AccessTools.Method(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "vultureUpdate");
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                foreach (var code in instructions) {
                    if (code.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Object) &&
                        method.Name == nameof(UnityEngine.Object.FindObjectsOfType) && method.IsGenericMethod &&
                        method.GetGenericArguments()[0] == typeof(DeadBody) && method.GetParameters().Length == 0)
                        code.operand = AccessTools.Method(typeof(BodySearch), nameof(Bodies));
                    yield return code;
                }
            }
            internal static void Clear() { cached = null; expires = 0; }
        }

        // Audit 16.11. TOR-067: Kein Textzugriff nach Zerstoerung; statischen Eintrag entfernen.
        [HarmonyPatch]
        private static class MessageCallback {
            private static readonly FieldInfo text = AccessTools.Field(typeof(CustomMessage), "text");
            private static readonly FieldInfo messages = AccessTools.Field(typeof(CustomMessage), "customMessages");
            private static readonly Dictionary<Type, FieldInfo> owners = new();
            private static IEnumerable<MethodBase> TargetMethods() {
                foreach (var type in typeof(CustomMessage).GetNestedTypes(BindingFlags.NonPublic)) {
                    var owner = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(x => x.FieldType == typeof(CustomMessage));
                    if (owner == null) continue;
                    owners[type] = owner;
                    foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        if (method.Name.StartsWith("<.ctor>b__", StringComparison.Ordinal)) yield return method;
                }
            }
            private static bool Prefix(object __instance) {
                var message = (CustomMessage)owners[__instance.GetType()].GetValue(__instance);
                if (text.GetValue(message) is TMPro.TMP_Text label && label != null) return true;
                ((List<CustomMessage>)messages.GetValue(null)).Remove(message);
                return false;
            }
            internal static void Clear() {
                var list = (List<CustomMessage>)messages.GetValue(null);
                foreach (var message in list)
                    if (text.GetValue(message) is TMPro.TMP_Text label && label != null) UnityEngine.Object.Destroy(label.gameObject);
                list.Clear();
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        private static class Reset {
            private static void Prefix() { BodySearch.Clear(); MessageCallback.Clear(); }
        }
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.OnDestroy))]
        private static class HudDestroyed {
            private static void Prefix() { BodySearch.Clear(); MessageCallback.Clear(); }
        }
    }
}
