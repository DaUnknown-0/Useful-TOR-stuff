// Useful TOR Stuff, GPL-3.0-or-later.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    internal static class AuditFixC_RoleLifecycle {
        internal static readonly Type Buttons = TorType.Get("TheOtherRoles.HudManagerStartPatch");
        internal static CustomButton Button(string name) => AccessTools.Field(Buttons, name)?.GetValue(null) as CustomButton;

        internal static void CancelYoyo() {
            var button = Button("yoyoButton");
            if (button == null) return;
            button.isEffectActive = false;
            button.HasEffect = false;
            button.Timer = button.DeputyTimer = button.MaxTimer;
            button.Sprite = Yoyo.getMarkButtonSprite();
            button.buttonText = "Mark Location";
            if (button.actionButton != null) button.actionButton.cooldownTimerText.color = Palette.EnabledColor;
        }

        // Audit 16.11. TOR-006: Rollenentzug muss auch den Rolleninhaber entfernen.
        [HarmonyPatch(typeof(Yoyo), nameof(Yoyo.clearAndReload))]
        private static class YoyoReset {
            private static void Prefix() { CancelYoyo(); Yoyo.yoyo = null; Silhouette.clearSilhouettes(); }
        }

        // Audit 16.11. TOR-016: Kein Effektabschluss mit geloeschtem Ruecksprungziel.
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.MeetingEndedUpdate))]
        private static class YoyoMeeting {
            private static void Prefix() {
                if (!Yoyo.markStaysOverMeeting) CancelYoyo();
            }
        }

        // Audit 16.11. TOR-010: Nur die Sammlung initialisieren, keinen Rollenreset ausloesen.
        [HarmonyPatch]
        private static class ChameleonBeforeWrapUp {
            private static IEnumerable<MethodBase> TargetMethods() {
                var type = TorType.Get("TheOtherRoles.Patches.ExileControllerWrapUpPatch");
                yield return AccessTools.Method(type, "WrapUpPostfix");
                yield return AccessTools.Method(type, "Prefix", new[] { typeof(GameObject) });
                yield return AccessTools.Method(TorType.Get("TheOtherRoles.Patches.AirshipSpawnInPatch"), "Postfix");
            }
            private static void Prefix() { Chameleon.lastMoved ??= new Dictionary<byte, float>(); }
        }

        // Audit 16.11. TOR-010: Synthetisches Exil darf fehlende initData als leeres Exil behandeln.
        [HarmonyPatch]
        private static class BaseExileData {
            private static MethodBase TargetMethod() => AccessTools.Method(TorType.Get(
                "TheOtherRoles.Patches.ExileControllerWrapUpPatch+BaseExileControllerPatch"), "Postfix");
            private static bool Prefix(ExileController __0) {
                if (__0 != null && __0.initData != null) return true;
                if (__0 != null && AmongUsClient.Instance != null &&
                    AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started) WrapUp(null);
                return false;
            }
        }

        private static readonly MethodInfo wrapUp = AccessTools.Method(TorType.Get(
            "TheOtherRoles.Patches.ExileControllerWrapUpPatch"), "WrapUpPostfix");
        private static void WrapUp(PlayerControl exiled) => wrapUp.Invoke(null, new object[] { exiled });

        private static readonly List<PlayerControl> pendingAirship = new();

        // Der Exilcontroller zerstoert sich selbst. Ein weiterer Yield in seiner
        // Coroutine wuerde den Abschluss verlieren; der HUD-Tick lebt weiter.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class FinishAirship {
            private static void Postfix() {
                if (pendingAirship.Count == 0) return;
                if (AmongUsClient.Instance == null ||
                    AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Started) {
                    pendingAirship.Clear();
                    return;
                }
                if (UnityEngine.Object.FindObjectOfType<SpawnInMinigame>() != null ||
                    PlayerControl.LocalPlayer == null || !PlayerControl.LocalPlayer.CanMove) return;
                var completed = pendingAirship.ToArray();
                pendingAirship.Clear();
                foreach (var exiled in completed) WrapUp(exiled);
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        private static class CancelAirshipOnEnd {
            private static void Prefix() => pendingAirship.Clear();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class CancelAirshipOnJoin {
            private static void Postfix() => pendingAirship.Clear();
        }

        // Audit 16.11. TOR-050: TORs fruehen Callback durch Abschluss des Enumerators ersetzen.
        [HarmonyPatch]
        private static class DisableEarlyAirshipWrapUp {
            private static MethodBase TargetMethod() => AccessTools.Method(TorType.Get(
                "TheOtherRoles.Patches.ExileControllerWrapUpPatch+AirshipExileControllerPatch"), "Postfix");
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        private static class AirshipCompletion {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(AirshipExileController __instance, ref Il2CppSystem.Collections.IEnumerator __result) {
                var exiled = __instance.initData?.networkedPlayer?.Object;
                __result = Complete(__result.WrapToManaged(), exiled).WrapToIl2Cpp();
            }
            private static IEnumerator Complete(IEnumerator original, PlayerControl exiled) {
                try {
                    while (original.MoveNext()) yield return original.Current;
                    if (AmongUsClient.Instance != null &&
                        AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started)
                        pendingAirship.Add(exiled);
                } finally { (original as IDisposable)?.Dispose(); }
            }
        }
    }
}
