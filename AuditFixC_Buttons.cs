// Useful TOR Stuff, GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;

namespace UsefulTORStuff {
    internal static class AuditFixC_Buttons {
        private static readonly Type propHunt = TorType.Get("TheOtherRoles.CustomGameModes.PropHunt");
        private static readonly FieldInfo revealed = AccessTools.Field(propHunt, "isCurrentlyRevealed");
        private static readonly FieldInfo click = AccessTools.Field(typeof(CustomButton), "OnClick");
        private static readonly FieldInfo initialClick = AccessTools.Field(typeof(CustomButton), "InitialOnClick");

        private static bool HasRevealCandidate() {
            var map = (Dictionary<byte, float>)revealed.GetValue(null);
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p != null && p.Data != null && p.Data.Role != null && !p.Data.Disconnected &&
                    !p.Data.IsDead && !p.Data.Role.IsImpostor && !map.ContainsKey(p.PlayerId)) return true;
            return false;
        }

        [HarmonyPatch]
        private static class ButtonCreation {
            private static MethodBase TargetMethod() => AccessTools.Method(AuditFixC_RoleLifecycle.Buttons, "createButtonsPostfix");
            private static void Prefix(out HashSet<CustomButton> __state) => __state = new HashSet<CustomButton>(CustomButton.buttons);

            // Audit 16.11. TOR-034: Pruefung in CouldUse und direkt vor der Auswahl.
            private static void Postfix() {
                var button = AuditFixC_RoleLifecycle.Button("propHuntRevealButton");
                if (button == null) return;
                var couldUse = button.CouldUse;
                var onClick = (Action)initialClick.GetValue(button);
                button.CouldUse = () => couldUse() && HasRevealCandidate();
                Action guarded = () => { if (HasRevealCandidate()) onClick(); };
                initialClick.SetValue(button, guarded);
                click.SetValue(button, guarded);
            }

            // Audit 16.11. TOR-035: Fehlversuch vollstaendig entfernen, bevor TOR erneut aufbaut.
            private static Exception Finalizer(Exception __exception, HashSet<CustomButton> __state) {
                if (__exception == null || __state == null) return __exception;
                for (int i = CustomButton.buttons.Count - 1; i >= 0; i--) {
                    var button = CustomButton.buttons[i];
                    if (__state.Contains(button)) continue;
                    CustomButton.buttons.RemoveAt(i);
                    if (button == null) continue;
                    if (button.actionButton != null) {
                        var passive = button.actionButton.GetComponent<PassiveButton>();
                        passive?.OnClick?.RemoveAllListeners();
                        passive?.OnMouseOver?.RemoveAllListeners();
                        passive?.OnMouseOut?.RemoveAllListeners();
                        button.actionButton.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(button.actionButton.gameObject);
                    }
                    foreach (var field in AuditFixC_RoleLifecycle.Buttons.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        if (field.FieldType == typeof(CustomButton) && ReferenceEquals(field.GetValue(null), button)) field.SetValue(null, null);
                    button.HasButton = button.CouldUse = () => false;
                    foreach (var field in typeof(CustomButton).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                        if (field.FieldType == typeof(Action)) field.SetValue(button, (Action)(() => { }));
                    button.hudManager = null;
                }
                AccessTools.Field(AuditFixC_RoleLifecycle.Buttons, "initialized").SetValue(null, false);
                return __exception;
            }
        }
    }
}
