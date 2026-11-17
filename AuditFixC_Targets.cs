// Useful TOR Stuff, GPL-3.0-or-later.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    internal static class AuditFixC_Targets {
        // Audit 16.11. TOR-012: Derselbe Spy-Schalter gilt fuer Kill und Ruestung.
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkArmored))]
        private static class SpyArmor {
            private static void Prefix(PlayerControl target, ref bool additionalCondition) {
                if (!additionalCondition && target != null && target == Spy.spy && Sheriff.spyCanDieToSheriff)
                    additionalCondition = true;
            }
        }

        // Audit 16.11. TOR-022: Nur die obere Indexgrenze im verwalteten TOR-Code ersetzen.
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "setTarget")]
        private static class LongKillDistance {
            private static int DistanceIndex(int value, int min, int max) =>
                Mathf.Clamp(value, min, AmongUs.GameOptions.GameOptionsData.KillDistances.Length - 1);
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                var clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp), new[] { typeof(int), typeof(int), typeof(int) });
                foreach (var code in instructions) {
                    if (code.Calls(clamp)) code.operand = AccessTools.Method(typeof(LongKillDistance), nameof(DistanceIndex));
                    yield return code;
                }
            }
        }

        // Audit 16.11. TOR-022: Vier Werte vor Initialize anbieten und Long nicht zuruecksetzen.
        [HarmonyPatch]
        private static class KillDistanceWidget {
            private static MethodBase TargetMethod() => AccessTools.Method(TorType.Get("TheOtherRoles.AddToKillDistanceSetting"),
                "Prefix", new[] { typeof(StringOption) });
            private static bool Prefix(StringOption __0) {
                if (__0.Title == StringNames.GameKillDistance && __0.Values.Count == 3)
                    __0.Values = new(new[] { (StringNames)49999, StringNames.SettingShort, StringNames.SettingMedium, StringNames.SettingLong });
                return false;
            }
        }

        // Audit 16.11. TOR-027: Ein leerer Pool ist ein normaler Zustand, kein Fehlerpfad.
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "bountyHunterUpdate")]
        private static class EmptyBountyPool {
            private static readonly FieldInfo icons = AccessTools.Field(TorType.Get("TheOtherRoles.TORMapOptions"), "playerIcons");
            private static bool Prefix() {
                var hunter = BountyHunter.bountyHunter;
                if (hunter == null || hunter != PlayerControl.LocalPlayer || hunter.Data == null || hunter.Data.IsDead) return true;
                if (BountyHunter.bounty != null && BountyHunter.bountyUpdateTimer > Time.fixedDeltaTime) return true;
                var partner = Lovers.getPartner(hunter);
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null || p.Data == null || p.Data.Role == null || p.Data.IsDead || p.Data.Disconnected || p.Data.Role.IsImpostor) continue;
                    if (p == Spy.spy || (p == Sidekick.sidekick && Sidekick.wasTeamRed) ||
                        (p == Jackal.jackal && Jackal.wasTeamRed) || (p == Mini.mini && !Mini.isGrownUp()) || p == partner) continue;
                    return true;
                }
                BountyHunter.bounty = null;
                BountyHunter.bountyUpdateTimer = 0f;
                if (BountyHunter.arrow?.arrow != null) BountyHunter.arrow.arrow.SetActive(false);
                if (BountyHunter.cooldownText != null) BountyHunter.cooldownText.gameObject.SetActive(false);
                if (icons.GetValue(null) is IDictionary map)
                    foreach (var value in map.Values)
                        if (value is PoolablePlayer icon && icon != null) icon.gameObject.SetActive(false);
                return false;
            }
        }

        // Audit 16.11. TOR-043: Morphdarstellung gilt nur fuer den teleportierenden Morphling.
        [HarmonyPatch(typeof(Portal), nameof(Portal.startTeleport))]
        private static class PortalMorph {
            private static PlayerControl DisplayTarget(byte playerId) {
                var player = Helpers.playerById(playerId);
                return player != null && player == Morphling.morphling && Morphling.morphTimer > 0 && Morphling.morphTarget != null && Morphling.morphTarget.Data != null
                    ? Morphling.morphTarget : player;
            }
            private static bool ActualDead(NetworkedPlayerInfo displayData, byte playerId) {
                var actual = Helpers.playerById(playerId);
                return actual == null || actual.Data == null || actual.Data.IsDead;
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                var target = AccessTools.Field(typeof(Morphling), nameof(Morphling.morphTarget));
                var isDead = AccessTools.PropertyGetter(typeof(NetworkedPlayerInfo), nameof(NetworkedPlayerInfo.IsDead));
                foreach (var code in instructions) {
                    if (code.LoadsField(target)) {
                        var load = new CodeInstruction(OpCodes.Ldarg_0);
                        load.labels.AddRange(code.labels); load.blocks.AddRange(code.blocks);
                        yield return load;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortalMorph), nameof(DisplayTarget)));
                    } else if (code.Calls(isDead)) {
                        var load = new CodeInstruction(OpCodes.Ldarg_0);
                        load.labels.AddRange(code.labels); load.blocks.AddRange(code.blocks);
                        yield return load;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortalMorph), nameof(ActualDead)));
                    } else yield return code;
                }
            }
        }

        // Audit 16.11. TOR-054: Nullfall vor TORs Prefix definieren, Postfixe laufen normal weiter.
        [HarmonyPatch]
        private static class MissingLightPlayer {
            private static MethodBase TargetMethod() => AccessTools.Method(TorType.Get("TheOtherRoles.Patches.ShipStatusPatch"), "Prefix",
                new[] { typeof(float).MakeByRefType(), typeof(ShipStatus), typeof(NetworkedPlayerInfo) });
            private static bool Prefix(ref float __0, ShipStatus __1, NetworkedPlayerInfo __2, ref bool __result) {
                if (__2 != null && __2.Role != null) return true;
                __0 = __1 != null ? __1.MaxLightRadius : 1f;
                __result = false;
                return false;
            }
        }

        // Audit 16.11. TOR-057: Hell/Dunkel aus der Farbe statt aus der Verbindungs-ID ableiten.
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.isD))]
        private static class LightColorById {
            internal static readonly FieldInfo LightColors = AccessTools.Field(TorType.Get("TheOtherRoles.Modules.CustomColors"), "lighterColors");
            private static bool Prefix(byte playerId, ref bool __result) {
                var data = GameData.Instance != null ? GameData.Instance.GetPlayerById(playerId) : null;
                __result = data != null && LightColors.GetValue(null) is List<int> colors && colors.Contains(data.DefaultOutfit.ColorId);
                return false;
            }
        }
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.isLighterColor))]
        private static class LightColorByPlayer {
            private static bool Prefix(PlayerControl target, ref bool __result) {
                __result = target != null && target.Data != null && LightColorById.LightColors.GetValue(null) is List<int> colors &&
                    colors.Contains(target.Data.DefaultOutfit.ColorId);
                return false;
            }
        }
    }
}
