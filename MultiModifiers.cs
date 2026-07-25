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
 *   - Assignment: getSelectionForRoleId x quantity ONLY - that plugs straight into TOR's own
 *     quantity mechanism (Bloody & co.): 100% puts `quantity` copies into the ensured list,
 *     otherwise `selection x quantity` chance TICKETS go into the pool, so every extra copy
 *     still rolls the spawn chance, and assignModifiersToPlayers draws all modifiers from one
 *     shared player pool = max ONE modifier per person. (An earlier host-side "top-up" that
 *     force-filled the quantity after the first spawn is deliberately GONE - it bypassed the
 *     chance and could stack Mini+Armored on the same player; user decision 2026-07-05.)
 *   - Body scale + collider: postfix on PlayerControlFixedUpdatePatch.playerSizeUpdate.
 *   - Age suffix on the name tag: postfix on HudManagerUpdatePatch.miniUpdate (internal, reflection).
 *   - Kill protection while not grown up: postfix on Helpers.checkMuderAttempt (SuppressKill).
 *   - Targeting exclusion: prefix on setTarget - whenever a call site excluded TOR's base Mini via
 *     untargetablePlayers, the extras are appended to the same list (mirrors call-site semantics).
 *   - Crew mini voted out young -> Mini lose (Mini.triggerMiniLose), like TOR's WrapUpPostfix.
 *   - Impostor extra mini: kill cooldown x2 young / x0.66 grown, re-applied after kills and meetings.
 *
 * Armored extras:
 *   - Assignment: like the Mini (quantity via TOR's own ticket mechanism).
 *   - Armor block: postfix on Helpers.checkArmored - first kill attempt on an unbroken extra armor
 *     is blocked, the break is synced via our own RPC (245 sub 0) so every client greys the armor.
 *     Ability SELF-probes (bomb plant) are exempt - see BomberArmoredFix.
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

        private const byte RpcId = 245;              // keep globally unique (see ID-Registry.md)
        private const byte SubBreakExtraArmor = 0;   // playerId

        // ALL holders (including the one in TOR's single static). "Extra" = holder != TOR's single.
        private static readonly List<byte> minis = new List<byte>();
        private static readonly List<byte> armoreds = new List<byte>();
        private static readonly HashSet<byte> brokenExtraArmor = new HashSet<byte>();

        private static bool EveryoneHasMod() {
            try { return UsefulVersionHandshake.BuildMismatchMessage() == ""; }
            catch { return false; }
        }

        private static int MiniQty() => MiniQuantity != null ? MiniQuantity.getQuantity() : 1;
        private static int ArmoredQty() => ArmoredQuantity != null ? ArmoredQuantity.getQuantity() : 1;

        // The quantities as they are ACTUALLY applied (extras need the handshake). TrueModifierChances
        // rolls every copy itself and reads them through these.
        public static int EffectiveMiniQuantity() => EveryoneHasMod() ? MiniQty() : 1;
        public static int EffectiveArmoredQuantity() => EveryoneHasMod() ? ArmoredQty() : 1;

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
                UTSLocalization.BindOptionTitle(MiniQuantity, "uts.multimodifiers.mini_quantity");
                ArmoredQuantity = CustomOption.Create(
                    1351, Types.Modifier, "Armored Quantity (max 3)",
                    new string[] { "1", "2", "3" }, CustomOptionHolder.modifierArmored);
                UTSLocalization.BindOptionTitle(ArmoredQuantity, "uts.multimodifiers.armored_quantity");

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
            UTSRpc.Register(RpcId, HandleModuleRpc);
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var rmsr = torAsm.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");

                var gsfr = rmsr?.GetMethod("getSelectionForRoleId", BindingFlags.NonPublic | BindingFlags.Static);
                if (gsfr != null)
                    harmony.Patch(gsfr, postfix: new HarmonyMethod(typeof(MultiModifiers), nameof(GetSelectionForRoleIdPostfix)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning("[MultiModifiers] getSelectionForRoleId not found - multi-assignment disabled.");

                // Age suffix: miniUpdate lives in the internal HudManagerUpdatePatch class.
                var hmu = torAsm.GetType("TheOtherRoles.Patches.HudManagerUpdatePatch");
                var miniUpdate = hmu?.GetMethod("miniUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (miniUpdate != null)
                    harmony.Patch(miniUpdate, postfix: new HarmonyMethod(typeof(MultiModifiers), nameof(MiniUpdatePostfix)));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning("[MultiModifiers] miniUpdate not found - extra-mini age suffix disabled.");

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[MultiModifiers][DIAG] getSelectionForRoleId={(gsfr != null)}, miniUpdate={(miniUpdate != null)}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] TryPatch failed: {e}");
            }
        }

        // ====================================================================
        // Assignment: multiply the ensured spawn count; top up the chance path.
        // ====================================================================
        public static void GetSelectionForRoleIdPostfix(ref int __result, RoleId roleId, bool multiplyQuantity) {
            try {
                // TrueModifierChances already rolled every copy and writes the final ensured count
                // itself - multiplying on top of that would square the quantity.
                if (TrueModifierChances.IsActive) return;
                if (!multiplyQuantity || !EveryoneHasMod()) return;
                if (roleId == RoleId.Mini) __result *= MiniQty();
                else if (roleId == RoleId.Armored) __result *= ArmoredQty();
            } catch { }
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

        // resetVariables alone is NOT enough: it is an RPC only a (same-version) TOR host sends.
        // The lists hold bare PlayerIds, and ids are reused per lobby - carrying entries into a
        // lobby whose host never sends resetVariables (vanilla host / host without the mod) turns
        // whoever now owns those ids into fake extra Minis/Armored on this client. OnGameJoined
        // fires on every lobby (re-)entry, so the lists always start empty there.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch {
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
                    suffix = "<color=#FAD934FF>" + UTSLocalization.Tr("uts.multimodifiers.age_suffix", Mathf.FloorToInt(progress * 18)) + "</color>";
                if (!Mini.isGrowingUpInMeeting && MeetingHud.Instance != null
                    && Mini.ageOnMeetingStart != 0 && !(Mini.ageOnMeetingStart >= 18))
                    suffix = "<color=#FAD934FF>" + UTSLocalization.Tr("uts.multimodifiers.age_suffix", Mini.ageOnMeetingStart) + "</color>";
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
                    // Ability self-probes (bomb plant etc.) never hit armor - see BomberArmoredFix.
                    if (BomberArmoredFix.InSelfCheck) return;
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
                // LEGACY DUAL-SEND (see UTSRpc.cs): legacy callId 245 + consolidated channel 240.
                // Classified IDEMPOTENT: the receiver only does brokenExtraArmor.Add(id) on a
                // HashSet, so a second copy of the same id changes nothing. The legacy half exists
                // for pre-240 builds and can be deleted in a future breaking release.
                UTSRpc.SendDual(RpcId, RpcId, w => { w.Write(SubBreakExtraArmor); w.Write(playerId); });
                brokenExtraArmor.Add(playerId);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] break-armor send failed: {e}");
            }
        }

        // Receiver on the consolidated channel (module byte 245). Registered from TryPatch; the
        // module byte is already consumed, so this starts at the subtype byte exactly as before.
        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte subtype = reader.ReadByte();
                if (subtype == SubBreakExtraArmor) brokenExtraArmor.Add(reader.ReadByte());
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MultiModifiers] HandleRpc failed: {e}");
            }
        }

        // LEGACY DUAL-SEND receiver: still accepts the old standalone callId 245 from pre-240
        // builds. Idempotent, so receiving both copies is harmless.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                HandleModuleRpc(reader);
                return false;
            }
        }
    }
}
