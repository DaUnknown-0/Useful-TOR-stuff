// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TrueModifierChances - turns TOR's modifier percentages into REAL, independent spawn chances.
 *
 * WHY
 * TOR's assignModifiers (RoleAssignmentPatch.cs:419-489) never rolls the configured percentage
 * for a modifier; it treats the percentages as LOTTERY TICKETS:
 *   1. the modifier COUNT is rolled first: rnd.Next(modifierMin, modifierMax + 1),
 *   2. every modifier at selection == 10 (100%) goes straight into `ensuredModifiers`
 *      (`getSelectionForRoleId(m, true) / 10` copies), every other one throws
 *      `selection x quantity` tickets into `chanceModifiers` (lines 464-466),
 *   3. the remaining slots are then FILLED UP from that ticket pool until they are full
 *      (lines 475-486).
 * So the percentages only act as a relative WEIGHT: with enough slots for everything, a 10%
 * modifier spawns every single round; with few slots even a 90% modifier is missing constantly.
 * The only correct roll in TOR is the Lovers pair (line 448: rnd.Next(1, 101) <= selection * 10).
 *
 * WHAT THIS DOES (host-side, opt-in via the option below, default OFF)
 * Once per game, before TOR assigns anything, we roll every modifier - and for quantity modifiers
 * EVERY COPY separately - against its real percentage. Winners are then handed to TOR through its
 * OWN machinery instead of reimplementing the assignment:
 *   - prefix on RoleManagerSelectRolesPatch.assignModifiers: do the rolls, store `wonCopies`,
 *   - postfix on RoleManagerSelectRolesPatch.getSelectionForRoleId (the ONLY place TOR reads the
 *     modifier percentages, all three call sites sit inside assignModifiers):
 *       loser  (0 won copies)          -> __result = 0        (not ensured, and `Repeat(m, 0)`
 *                                                              puts ZERO tickets in the pool)
 *       winner (multiplyQuantity=false)-> __result = 10       (=> TOR takes the ENSURED branch)
 *       winner (multiplyQuantity=true) -> __result = 10 * won (=> TOR's `/10` yields exactly
 *                                                              `won` guaranteed copies)
 * The chance ticket pool therefore stays EMPTY and TOR's broken fill-up path runs dry, while the
 * Shifter/Sunglasses special casing, the Guesser gamemode filter and the shared player pool in
 * assignModifiersToPlayers stay completely untouched.
 *
 * The "Minimum/Maximum Modifiers" option becomes a pure CAP: TOR's assignModifiersToPlayers drops
 * random entries while `modifierCount < modifiers.Count`, so more winners than slots are trimmed
 * randomly (logged below). Fewer winners than slots simply means fewer modifiers this round -
 * which is the whole point of real chances.
 *
 * LOVER IS DELIBERATELY NOT TOUCHED: TOR already rolls it correctly and it never runs through
 * getSelectionForRoleId during assignment; rolling it again would square its chance.
 *
 * MINI + EVENT MODE: TOR forces the Mini selection to 10 while EventUtility.isEnabled (unless
 * "Really No Mini :(" is set at 0%). We mirror that rule, so during the event the Mini is a
 * fixed winner instead of a rolled one.
 *
 * COORDINATION WITH THE OTHER UTS MODULES (they patch the same getSelectionForRoleId):
 *   - MultiModifiers (Mini/Armored) and TiebreakerMultiple multiply __result by their quantity.
 *     While this feature is active they return early (guard on `IsActive`) - we read those
 *     quantities ourselves (MultiModifiers.EffectiveMiniQuantity/EffectiveArmoredQuantity,
 *     TiebreakerMultiple.EffectiveQuantity) and roll each copy, so nothing is multiplied twice.
 *   - TiebreakerMultiple.TopUp() (postfix on assignModifiers) force-fills the Tiebreaker quantity
 *     and would fill right past our roll -> also guarded off while this feature is active.
 *
 * GATING: `IsActive` is only true when the option is ON *and* both reflection targets were
 * resolved. If TOR ever renames them, the guards above stay inactive too, so the plugin falls
 * back to the previous behaviour as a whole instead of half-applying it.
 *
 * HOST ONLY: assignModifiers runs on the host exclusively, the results reach everybody through
 * TOR's own SetModifier RPCs. No handshake, no own RPC, works with plain-TOR clients.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class TrueModifierChances {
        public static CustomOption Option;   // 1375 - see ID-Registry.md

        // Every modifier TOR rolls in assignModifiers (its `allModifiers` list, line 434-446).
        // The Lover is NOT part of it - TOR rolls the pair separately and correctly.
        private static readonly RoleId[] RolledModifiers = {
            RoleId.Tiebreaker,
            RoleId.Mini,
            RoleId.Bait,
            RoleId.Bloody,
            RoleId.AntiTeleport,
            RoleId.Sunglasses,
            RoleId.Vip,
            RoleId.Invert,
            RoleId.Chameleon,
            RoleId.Armored,
            RoleId.Shifter
        };

        // Per-game roll result and what TOR actually handed out (for the cap log).
        private static readonly Dictionary<RoleId, int> wonCopies = new Dictionary<RoleId, int>();
        private static readonly Dictionary<RoleId, int> assignedCopies = new Dictionary<RoleId, int>();

        // Armed by the assignModifiers prefix on the host only: getSelectionForRoleId must only be
        // rewritten inside that one assignment window.
        private static bool assignmentActive;

        // Both reflection targets resolved -> we are actually able to take over the assignment.
        private static bool patchesReady;

        /// True while the feature is in charge. The other modules' getSelectionForRoleId postfixes
        /// and TiebreakerMultiple.TopUp bail out on this so nothing is applied twice.
        public static bool IsActive => patchesReady && Option != null && UTSGate.Bool(Option);

        // ====================================================================
        // Option
        // ====================================================================
        public static void CreateOptions() {
            try {
                // ID 1375 (free per ID-Registry.md; 1370-1374 belong to ImpostorCountRange).
                // Duplicate ids scramble each other's selections via TOR's id-delta sync.
                Option = CustomOption.Create(
                    1375, Types.Modifier, "True Modifier Chances", false, null, true);
                UTSLocalization.BindOptionTitle(Option, "uts.truemodifierchances.enabled");

                // Very top of the Modifier tab, above TOR's first modifier option.
                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.modifiersAreHidden);
                if (idx < 0) idx = opts.Count;
                opts.Insert(idx, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[TrueModifierChances] Option created (Modifier tab).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TrueModifierChances] CreateOptions failed: {e}");
            }
        }

        // ====================================================================
        // Reflection patches (both targets are private/internal TOR members).
        // ====================================================================
        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var rmsr = torAsm.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");

                // public static void assignModifiers()
                var assignModifiers = rmsr?.GetMethod("assignModifiers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                // private static int getSelectionForRoleId(RoleId roleId, bool multiplyQuantity = false)
                var gsfr = rmsr?.GetMethod("getSelectionForRoleId",
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (assignModifiers != null && gsfr != null) {
                    harmony.Patch(assignModifiers,
                        prefix: new HarmonyMethod(typeof(TrueModifierChances), nameof(AssignModifiersPrefix)),
                        postfix: new HarmonyMethod(typeof(TrueModifierChances), nameof(AssignModifiersPostfix)));
                    harmony.Patch(gsfr,
                        postfix: new HarmonyMethod(typeof(TrueModifierChances), nameof(GetSelectionForRoleIdPostfix)));
                    patchesReady = true;
                } else {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TrueModifierChances] assignModifiers/getSelectionForRoleId not found - feature disabled, "
                        + "TOR's original ticket-pool assignment stays active.");
                }

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[TrueModifierChances][DIAG] Reflection resolved: assignModifiers={(assignModifiers != null)}, "
                    + $"getSelectionForRoleId={(gsfr != null)}, patchesReady={patchesReady}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TrueModifierChances] TryPatch failed: {e}");
            }
        }

        // ====================================================================
        // 1) The rolls - once per game, right before TOR assigns anything.
        // ====================================================================
        public static void AssignModifiersPrefix() {
            wonCopies.Clear();
            assignedCopies.Clear();
            assignmentActive = false;
            try {
                if (!IsActive) return;
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

                var won = new List<string>();
                var lost = new List<string>();
                foreach (RoleId m in RolledModifiers) {
                    int selection = RawSelection(m);           // 0-10 (= 0-100%)
                    int quantity = EffectiveQuantity(m);       // copies this modifier may spawn
                    int hits = 0;
                    for (int i = 0; i < quantity; i++)
                        if (rnd.Next(1, 101) <= selection * 10) hits++;   // TOR's own roll form

                    wonCopies[m] = hits;
                    if (selection <= 0) continue;              // disabled: not worth logging
                    if (hits > 0) won.Add($"{m} {hits}/{quantity} @ {selection * 10}%");
                    else lost.Add($"{m} @ {selection * 10}%");
                }

                assignmentActive = true;
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[TrueModifierChances] Rolled - won: [{string.Join(", ", won)}], lost: [{string.Join(", ", lost)}].");
            } catch (Exception e) {
                // Never leave a half-armed state behind: TOR's original behaviour must stay intact.
                assignmentActive = false;
                wonCopies.Clear();
                UsefulTORStuffPlugin.Logger?.LogError($"[TrueModifierChances] rolling failed - TOR's own assignment stays active: {e}");
            }
        }

        // ====================================================================
        // 2) Redirect TOR's percentage lookup (see the header block for the mapping).
        // ====================================================================
        public static void GetSelectionForRoleIdPostfix(ref int __result, RoleId roleId, bool multiplyQuantity) {
            try {
                if (!assignmentActive) return;
                if (roleId == RoleId.Lover) return;                       // TOR rolls the pair itself
                if (!wonCopies.TryGetValue(roleId, out int won)) return;  // unknown modifier: hands off
                __result = won <= 0 ? 0 : (multiplyQuantity ? 10 * won : 10);
            } catch { }
        }

        // ====================================================================
        // 3) Result log (incl. the max-count cap) + disarm.
        // ====================================================================
        public static void AssignModifiersPostfix() {
            try {
                if (!assignmentActive) return;
                assignmentActive = false;

                int totalWon = 0, totalAssigned = 0;
                var lines = new List<string>();
                foreach (var kv in wonCopies) {
                    if (kv.Value <= 0) continue;
                    assignedCopies.TryGetValue(kv.Key, out int got);
                    totalWon += kv.Value;
                    totalAssigned += got;
                    lines.Add($"{kv.Key} {got}/{kv.Value}");
                }

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[TrueModifierChances] Assigned (actual/won): [{string.Join(", ", lines)}].");
                if (totalAssigned < totalWon)
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[TrueModifierChances] Modifier max-count cap discarded {totalWon - totalAssigned} winning "
                        + "copy/copies (raise \"Maximum Modifiers\" to fit them all).");
            } catch (Exception e) {
                assignmentActive = false;
                UsefulTORStuffPlugin.Logger?.LogError($"[TrueModifierChances] result log failed: {e}");
            }
        }

        // What TOR really handed out during our window (host applies every SetModifier locally too).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.setModifier))]
        static class SetModifierPatch {
            public static void Postfix(byte modifierId) {
                try {
                    if (!assignmentActive) return;
                    var id = (RoleId)modifierId;
                    if (!wonCopies.ContainsKey(id)) return;   // Lover & co: not ours
                    assignedCopies[id] = assignedCopies.TryGetValue(id, out int c) ? c + 1 : 1;
                } catch { }
            }
        }

        // ====================================================================
        // Per-round cleanup. The state is host-only and re-created in the prefix, but stale
        // entries must never survive into another lobby (same reasoning as MultiModifiers).
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { ClearState(); }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch {
            public static void Postfix() { ClearState(); }
        }

        private static void ClearState() {
            assignmentActive = false;
            wonCopies.Clear();
            assignedCopies.Clear();
        }

        // ====================================================================
        // Option readers - mirrors of TOR's getSelectionForRoleId WITHOUT the quantity multiply,
        // read straight from the options so our own postfix can never feed back into them.
        // ====================================================================
        private static int Sel(CustomOption o) => o != null ? o.getSelection() : 0;

        private static int Qty(CustomOption o) => o != null ? o.getQuantity() : 1;

        private static int RawSelection(RoleId roleId) {
            switch (roleId) {
                case RoleId.Tiebreaker: return Sel(CustomOptionHolder.modifierTieBreaker);
                case RoleId.Mini: {
                    int sel = Sel(CustomOptionHolder.modifierMini);
                    // TOR forces the Mini during the event (RoleAssignmentPatch.cs:603-607).
                    if (EventUtility.isEnabled) {
                        sel = 10;
                        if (Sel(CustomOptionHolder.modifierMini) == 0
                            && CustomOptionHolder.eventReallyNoMini != null
                            && CustomOptionHolder.eventReallyNoMini.getBool()) sel = 0;
                    }
                    return sel;
                }
                case RoleId.Bait: return Sel(CustomOptionHolder.modifierBait);
                case RoleId.Bloody: return Sel(CustomOptionHolder.modifierBloody);
                case RoleId.AntiTeleport: return Sel(CustomOptionHolder.modifierAntiTeleport);
                case RoleId.Sunglasses: return Sel(CustomOptionHolder.modifierSunglasses);
                case RoleId.Vip: return Sel(CustomOptionHolder.modifierVip);
                case RoleId.Invert: return Sel(CustomOptionHolder.modifierInvert);
                case RoleId.Chameleon: return Sel(CustomOptionHolder.modifierChameleon);
                case RoleId.Armored: return Sel(CustomOptionHolder.modifierArmored);
                case RoleId.Shifter: return Sel(CustomOptionHolder.modifierShifter);
                default: return 0;
            }
        }

        // How many copies of this modifier may spawn at most: TOR's own quantity options plus the
        // three quantities this plugin adds (Tiebreaker/Mini/Armored). The Mini/Armored extras only
        // exist when every client runs the mod, hence the Effective* helpers.
        private static int EffectiveQuantity(RoleId roleId) {
            switch (roleId) {
                case RoleId.Bait: return Qty(CustomOptionHolder.modifierBaitQuantity);
                case RoleId.Bloody: return Qty(CustomOptionHolder.modifierBloodyQuantity);
                case RoleId.AntiTeleport: return Qty(CustomOptionHolder.modifierAntiTeleportQuantity);
                case RoleId.Sunglasses: return Qty(CustomOptionHolder.modifierSunglassesQuantity);
                case RoleId.Vip: return Qty(CustomOptionHolder.modifierVipQuantity);
                case RoleId.Invert: return Qty(CustomOptionHolder.modifierInvertQuantity);
                case RoleId.Chameleon: return Qty(CustomOptionHolder.modifierChameleonQuantity);
                case RoleId.Tiebreaker: return TiebreakerMultiple.EffectiveQuantity();
                case RoleId.Mini: return MultiModifiers.EffectiveMiniQuantity();
                case RoleId.Armored: return MultiModifiers.EffectiveArmoredQuantity();
                default: return 1;   // Shifter: single-instance in TOR, never multiplied
            }
        }
    }
}
