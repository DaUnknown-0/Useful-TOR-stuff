// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorCrashGuards - crash and NRE fixes from the crash/NRE class of the 2026-08-23 full-source audit
 * of TOR 4.8.0 (Audits\TOR-AUDIT-2026-08-23.md, cross-checked against Audits\TOR-ABDECKUNG-2026-08-23.md
 * so nothing here duplicates a fix TorAuditFixes/TorNullGuards/TorPerfFixes/TorRoundFixes/TorUpstreamFixes
 * already carry). Every item below was re-read against the current TheOtherRoles-main workspace before
 * being patched; the audit's line numbers moved a little between revisions but every fundstelle checked
 * out exactly as described.
 *
 * All fixes here are option-less and NOT behind UTSGate, same reasoning as TorNullGuards: they only
 * turn a crash, a freeze, or a per-tick exception storm into normal behaviour, never hand anyone an
 * advantage, and a client running this mod is protected even when the host is not.
 *
 * Findings fixed here:
 *
 *  M-4) Deputy.setHandcuffedKnows - ArgumentException on a double cuff (TheOtherRoles.cs:319-334).
 *      `handcuffedKnows.Add(playerId, handcuffDuration)` assumes the key is not already present, but
 *      a second HandcuffNoticed for the same player (RPC.cs:1264, or a local re-trigger from
 *      CustomButton.cs:267 / UsablesPatch.cs:104,179,220 before the first entry expired or was
 *      cleared by RPC.cs:1267's HandcuffOver) throws before the RemoveAll cleanup and the button-
 *      status/sound tail of the method ever run. A Prefix removes any stale key first (no-op if
 *      absent), so TOR's own Add() always succeeds - the rest of the method is untouched.
 *
 *  M-15) CustomButton.ReloadHotkeys - an unbound Kill/Ability key aborts resetVariables before
 *      assignRoles (Objects/CustomButton.cs:159-179). Q/F-bound buttons resolve their live key via
 *      Rewired's GetFirstButtonMapWithAction; if that action has no binding the call returns null and
 *      `.elementIdentifierName` throws. resetVariables() calls ReloadHotkeys() BEFORE assignRoles() in
 *      the same synchronous call chain (RPC.cs:181-199, RoleAssignmentPatch.cs:55-58's
 *      RoleManagerSelectRolesPatch.Postfix) with nothing between them to catch it, so one unbound key
 *      leaves every client without a role for the round. Rebuilt with a try/catch around each button so
 *      one bad binding cannot take the whole loop - and with it assignRoles() - down.
 *
 *  M-19) IntroPatch's SetRoleTexts dereferences roleInfo before its own null check in EventMode
 *      (Patches/IntroPatch.cs:216-233, nested in class IntroPatch). getRoleInfoForPlayer returns an
 *      empty list when PlayerControl.LocalPlayer is null (a boot/disconnect race during the intro
 *      cutscene), so `infos.Where(...).FirstOrDefault()` can be null - and the EventUtility.isEnabled
 *      block reads `roleInfo.isNeutral`/`.color` six lines before TOR's own `if (roleInfo != null)`
 *      guard runs. SetUpRoleTextPatch is a private class nested inside the internal IntroPatch, so it
 *      is resolved the same way MultiJester.cs already resolves this exact type (TryPatch, its own
 *      postfix on SetRoleTexts) - Assembly.GetType with the '+' nested-type separator. Our Prefix
 *      fully replaces the method (faithful copy of IntroPatch.cs:216-249, minus the missing guard) and
 *      returns false; MultiJester's postfix on the same method still runs afterward regardless (a
 *      skipped original never skips postfixes), so the two patches do not conflict.
 *
 *  M-20) MapBehaviourPatch's Trapper branch leaks a GameObject per FixedUpdate for a disconnected
 *      trapped player, and can throw KeyNotFoundException if Trap.trapPlayerIdMap and
 *      Trapper.playersOnMap (kept in sync only by convention across two classes and four write sites)
 *      ever disagree (Patches/MapBehaviourPatch.cs:43-61). TOR instantiates the HerePoint GameObject
 *      BEFORE checking `player == null` and only adds it to the tracked `herePoints` dictionary AFTER
 *      that check - so a disconnected trapped player's entry is instantiated fresh, and leaked, every
 *      single tick for as long as they stay in Trapper.playersOnMap. Separately, `Trap.trapPlayerIdMap
 *      [playerId]` is indexed unconditionally. Fixing this from the display method itself would mean
 *      rebuilding TOR's own MapBehaviourPatch.Postfix (a nested Harmony patch stub, not a normal
 *      method) or duplicating its rendering logic. Instead we add our own Prefix directly on the
 *      vanilla MapBehaviour.FixedUpdate: Harmony always runs every prefix before every postfix on the
 *      same target regardless of what any prefix returns, so ours runs strictly before TOR's Postfix
 *      every time. It only sanitizes the shared Trapper.playersOnMap list in place (dropping ids whose
 *      player is gone or whose trap record is missing) before TOR's own loop ever reads it - no
 *      rendering logic duplicated, nothing about a healthy trap touched.
 *
 *  M-36, second half) SurveillanceMinigamePatch.resetNightVision throws inside its own dead-body
 *      colour loop on a disconnected body owner (Patches/UsablesPatch.cs:677-703). TorAuditFixes' A8
 *      already guards the METHOD START (`nightVisionOverlays == null`, UsablesPatch.cs:678) - a
 *      different defect on the same method, not this one. Deeper in the same method, inside the
 *      per-player dead-body loop, `GameData.Instance.GetPlayerById(deadBody.ParentId).Object.Data
 *      .DefaultOutfit.ColorId` is read unconditionally; a disconnected body owner makes GetPlayerById
 *      (or its `.Object`) null, throws, and aborts the OUTER PlayerControl loop mid-iteration - every
 *      player after the current one in AllPlayerControls keeps its lights-out night-vision look
 *      forever. The crash sits inside a loop nested inside another loop in the middle of the method,
 *      not at its start or end, so per the "too risky to reach with a Prefix/Finalizer alone" rule the
 *      method is rebuilt faithfully (verbatim copy of UsablesPatch.cs:677-703) with one added guard:
 *      skip a dead body whose owner has already left. A8's own guard is reproduced too (defensively,
 *      independent of prefix-ordering between the two patches).
 *
 *  M-38) Footprint's default-colour branch NREs forever once its owner disconnects
 *      (Objects/Footprint.cs:78-109, FootprintHolder.FootprintUpdate, InvokeRepeating every 0.1s). A
 *      Footprint stores a snapshot of `player.Data` at creation time; once that reference is null
 *      (the owner disconnected right as the print was made, or since), the fallback colour branch
 *      reads `activeFootprint.Data.DefaultOutfit.ColorId` unconditionally and throws - and because the
 *      throw happens BEFORE `activeFootprint.Lifetime -= dt` on the same iteration, the entry's
 *      lifetime never ticks down and it throws again on literally every following 0.1s tick for the
 *      rest of the round, permanently blocking every OTHER active footprint later in the same list
 *      from getting its fade update that tick too. A Finalizer cannot fix this (same reasoning as
 *      TorNullGuards item 7 for the structurally identical Bloody case): the stale entry would keep
 *      throwing on the very next tick. FootprintHolder's pooling internals are private, so the fix
 *      pre-filters `_activeFootprints` by reflection before TOR's own loop runs, replicating exactly
 *      what TOR's own `_toRemove` cleanup does for a normally-expired print (deactivate, return to
 *      `_pool`) so nothing is leaked by removing it early.
 *
 *  M-47b) propHuntSetRevealed/SetInvis/SetSpeedboost - ArgumentException on a duplicate key
 *      (RPC.cs:1232-1242). All three call `.Add(playerId, duration)` on a `Dictionary<byte, float>`
 *      unconditionally; a second reveal/invis/speedboost RPC for a player already inside the effect's
 *      window (two hunters targeting the same prop-hunt reveal near-simultaneously, or a resend) finds
 *      the key already present and throws. The audit's OTHER half of M-47 (remove-during-enumeration
 *      in PropHunt.cs) is the same net6.0 non-issue already established for H-7/M-47a - see
 *      Audits\TOR-ABDECKUNG-2026-08-23.md - and is not touched here. Each of the three RPCProcedure
 *      methods is rebuilt as a one-line Prefix using the dictionary indexer instead of Add (refreshing
 *      the timer on a repeat trigger instead of throwing); PropHunt's fields are internal, resolved by
 *      reflection, but Dictionary<byte, float> itself is a fully public generic instantiation so the
 *      fetched value can be cast directly (unlike M-20's Trap.trapPlayerIdMap, keyed on the internal
 *      Trap type, which needs the non-generic IDictionary interface instead).
 *
 *  Section-3 robustness items named explicitly for this pass (mediumSetTarget, sidekickPromotes-Race,
 *  Arrow-Find - all NRE-family bugs reachable through a disconnect or a duplicate-trigger race):
 *
 *  - mediumSetTarget (Patches/PlayerControlPatch.cs:743-756): guards `AllVents == null` but not an
 *      EMPTY vent list; `AllVents.FirstOrDefault()` is null on an empty (but non-null) list and
 *      `.UsableDistance` throws. Reachable in the brief window after MapUtilities.CachedShipStatus is
 *      assigned but before its vent list is populated (map load/transition race). Guarded with the
 *      missing `Count == 0` check, otherwise returning early exactly like TOR's own existing guards.
 *
 *  - sidekickPromotes-Race (RPC.cs:722-730, three independent trigger sites at
 *      Patches/PlayerControlPatch.cs:268, 1231, 1416): a duplicate or stale SidekickPromotes RPC
 *      (the murder-trigger and the disconnect-poll trigger racing for the same event, or an RPC that
 *      crosses a round reset) finds Sidekick.sidekick already cleared by the first call's
 *      clearAndReload(). TOR's own Jackal.removeCurrentJackal() then indexes `jackal.PlayerId`
 *      unconditionally once Jackal.jackal is null too, throwing on every client that processes the
 *      duplicate - and even where it does not throw, a second promotion would wipe the freshly
 *      promoted Jackal.jackal back to null. Guarded at the root: skip the whole promotion when there
 *      is no Sidekick left to promote (every legitimate, same-round call always has one, since all
 *      three trigger sites check `Sidekick.sidekick != null` themselves before calling this).
 *
 *  - Arrow-Find (Objects/Arrow.cs:44-49, Arrow.UpdateProximity, the Tracker's proximity meter):
 *      `GameObject.Instantiate(GameObject.Find("ImpostorDetector"), ...)` on the FIRST proximity tick
 *      of the round. If that named HUD template is not present (a HUD variant, or another mod having
 *      renamed/removed it), Find returns null and Instantiate(null, ...) throws - every tick, forever,
 *      since Tracker.DangerMeterParent never gets set and the creation guard keeps retrying. Skipped
 *      for that tick when the template is missing; retried automatically next tick.
 *
 * Deliberately NOT fixed here, with reasons:
 *
 *  M-46) EnumerationHelpers.GetFastEnumerator<T> (Utilities/EnumerationHelpers.cs:13-84) snapshots the
 *      Il2Cpp List<T>'s raw backing-array pointer and length at enumerator construction, then walks
 *      that snapshot with unsafe pointer arithmetic. If the underlying Il2Cpp list reallocates its
 *      backing array WHILE an enumerator built from the old snapshot is still being read, the pointer
 *      is left dangling: undefined behaviour, not a normal CLR exception. .NET does not deliver
 *      AccessViolationException-class corrupted-state failures to an ordinary catch block by default,
 *      so the Prefix/Finalizer "swallow and continue" technique used everywhere else in this file
 *      (and in TorNullGuards/TorAuditFixes) is not effective against this particular failure mode -
 *      there is no safe managed catch point to add. A real fix means either re-validating the pointer
 *      on every MoveNext() (which mostly defeats the point of skipping Il2Cpp's own bounds-checked
 *      accessor) or moving all ~15 call sites across many files back to safe enumeration, which is
 *      both outside this file's one-file mandate and its own separate risk assessment. In practice,
 *      every caller checked here (setTarget included, PlayerControlPatch.cs:24-49) runs synchronously
 *      on Unity's single main thread with no re-entrant Add/Remove on the SAME list from within its
 *      own loop body, so the actual reachability of the race in normal play looks low - but that is a
 *      per-call-site judgement call, not a property of the unsafe class itself, and was not something
 *      a single crash-guard file could responsibly re-verify for all 15 sites. Left open.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Reactor.Utilities.Extensions;
using Rewired;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    public static class TorCrashGuards {
        // ── M-4) a double cuff must refresh the timer, not throw on Dictionary.Add ──────────────
        [HarmonyPatch(typeof(Deputy), nameof(Deputy.setHandcuffedKnows))]
        static class DeputyHandcuffDoubleAddPatch {
            public static void Prefix(bool active, byte playerId) {
                try {
                    if (!active) return;
                    byte target = playerId == Byte.MaxValue ? PlayerControl.LocalPlayer.PlayerId : playerId;
                    // No-op if absent; guarantees TOR's own Add() right after this cannot throw.
                    Deputy.handcuffedKnows.Remove(target);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] handcuff double-cuff guard failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── M-15) an unbound Kill/Ability key must not abort resetVariables before assignRoles ──
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.ReloadHotkeys))]
        static class ReloadHotkeysGuardPatch {
            // Faithful rebuild of Objects/CustomButton.cs:159-179, with a try/catch around each
            // button so one unbound Rewired action cannot take the rest of the loop down with it.
            public static bool Prefix() {
                try {
                    foreach (var button in CustomButton.buttons) {
                        try {
                            if (button.originalHotkey == KeyCode.Q) {
                                Player player = Rewired.ReInput.players.GetPlayer(0);
                                var map = player.controllers.maps.GetFirstButtonMapWithAction(8, true);
                                if (map != null) button.hotkey = (KeyCode)Enum.Parse(typeof(KeyCode), map.elementIdentifierName);
                            }
                            if (button.originalHotkey == KeyCode.F) {
                                Player player = Rewired.ReInput.players.GetPlayer(0);
                                var map = player.controllers.maps.GetFirstButtonMapWithAction(49, true);
                                if (map != null) button.hotkey = (KeyCode)Enum.Parse(typeof(KeyCode), map.elementIdentifierName);
                            }
                            if (button.originalHotkey == KeyCode.G) button.hotkey = CustomButton.Action2Keycode;
                            if (button.originalHotkey == KeyCode.H) button.hotkey = CustomButton.Action3Keycode;
                        } catch (Exception inner) {
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                $"[TorCrashGuards] ReloadHotkeys skipped one button (unbound key?): {inner.GetType().Name}: {inner.Message}");
                        }
                    }
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError(
                        $"[TorCrashGuards] ReloadHotkeys rebuild failed, falling back to TOR's own (may abort resetVariables before assignRoles): {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── M-19) the intro role card must not read roleInfo before its own null check ──────────
        [HarmonyPatch]
        static class IntroRoleTextNullGuardPatch {
            // SetUpRoleTextPatch is a private class nested inside the internal IntroPatch
            // (Patches/IntroPatch.cs:150,214) - the same type MultiJester.cs already resolves for its
            // own postfix on this exact method, via the identical Assembly.GetType lookup.
            private static readonly Type setRoleTextsType =
                typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.IntroPatch+SetUpRoleTextPatch");
            private static readonly FieldInfo seedField =
                setRoleTextsType?.GetField("seed", BindingFlags.NonPublic | BindingFlags.Static);

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                return setRoleTextsType?.GetMethod("SetRoleTexts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            // Faithful rebuild of IntroPatch.cs:216-249, with one added guard: skip the EventMode
            // re-roll when roleInfo is null instead of dereferencing it. MultiJester.cs patches a
            // Postfix onto this same method (TryPatch) - a skipped original never skips postfixes on
            // the same target, so returning false here does not stop MultiJester's card re-stamp from
            // running right after.
            [HarmonyPrefix]
            public static bool Prefix(IntroCutscene __instance) {
                try {
                    List<RoleInfo> infos = RoleInfo.getRoleInfoForPlayer(PlayerControl.LocalPlayer);
                    RoleInfo roleInfo = infos.Where(info => !info.isModifier).FirstOrDefault();
                    RoleInfo modifierInfo = infos.Where(info => info.isModifier).FirstOrDefault();

                    if (EventUtility.isEnabled && roleInfo != null) {
                        var roleInfos = RoleInfo.allRoleInfos.Where(x => !x.isModifier).ToList();
                        if (roleInfo.isNeutral) roleInfos.RemoveAll(x => !x.isNeutral);
                        if (roleInfo.color == Palette.ImpostorRed) roleInfos.RemoveAll(x => x.color != Palette.ImpostorRed);
                        if (!roleInfo.isNeutral && roleInfo.color != Palette.ImpostorRed) roleInfos.RemoveAll(x => x.color == Palette.ImpostorRed || x.isNeutral);
                        int seed = seedField != null ? (int)seedField.GetValue(null) : 0;
                        var rnd = new System.Random(seed);
                        if (roleInfos.Count > 0) roleInfo = roleInfos[rnd.Next(roleInfos.Count)];
                    }

                    __instance.RoleBlurbText.text = "";
                    if (roleInfo != null) {
                        __instance.RoleText.text = roleInfo.name;
                        __instance.RoleText.color = roleInfo.color;
                        __instance.RoleBlurbText.text = roleInfo.introDescription;
                        __instance.RoleBlurbText.color = roleInfo.color;
                    }
                    if (modifierInfo != null) {
                        if (modifierInfo.roleId != RoleId.Lover)
                            __instance.RoleBlurbText.text += Helpers.cs(modifierInfo.color, $"\n{modifierInfo.introDescription}");
                        else {
                            PlayerControl otherLover = PlayerControl.LocalPlayer == Lovers.lover1 ? Lovers.lover2 : Lovers.lover1;
                            // \u2665 is TOR's own heart glyph (IntroPatch.cs:239) - written as an
                            // escape so this source file stays ASCII-only while the on-screen text
                            // (a faithful copy of TOR's own) stays byte-for-byte identical.
                            __instance.RoleBlurbText.text += Helpers.cs(Lovers.color, $"\n\u2665 You are in love with {otherLover?.Data?.PlayerName ?? ""} \u2665");
                        }
                    }
                    if (Deputy.knowsSheriff && Deputy.deputy != null && Sheriff.sheriff != null) {
                        if (infos.Any(info => info.roleId == RoleId.Sheriff))
                            __instance.RoleBlurbText.text += Helpers.cs(Sheriff.color, $"\nYour Deputy is {Deputy.deputy?.Data?.PlayerName ?? ""}");
                        else if (infos.Any(info => info.roleId == RoleId.Deputy))
                            __instance.RoleBlurbText.text += Helpers.cs(Sheriff.color, $"\nYour Sheriff is {Sheriff.sheriff?.Data?.PlayerName ?? ""}");
                    }
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[TorCrashGuards] SetRoleTexts rebuild failed, falling back to TOR's own (may still throw on the null-roleInfo path): {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── M-20) the Trapper map display must not leak GameObjects or index a missing trap ─────
        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        static class TrapperMapDesyncGuardPatch {
            // Trap is internal to TOR; trapPlayerIdMap is keyed on the internal Trap type itself, so
            // (unlike M-47b's PropHunt dictionaries) it cannot be cast to a named generic Dictionary
            // from outside - read through the non-generic IDictionary interface instead.
            private static readonly Type trapType = AccessTools.TypeByName("TheOtherRoles.Objects.Trap");
            private static readonly FieldInfo trapPlayerIdMapField = AccessTools.Field(trapType, "trapPlayerIdMap");

            // A Prefix on the vanilla MapBehaviour.FixedUpdate always runs before every Postfix on the
            // same target regardless of what any prefix returns (Harmony composes prefixes -> original
            // -> postfixes) - so this runs strictly before TOR's own MapBehaviourPatch.Postfix every
            // time, without needing to touch or duplicate that nested Harmony patch stub at all.
            public static void Prefix() {
                try {
                    if (trapPlayerIdMapField?.GetValue(null) is not IDictionary trapMap) return;
                    List<byte> playersOnMap = Trapper.playersOnMap;
                    if (playersOnMap == null) return;
                    for (int i = playersOnMap.Count - 1; i >= 0; i--) {
                        byte playerId = playersOnMap[i];
                        bool ownerGone = Helpers.playerById(playerId) == null;
                        bool tracked = trapMap.Contains(playerId);
                        if (!ownerGone && tracked) continue;
                        // Either the trapped player disconnected (TOR instantiates a HerePoint
                        // GameObject BEFORE its own player==null check and only registers it in
                        // herePoints AFTER that check, so this entry leaks one orphaned GameObject
                        // every FixedUpdate for as long as it stays here) or trapPlayerIdMap lost the
                        // entry while playersOnMap kept it (TOR indexes trapPlayerIdMap[playerId]
                        // unconditionally, KeyNotFoundException). Dropping the id here prevents both
                        // before TOR's own display loop ever sees it.
                        playersOnMap.RemoveAt(i);
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[TorCrashGuards] dropped stale Trapper.playersOnMap entry for player {playerId} " +
                            $"(disconnected={ownerGone}, missing trap record={!tracked}).");
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] Trapper map desync guard failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── M-36, second half) resetNightVision's dead-body loop must survive a disconnected owner ─
        [HarmonyPatch]
        static class ResetNightVisionDeadBodyGuardPatch {
            private static readonly Type surveillanceType = AccessTools.TypeByName("TheOtherRoles.Patches.SurveillanceMinigamePatch");
            private static readonly FieldInfo overlaysField = AccessTools.Field(surveillanceType, "nightVisionOverlays");
            private static readonly FieldInfo isActiveField = AccessTools.Field(surveillanceType, "nightVisionIsActive");

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                return surveillanceType == null ? null : AccessTools.Method(surveillanceType, "resetNightVision");
            }

            // Faithful rebuild of UsablesPatch.cs:677-703. TorAuditFixes' A8 already guards the
            // `nightVisionOverlays == null` case at the top of this method (a different defect); that
            // guard is reproduced here too, defensively, independent of which patch happens to run
            // first. The one behavioural change from TOR 4.8.0 is the `ownerInfo` guard inside the
            // dead-body loop: a disconnected body owner used to throw and abort every player after
            // the current one in the outer AllPlayerControls loop for the rest of the round.
            [HarmonyPrefix]
            public static bool Prefix() {
                try {
                    if (overlaysField == null || isActiveField == null) return true;

                    if (overlaysField.GetValue(null) is List<GameObject> overlays)
                        foreach (var go in overlays) go?.Destroy();
                    overlaysField.SetValue(null, null);

                    bool wasActive = (bool)isActiveField.GetValue(null);
                    if (wasActive) {
                        isActiveField.SetValue(null, false);
                        foreach (PlayerControl pc in PlayerControl.AllPlayerControls) {
                            if (Camouflager.camouflageTimer > 0) {
                                pc.setLook("", 6, "", "", "", "", false);
                            } else if (pc == Morphling.morphling && Morphling.morphTimer > 0) {
                                PlayerControl target = Morphling.morphTarget;
                                Morphling.morphling.setLook(target.Data.PlayerName, target.Data.DefaultOutfit.ColorId, target.Data.DefaultOutfit.HatId, target.Data.DefaultOutfit.VisorId, target.Data.DefaultOutfit.SkinId, target.Data.DefaultOutfit.PetId, false);
                            } else if (pc == Ninja.ninja && Ninja.invisibleTimer > 0f) {
                                continue;
                            } else {
                                Helpers.setDefaultLook(pc, false);
                            }
                            foreach (DeadBody deadBody in GameObject.FindObjectsOfType<DeadBody>()) {
                                // TOR-M36: GetPlayerById returns null once the body's owner has
                                // disconnected; TOR's own resetNightVision dereferences
                                // .Object.Data.DefaultOutfit.ColorId unconditionally here
                                // (UsablesPatch.cs:697), throwing and aborting this whole
                                // PlayerControl loop mid-iteration.
                                var ownerInfo = GameData.Instance.GetPlayerById(deadBody.ParentId);
                                if (ownerInfo == null || ownerInfo.Object == null) continue;
                                var colorId = ownerInfo.Object.Data.DefaultOutfit.ColorId;
                                SpriteRenderer component = deadBody.bodyRenderers.FirstOrDefault();
                                component.material.SetColor("_BackColor", Palette.ShadowColors[colorId]);
                                component.material.SetColor("_BodyColor", Palette.PlayerColors[colorId]);
                            }
                        }
                    }
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[TorCrashGuards] resetNightVision rebuild failed, falling back to TOR's own: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── M-38) a footprint whose owner disconnected must not throw on every 0.1s tick forever ─
        [HarmonyPatch]
        static class FootprintDisconnectGuardPatch {
            // FootprintHolder is public but its pooling internals (the nested Footprint class,
            // _activeFootprints, _pool) are private - resolved by name once, mirroring TorNullGuards
            // item 7's precedent for the structurally identical Bloody-killer-left-the-lobby case.
            private static readonly Type footprintType = typeof(FootprintHolder).GetNestedType("Footprint", BindingFlags.NonPublic);
            private static readonly FieldInfo activeFootprintsField = AccessTools.Field(typeof(FootprintHolder), "_activeFootprints");
            private static readonly FieldInfo poolField = AccessTools.Field(typeof(FootprintHolder), "_pool");
            private static readonly FieldInfo dataField = footprintType == null ? null : AccessTools.Field(footprintType, "Data");
            private static readonly FieldInfo gameObjectField = footprintType == null ? null : AccessTools.Field(footprintType, "GameObject");
            private static readonly MethodInfo poolAddMethod = poolField == null ? null : poolField.FieldType.GetMethod("Add");

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                return AccessTools.Method(typeof(FootprintHolder), "FootprintUpdate");
            }

            // A footprint's Data is a snapshot of player.Data taken at creation time
            // (Objects/Footprint.cs:68); once that snapshot is null (the owner disconnected right as
            // the print was made, or since) the default-colour branch reads
            // activeFootprint.Data.DefaultOutfit.ColorId unconditionally (Footprint.cs:95) and
            // throws - every 0.1s forever, because the crash sits BEFORE
            // activeFootprint.Lifetime -= dt, so the entry never ages out and never reaches TOR's own
            // removal loop either. A Finalizer alone would not fix that (same reasoning as
            // TorNullGuards item 7): the stale entry would keep throwing on the very next tick.
            // Pre-filtering the list before TOR's own loop runs - and replicating exactly what TOR's
            // own _toRemove cleanup does for a normally-expired print (deactivate, return to _pool) -
            // is the only way that actually clears it without leaking the pooled GameObject.
            [HarmonyPrefix]
            public static void Prefix(object __instance) {
                try {
                    if (activeFootprintsField == null || dataField == null) return;
                    if (activeFootprintsField.GetValue(__instance) is not IList list) return;
                    object pool = poolField?.GetValue(__instance);
                    for (int i = list.Count - 1; i >= 0; i--) {
                        object footprint = list[i];
                        if (footprint == null) { list.RemoveAt(i); continue; }
                        if (dataField.GetValue(footprint) != null) continue; // healthy entry

                        if (gameObjectField?.GetValue(footprint) is GameObject go && go != null) go.SetActive(false);
                        list.RemoveAt(i);
                        if (pool != null && poolAddMethod != null) poolAddMethod.Invoke(pool, new[] { footprint });
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            "[TorCrashGuards] dropped a footprint whose owner's Data went stale (disconnect) " +
                            "before FootprintUpdate could throw on it every tick.");
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] footprint disconnect guard failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── M-47b) a duplicate PropHunt effect trigger must refresh the timer, not throw ─────────
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.propHuntSetRevealed))]
        static class PropHuntSetRevealedPatch {
            private static readonly Type propHuntType = AccessTools.TypeByName("TheOtherRoles.CustomGameModes.PropHunt");
            private static readonly FieldInfo revealedField = AccessTools.Field(propHuntType, "isCurrentlyRevealed");
            private static readonly FieldInfo revealDurationField = AccessTools.Field(propHuntType, "revealDuration");
            private static readonly FieldInfo timerField = AccessTools.Field(propHuntType, "timer");
            private static readonly FieldInfo revealPunishField = AccessTools.Field(propHuntType, "revealPunish");

            public static bool Prefix(byte playerId) {
                try {
                    if (revealedField?.GetValue(null) is not Dictionary<byte, float> dict) return true;
                    SoundEffectsManager.play("morphlingMorph");
                    dict[playerId] = revealDurationField != null ? (float)revealDurationField.GetValue(null) : 0f;
                    if (timerField != null && revealPunishField != null)
                        timerField.SetValue(null, (float)timerField.GetValue(null) - (float)revealPunishField.GetValue(null));
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] propHuntSetRevealed guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.propHuntSetInvis))]
        static class PropHuntSetInvisPatch {
            private static readonly Type propHuntType = AccessTools.TypeByName("TheOtherRoles.CustomGameModes.PropHunt");
            private static readonly FieldInfo invisField = AccessTools.Field(propHuntType, "invisPlayers");
            private static readonly FieldInfo invisDurationField = AccessTools.Field(propHuntType, "invisDuration");

            public static bool Prefix(byte playerId) {
                try {
                    if (invisField?.GetValue(null) is not Dictionary<byte, float> dict) return true;
                    dict[playerId] = invisDurationField != null ? (float)invisDurationField.GetValue(null) : 0f;
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] propHuntSetInvis guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.propHuntSetSpeedboost))]
        static class PropHuntSetSpeedboostPatch {
            private static readonly Type propHuntType = AccessTools.TypeByName("TheOtherRoles.CustomGameModes.PropHunt");
            private static readonly FieldInfo speedboostField = AccessTools.Field(propHuntType, "speedboostActive");
            private static readonly FieldInfo speedboostDurationField = AccessTools.Field(propHuntType, "speedboostDuration");

            public static bool Prefix(byte playerId) {
                try {
                    if (speedboostField?.GetValue(null) is not Dictionary<byte, float> dict) return true;
                    dict[playerId] = speedboostDurationField != null ? (float)speedboostDurationField.GetValue(null) : 0f;
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] propHuntSetSpeedboost guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── mediumSetTarget must not index the first vent of an empty (but non-null) list ────────
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch),
            nameof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch.mediumSetTarget))]
        static class MediumSetTargetEmptyVentsGuardPatch {
            // TOR guards `AllVents == null` (Patches/PlayerControlPatch.cs:743-744) but not an EMPTY
            // list - `AllVents.FirstOrDefault().UsableDistance` throws on a null FirstOrDefault()
            // result, reachable in the brief window after MapUtilities.CachedShipStatus is assigned
            // but before its vent list is populated (a map load/transition race).
            public static bool Prefix() {
                try {
                    if (Medium.medium == null || Medium.medium != PlayerControl.LocalPlayer || Medium.medium.Data.IsDead
                        || Medium.deadBodies == null || MapUtilities.CachedShipStatus?.AllVents == null)
                        return true; // TOR's own guard already returns safely on these
                    if (MapUtilities.CachedShipStatus.AllVents.Count == 0) return false; // nothing to measure against yet
                    return true;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] mediumSetTarget empty-vents guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── sidekickPromotes-Race: a duplicate/stale promotion must not throw or re-wipe Jackal ──
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.sidekickPromotes))]
        static class SidekickPromotesRaceGuardPatch {
            public static bool Prefix() {
                try {
                    if (Sidekick.sidekick != null) return true; // normal path, original is safe
                    // Every legitimate, same-round call site checks Sidekick.sidekick != null itself
                    // before calling this (PlayerControlPatch.cs:255,1231,1416); reaching here with it
                    // already null means a second RPC for the same promotion (murder-trigger and
                    // disconnect-poll trigger racing for the same event) or a stale RPC that crossed a
                    // round reset. TOR's own Jackal.removeCurrentJackal() indexes jackal.PlayerId
                    // unconditionally once Jackal.jackal is null too - nothing left to promote a
                    // second time, the first call already did the whole job.
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        "[TorCrashGuards] dropped a duplicate/stale sidekickPromotes (no Sidekick left to promote).");
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] sidekickPromotes race guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── Arrow-Find: a missing HUD template must not throw on every tracking tick ─────────────
        [HarmonyPatch(typeof(Arrow), nameof(Arrow.UpdateProximity))]
        static class ArrowUpdateProximityMissingHudTemplatePatch {
            // Objects/Arrow.cs:47-49: on the first proximity tick, TOR instantiates the Tracker's
            // danger meter from a HUD template found by name ("ImpostorDetector"). If that object is
            // not in the HUD hierarchy yet (a HUD variant, or another mod having renamed/removed it),
            // GameObject.Find returns null and Instantiate(null, ...) throws - every tick, forever,
            // because Tracker.DangerMeterParent never gets set and the creation guard keeps retrying.
            public static bool Prefix() {
                try {
                    if (Tracker.DangerMeterParent != null) return true; // already created, original is safe
                    if (GameObject.Find("ImpostorDetector") != null) return true; // template present, original is safe
                    return false; // skip this tick; retried automatically next tick
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorCrashGuards] Arrow.UpdateProximity HUD-template guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }
    }
}
