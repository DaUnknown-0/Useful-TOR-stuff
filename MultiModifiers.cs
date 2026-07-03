// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MultiModifiers - quantity options for the modifiers TOR caps at ONE holder: the Mini and the
 * Armored. (TOR already ships quantity options for Bait/Bloody/AntiTeleport/Sunglasses/Vip/Invert/
 * Chameleon; the Tiebreaker is covered by TiebreakerMultiple in this same plugin. The Shifter
 * modifier and the Lovers pair are deliberately NOT multiplied - both are deep single-instance
 * systems in TOR: the Shifter reuses the whole shift-button machinery around Shifter.shifter, and
 * Lovers is a hard-wired lover1/lover2 pair that LoverRevenger also builds on.)
 *
 * Approach (same architecture as TiebreakerMultiple): TOR's single statics (Mini.mini /
 * Armored.armored) stay untouched and keep driving TOR's own logic for ONE holder - the LAST one
 * assigned. Every holder is additionally tracked in our own lists via a setModifier postfix, and
 * the "extra" holders (list minus TOR's single) get the modifier behaviour re-supplied by patches:
 *
 * Mini extras (shared growth clock - TOR's Mini growth statics are global, not per-player):
 *   - Assignment: getSelectionForRoleId x quantity (ensured path) + host top-up after assignModifiers
 *     (chance path under-assigns, exactly like the Tiebreaker).
 *   - Body scale + collider: postfix on PlayerControlFixedUpdatePatch.playerSizeUpdate.
 *   - Age suffix on the name tag: postfix on HudManagerUpdatePatch.miniUpdate (internal, reflection).
 *   - Kill protection while not grown up: postfix on Helpers.checkMuderAttempt (SuppressKill).
 *   - Targeting exclusion: prefix on setTarget - whenever a call site excluded TOR's base Mini via
 *     untargetablePlayers, the extras are appended to the same list (mirrors call-site semantics).
 *   - Crew mini voted out young -> Mini lose (Mini.triggerMiniLose), like TOR's WrapUpPostfix.
 *   - Impostor extra mini: kill cooldown x2 young / x0.66 grown, re-applied after kills and meetings.
 *
 * Armored extras:
 *   - Assignment: like the Mini (quantity + top-up).
 *   - Armor block: postfix on Helpers.checkArmored - first kill attempt on an unbroken extra armor
 *     is blocked, the break is synced via our own RPC (246 sub 0) so every client greys the armor.
 *
 * GATING: assignment of extras only happens when EVERY client runs this mod (handshake) - the kill
 * protection/armor block runs on the KILLER's client, so a mixed lobby would let unmodded players
 * kill young extra Minis. With the gate, quantity silently behaves as 1 in mixed lobbies.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class MultiModifiers {
        public static CustomOption MiniQuantity;     // 1350 - child of modifierMini
        public static CustomOption ArmoredQuantity;  // 1351 - child of modifierArmored

        // TOR's SetModifier RPC (enum internal; value stable - see RPC.cs) + our own sync RPC.
        private const byte TorSetModifierRpcId = 105;
        private const byte RpcId = 245;              // keep globally unique (see ID-Registry.md)
        private const byte SubBreakExtraArmor = 0;   // playerId

        // ALL holders (including the one in TOR's single static). "Extra" = holder != TOR's single.
        private static readonly List<byte> minis = new List<byte>();
        private static readonly List<byte> armoreds = new List<byte>();
        private static readonly HashSet<byte> brokenExtraArmor = new HashSet<byte>();
        private static readonly System.Random rng = new System.Random();

        private static bool EveryoneHasMod() {
            try { return UsefulVersionHandshake.BuildMismatchMessage() == ""; }
            catch { return false; }
        }

        private static int MiniQty() => MiniQuantity != null ? MiniQuantity.getQuantity() : 1;
        private static int ArmoredQty() => ArmoredQuantity != null ? ArmoredQuantity.getQuantity() : 1;

        private static bool IsExtraMini(PlayerControl p) =>
            p != null && minis.Contains(p.PlayerId)
            && (Mini.mini == null || Mini.mini.PlayerId != p.PlayerId);

        private static bool IsExtraArmored(byte playerId) =>
            armoreds.Contains(playerId)
            && (Armored.armored == null || Armored.armored.PlayerId != playerId);

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                MiniQuantity = CustomOption.Create(
                    1350, Types.Modifier, "Mini Quantity (max 3)",
                    new string[] { "1", "2", "3" }, CustomOptionHolder.modifierMini);
                ArmoredQuantity = CustomOption.Create(
                    1351, Types.Modifier, "Armored Quantity (max 3)",
                    new string[] { "1", "2", "3" }, CustomOptionHolder.modifierArmored);

                var opts = CustomOption.options;
                opts.Remove(MiniQuantity);
                int idx = opts.IndexOf(CustomOptionHolder.modifierMini);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, MiniQuantity);

                opts.Remove(ArmoredQuantity);
                idx = opts.IndexOf(CustomOptionHolder.modifierArmored);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, ArmoredQuantity);

                UsefulTORStuffPlugin.Logger?.LogInfo("[MultiModifiers] Quantity options created (Mini, Armored).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] CreateOptions failed: {e}");
            }
        }

        // ====================================================================
        // Reflection patches (internal TOR members).
        // ====================================================================
        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var rmsr = torAsm.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");

                var gsfr = rmsr?.GetMethod("getSelectionForRoleId", BindingFlags.NonPublic | BindingFlags.Static);
                if (gsfr != null)
                    harmony.Patch(gsfr, postfix: new HarmonyMethod(typeof(MultiModifiers), nameof(GetSelectionForRoleIdPostfix)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning("[MultiModifiers] getSelectionForRoleId not found - multi-assignment disabled.");

                var assignModifiers = rmsr?.GetMethod("assignModifiers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (assignModifiers != null)
                    harmony.Patch(assignModifiers, postfix: new HarmonyMethod(typeof(MultiModifiers), nameof(TopUp)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning("[MultiModifiers] assignModifiers not found - top-up disabled.");

                // Age suffix: miniUpdate lives in the internal HudManagerUpdatePatch class.
                var hmu = torAsm.GetType("TheOtherRoles.Patches.HudManagerUpdatePatch");
                var miniUpdate = hmu?.GetMethod("miniUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (miniUpdate != null)
                    harmony.Patch(miniUpdate, postfix: new HarmonyMethod(typeof(MultiModifiers), nameof(MiniUpdatePostfix)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning("[MultiModifiers] miniUpdate not found - extra-mini age suffix disabled.");

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[MultiModifiers][DIAG] getSelectionForRoleId={(gsfr != null)}, assignModifiers={(assignModifiers != null)}, miniUpdate={(miniUpdate != null)}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] TryPatch failed: {e}");
            }
        }

        // ====================================================================
        // Assignment: multiply the ensured spawn count; top up the chance path.
        // ====================================================================
        public static void GetSelectionForRoleIdPostfix(ref int __result, RoleId roleId, bool multiplyQuantity) {
            try {
                if (!multiplyQuantity || !EveryoneHasMod()) return;
                if (roleId == RoleId.Mini) __result *= MiniQty();
                else if (roleId == RoleId.Armored) __result *= ArmoredQty();
            } catch { }
        }

        public static void TopUp() {
            try {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                if (!EveryoneHasMod()) return;
                TopUpModifier(RoleId.Mini, CustomOptionHolder.modifierMini, MiniQty(), minis);
                TopUpModifier(RoleId.Armored, CustomOptionHolder.modifierArmored, ArmoredQty(), armoreds);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] top-up failed: {e}");
            }
        }

        // Ensure up to `want` holders exist - but only if at least one already spawned, preserving
        // the chance gate (same rule as TiebreakerMultiple.TopUp).
        private static void TopUpModifier(RoleId modifier, CustomOption spawnOpt, int want, List<byte> holders) {
            if (spawnOpt == null || spawnOpt.getSelection() <= 0) return;
            if (holders.Count == 0 || holders.Count >= want) return;

            var eligible = PlayerControl.AllPlayerControls.ToArray()
                .Where(p => p != null && p.Data != null && !p.Data.Disconnected && !p.Data.IsDead)
                .Where(p => !holders.Contains(p.PlayerId))
                .ToList();

            int toAdd = Math.Min(want - holders.Count, eligible.Count);
            for (int i = 0; i < toAdd; i++) {
                int idx = rng.Next(eligible.Count);
                byte playerId = eligible[idx].PlayerId;
                eligible.RemoveAt(idx);

                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, TorSetModifierRpcId, SendOption.Reliable, -1);
                writer.Write((byte)modifier);
                writer.Write(playerId);
                writer.Write((byte)0); // flag
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                RPCProcedure.setModifier((byte)modifier, playerId, 0); // tracked by SetModifierPatch
            }
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[MultiModifiers] {modifier} holders after top-up: {holders.Count} (target {want}).");
        }

        // Track every holder (TOR's statics only keep the LAST-assigned one).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.setModifier))]
        static class SetModifierPatch {
            public static void Postfix(byte modifierId, byte playerId) {
                try {
                    if (modifierId == (byte)RoleId.Mini && !minis.Contains(playerId)) minis.Add(playerId);
                    else if (modifierId == (byte)RoleId.Armored && !armoreds.Contains(playerId)) armoreds.Add(playerId);
                } catch { }
            }
        }

        // Erased players (Eraser) lose the modifier - keep our lists in step with TOR's own cleanup.
        // TOR only touches modifiers when ignoreModifier is false (RPC.cs:793), so mirror that gate.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.erasePlayerRoles))]
        static class ErasePatch {
            public static void Postfix(byte playerId, bool ignoreModifier) {
                try {
                    if (ignoreModifier) return;
                    minis.Remove(playerId);
                    armoreds.Remove(playerId);
                    brokenExtraArmor.Remove(playerId);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                minis.Clear();
                armoreds.Clear();
                brokenExtraArmor.Clear();
            }
        }

        // ====================================================================
        // Display: show the modifier for EVERY holder (TOR's RoleInfo only matches its single static).
        // Mini/Armored sit OUTSIDE the modifiersAreHidden block in TOR, so `showModifier` is the
        // whole gate (same as the Tiebreaker).
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class DisplayPatch {
            public static void Postfix(List<RoleInfo> __result, PlayerControl p, bool showModifier) {
                try {
                    if (!showModifier || p == null || __result == null) return;
                    if (minis.Contains(p.PlayerId) && !__result.Contains(RoleInfo.mini))
                        __result.Add(RoleInfo.mini);
                    if (armoreds.Contains(p.PlayerId) && !__result.Contains(RoleInfo.armored))
                        __result.Add(RoleInfo.armored);
                } catch { }
            }
        }

        // ====================================================================
        // Extra-Mini behaviour
        // ====================================================================

        // Body scale + collider (TOR resets everyone to 0.7 first, then scales only Mini.mini).
        [HarmonyPatch(typeof(PlayerControlFixedUpdatePatch), nameof(PlayerControlFixedUpdatePatch.playerSizeUpdate))]
        static class SizePatch {
            public static void Postfix(PlayerControl p) {
                try {
                    if (Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive()) return;
                    bool scaleThis = IsExtraMini(p)
                        // a Morphling morphed INTO an extra mini shrinks too (mirrors TOR's base-mini case)
                        || (Morphling.morphling != null && p == Morphling.morphling
                            && Morphling.morphTimer > 0f && IsExtraMini(Morphling.morphTarget));
                    // ...but an extra mini who is CURRENTLY morphed into someone else must not shrink.
                    if (IsExtraMini(p) && p == Morphling.morphling && Morphling.morphTimer > 0f
                        && !IsExtraMini(Morphling.morphTarget)) scaleThis = false;
                    if (!scaleThis) return;

                    float progress = Mini.growingProgress();
                    float scale = progress * 0.35f + 0.35f;
                    p.transform.localScale = new Vector3(scale, scale, 1f);
                    var collider = p.Collider.CastFast<CircleCollider2D>();
                    if (collider != null) {
                        collider.radius = Mini.defaultColliderRadius * 0.7f / scale;
                        collider.offset = Mini.defaultColliderOffset * Vector2.down;
                    }
                } catch { }
            }
        }

        // SurveillanceMinigamePatch is internal to TOR - resolve the night-vision flag via reflection.
        private static FieldInfo nightVisionField;
        private static bool nightVisionInit;
        private static bool NightVisionActive() {
            try {
                if (!nightVisionInit) {
                    nightVisionInit = true;
                    var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.SurveillanceMinigamePatch");
                    nightVisionField = t?.GetField("nightVisionIsActive", BindingFlags.Public | BindingFlags.Static);
                }
                return nightVisionField != null && (bool)nightVisionField.GetValue(null);
            } catch { return false; }
        }

        // Age suffix "(x)" on the name tag + meeting vote area, mirroring TOR's miniUpdate guards.
        public static void MiniUpdatePostfix() {
            try {
                if (Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive()
                    || NightVisionActive()) return;

                float progress = Mini.growingProgress();
                string suffix = "";
                if (progress != 1f)
                    suffix = " <color=#FAD934FF>(" + Mathf.FloorToInt(progress * 18) + ")</color>";
                if (!Mini.isGrowingUpInMeeting && MeetingHud.Instance != null
                    && Mini.ageOnMeetingStart != 0 && !(Mini.ageOnMeetingStart >= 18))
                    suffix = " <color=#FAD934FF>(" + Mini.ageOnMeetingStart + ")</color>";
                if (suffix == "") return;

                foreach (byte id in minis) {
                    var p = Helpers.playerById(id);
                    if (p == null || !IsExtraMini(p)) continue; // TOR already suffixed its own Mini
                    if (p == Morphling.morphling && Morphling.morphTimer > 0f) continue;
                    if (p == Ninja.ninja && Ninja.isInvisble) continue;
                    if (p.cosmetics?.nameText != null && !p.cosmetics.nameText.text.EndsWith(")</color>"))
                        p.cosmetics.nameText.text += suffix;
                    if (MeetingHud.Instance != null) {
                        foreach (PlayerVoteArea pva in MeetingHud.Instance.playerStates)
                            if (pva.NameText != null && pva.TargetPlayerId == id
                                && !pva.NameText.text.EndsWith(")</color>"))
                                pva.NameText.text += suffix;
                    }
                }
            } catch { }
        }

        // Kill protection: a not-grown-up extra mini can never be murdered (TOR's own check only
        // covers Mini.mini). No HideNSeek/PropHunt guard needed - those gamemodes never assign
        // modifiers, so the minis list is empty there and this no-ops.
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class MurderAttemptPatch {
            public static void Postfix(ref MurderAttemptResult __result, PlayerControl target) {
                try {
                    if (__result != MurderAttemptResult.PerformKill) return;
                    if (IsExtraMini(target) && !Mini.isGrownUp())
                        __result = MurderAttemptResult.SuppressKill;
                } catch { }
            }
        }

        // Targeting: whenever a call site excluded TOR's base Mini via untargetablePlayers (Jackal,
        // Sidekick, ... - and UC's Maniac bomb), exclude the extra minis the same way. Call sites
        // that deliberately allow targeting a mini (Medic shield etc.) pass no such entry and stay
        // untouched.
        [HarmonyPatch(typeof(PlayerControlFixedUpdatePatch), nameof(PlayerControlFixedUpdatePatch.setTarget))]
        static class SetTargetPatch {
            public static void Prefix(List<PlayerControl> untargetablePlayers) {
                try {
                    if (untargetablePlayers == null || Mini.mini == null || Mini.isGrownUp()) return;
                    if (!untargetablePlayers.Contains(Mini.mini)) return;
                    foreach (byte id in minis) {
                        var p = Helpers.playerById(id);
                        if (p != null && IsExtraMini(p) && !untargetablePlayers.Contains(p))
                            untargetablePlayers.Add(p);
                    }
                } catch { }
            }
        }

        // Crew extra mini voted out before grown up -> Mini lose (TOR's WrapUpPostfix rule), and the
        // impostor extra mini gets the adapted kill cooldown after every meeting (x2 young, x0.66 grown).
        private static void OnExileWrapUp(PlayerControl exiled) {
            try {
                if (exiled != null && IsExtraMini(exiled) && !Mini.isGrownUp()
                    && !exiled.Data.Role.IsImpostor
                    && !RoleInfo.getRoleInfoForPlayer(exiled).Any(x => x.isNeutral))
                    Mini.triggerMiniLose = true;

                var me = PlayerControl.LocalPlayer;
                if (me != null && IsExtraMini(me) && me.Data != null && me.Data.Role.IsImpostor && !me.Data.IsDead) {
                    var multiplier = Mini.isGrownUp() ? 0.66f : 2f;
                    me.SetKillTimer(GameOptionsManager.Instance.currentNormalGameOptions.KillCooldown * multiplier);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] exile wrap-up failed: {e}");
            }
        }

        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        static class ExileWrapUpPatch {
            public static void Postfix(ExileController __instance) {
                var np = __instance.initData?.networkedPlayer;
                OnExileWrapUp(np != null ? np.Object : null);
            }
        }

        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        static class AirshipExileWrapUpPatch {
            public static void Postfix(AirshipExileController __instance) {
                var np = __instance.initData?.networkedPlayer;
                OnExileWrapUp(np != null ? np.Object : null);
            }
        }

        // Impostor extra mini: adapted kill cooldown after each of their own kills (TOR does this
        // for Mini.mini in its MurderPlayer handling).
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderCooldownPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    var me = PlayerControl.LocalPlayer;
                    if (me == null || __instance != me) return;
                    if (!IsExtraMini(me) || me.Data == null || !me.Data.Role.IsImpostor) return;
                    var multiplier = Mini.isGrownUp() ? 0.66f : 2f;
                    me.SetKillTimer(GameOptionsManager.Instance.currentNormalGameOptions.KillCooldown * multiplier);
                } catch { }
            }
        }

        // ====================================================================
        // Extra-Armored behaviour: block the first murder attempt, break + sync the armor.
        // ====================================================================
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkArmored))]
        static class ArmoredPatch {
            public static void Postfix(ref bool __result, PlayerControl target, bool breakShield,
                                       bool showShield, bool additionalCondition) {
                try {
                    if (__result || target == null || !additionalCondition) return;
                    if (!IsExtraArmored(target.PlayerId) || brokenExtraArmor.Contains(target.PlayerId)) return;

                    if (breakShield) SendBreakExtraArmor(target.PlayerId);
                    if (showShield) target.ShowFailedMurder();
                    __result = true;
                } catch { }
            }
        }

        private static void SendBreakExtraArmor(byte playerId) {
            try {
                MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
                w.Write(SubBreakExtraArmor);
                w.Write(playerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                brokenExtraArmor.Add(playerId);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] break-armor send failed: {e}");
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    if (subtype == SubBreakExtraArmor) brokenExtraArmor.Add(reader.ReadByte());
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] HandleRpc failed: {e}");
                }
                return false;
            }
        }
    }
}
