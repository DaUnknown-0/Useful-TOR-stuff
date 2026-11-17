// Useful TOR Stuff, GPL-3.0-or-later.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    internal static class AuditFixC_Draft {
        private static readonly Type draft = TorType.Get("TheOtherRoles.Modules.RoleDraft");
        private static readonly FieldInfo running = AccessTools.Field(draft, "isRunning");
        private static readonly FieldInfo order = AccessTools.Field(draft, "pickOrder");
        private static readonly FieldInfo pickedRoles = AccessTools.Field(draft, "alreadyPicked");
        private static readonly FieldInfo picked = AccessTools.Field(draft, "picked");
        private static readonly FieldInfo timer = AccessTools.Field(draft, "timer");
        private static readonly FieldInfo buttons = AccessTools.Field(draft, "buttons");

        private static void Cleanup() {
            running.SetValue(null, false);
            ((List<byte>)order.GetValue(null)).Clear();
            ((List<byte>)pickedRoles.GetValue(null)).Clear();
            picked.SetValue(null, false);
            timer.SetValue(null, 0f);
            var list = (List<ActionButton>)buttons.GetValue(null);
            foreach (var button in list) {
                if (button == null) continue;
                button.GetComponent<PassiveButton>()?.OnClick?.RemoveAllListeners();
                UnityEngine.Object.Destroy(button.gameObject);
            }
            list.Clear();
            SoundEffectsManager.stop("draft");
        }

        // Audit 16.11. TOR-005: Ungueltige Rollen vor setRole und alreadyPicked ablehnen.
        // Absenderautoritaet bleibt Paket A zugeordnet.
        [HarmonyPatch]
        private static class ValidRole {
            private static MethodBase TargetMethod() => AccessTools.Method(draft, "receivePick");
            private static bool Prefix(byte playerId, byte roleId) {
                var role = RoleInfo.allRoleInfos.Find(x => (byte)x.roleId == roleId && !x.isModifier);
                var player = Helpers.playerById(playerId);
                return role != null && player != null && player.Data != null && !player.Data.Disconnected;
            }
        }

        // Audit 16.11. TOR-005: Reset und Coroutine-Abbruch duerfen keine Timersperre hinterlassen.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        private static class Reset {
            private static void Prefix() => Cleanup();
        }

        [HarmonyPatch]
        private static class CoroutineCleanup {
            private static MethodBase TargetMethod() => AccessTools.Method(draft, "CoSelectRoles");
            private static void Postfix(ref IEnumerator __result) => __result = Run(__result);
            private static IEnumerator Run(IEnumerator original) {
                try { while (original.MoveNext()) yield return original.Current; }
                finally {
                    try { (original as IDisposable)?.Dispose(); }
                    finally { Cleanup(); }
                }
            }
        }
    }
}
