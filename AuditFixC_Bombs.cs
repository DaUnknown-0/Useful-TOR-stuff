// Useful TOR Stuff, GPL-3.0-or-later. Bombenablauf nach TOR 4.8.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    internal static class AuditFixC_Bombs {
        // Audit 16.11. TOR-014: Beide verzoegerten Callbacks gehoeren genau einer Bombe.
        [HarmonyPatch]
        private static class BombCallbacks {
            private static readonly Dictionary<Type, FieldInfo[]> owners = new();
            private static FieldInfo[] OwnerPath(Type closure, int depth = 0) {
                if (depth > 3) return null;
                foreach (var field in closure.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (field.FieldType == typeof(Bomb)) return new[] { field };
                    if (field.FieldType.DeclaringType != typeof(Bomb)) continue;
                    var rest = OwnerPath(field.FieldType, depth + 1);
                    if (rest != null) return new[] { field }.Concat(rest).ToArray();
                }
                return null;
            }
            private static IEnumerable<MethodBase> TargetMethods() {
                foreach (var closure in typeof(Bomb).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                    var path = OwnerPath(closure);
                    if (path == null) continue;
                    owners[closure] = path;
                    foreach (var method in closure.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (method.Name.StartsWith("<.ctor>b__", StringComparison.Ordinal) && method.ReturnType == typeof(void) &&
                            method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(float))
                            yield return method;
                }
            }
            private static bool Prefix(object __instance) {
                object owner = __instance;
                foreach (var field in owners[__instance.GetType()]) owner = owner == null ? null : field.GetValue(owner);
                var bomb = owner as Bomb;
                return ReferenceEquals(Bomber.bomb, bomb) && bomb?.bomb != null && bomb.background != null &&
                    !MeetingHud.Instance && !ExileController.Instance && AmongUsClient.Instance != null &&
                    AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started;
            }
        }

        private static readonly MethodInfo deathReason = AccessTools.Method(TorType.Get("TheOtherRoles.GameHistory"), "overrideDeathReasonAndKiller");
        private static readonly byte shareGhostInfo = Convert.ToByte(Enum.Parse(TorType.Get("TheOtherRoles.CustomRPC"), "ShareGhostInfo"));
        private static readonly byte deathInfo = Convert.ToByte(Enum.Parse(typeof(RPCProcedure).GetNestedType("GhostInfoTypes"), "DeathReasonAndKiller"));

        // Audit 16.11. TOR-015: Ledger und Ghost-RPC nur fuer einen tatsaechlich ausgefuehrten Kill.
        [HarmonyPatch(typeof(Bomb), nameof(Bomb.explode))]
        private static class BombDeath {
            private static bool Prefix(Bomb b) {
                if (b == null || !ReferenceEquals(Bomber.bomb, b) || b.bomb == null) return false;
                var bomber = Bomber.bomber;
                var local = PlayerControl.LocalPlayer;
                try {
                    if (bomber == null || local == null || local.Data == null) return false;
                    var position = b.bomb.transform.position;
                    if (Vector2.Distance(position, local.transform.position) < Bomber.destructionRange && !local.Data.IsDead) {
                        var result = Helpers.checkMurderAttemptAndKill(bomber, local, false, false, true, true);
                        if (result == MurderAttemptResult.PerformKill && local.Data.IsDead) {
                            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(local.NetId, shareGhostInfo, SendOption.Reliable, -1);
                            writer.Write(local.PlayerId);
                            writer.Write(deathInfo);
                            writer.Write(local.PlayerId);
                            writer.Write((byte)DeadPlayer.CustomDeathReason.Bomb);
                            writer.Write(bomber.PlayerId);
                            AmongUsClient.Instance.FinishRpcImmediately(writer);
                            deathReason.Invoke(null, new object[] { local, DeadPlayer.CustomDeathReason.Bomb, bomber });
                        }
                    }
                    try { SoundEffectsManager.playAtPosition("bombExplosion", position, maxDuration: 1.6f, range: Bomber.hearRange); }
                    catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[AuditFixC/TOR-015] Bombenton: {e.Message}"); }
                    return false;
                } finally {
                    if (ReferenceEquals(Bomber.bomb, b)) {
                        Bomber.clearBomb();
                        Bomb.canDefuse = false;
                        Bomber.isActive = false;
                    }
                }
            }
        }
    }
}
