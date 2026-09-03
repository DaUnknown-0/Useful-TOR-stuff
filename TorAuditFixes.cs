// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorAuditFixes - the remaining crash and behaviour fixes from the 2026-08-17 full-source audit of
 * TOR 4.8.0 (BUG_AUDIT.md in the TOR source folder). Everything here was checked against what this
 * mod pack ALREADY fixes before being written: TorNullGuards covers the /role freeze (A6), the
 * BountyHunter empty-pool crash (A2, via Finalizer), Bomb.explode on destroyed objects, guesserShoot
 * and nine more; TorPerfFixes covers the F1 overlay, GetRolesString and ShowHost rebuild costs;
 * SoundAtPositionFix covers playAtPosition; SnitchLogic covers B5; HostFixPlugin covers the HOST
 * half of the draft softlock (A9). This file only contains what none of them reach.
 *
 * All fixes are option-less and NOT behind UTSGate, for the same reasons as TorNullGuards: they turn
 * crashes/freezes into normal behaviour or restore TOR's own documented intent, and hand nobody an
 * advantage. Where a fix touches ROUND OUTCOMES (the Witch list, B1) the authority is host-side in
 * TOR itself (ExileControllerPatch executes spell kills only on the host), so a client-local fix is
 * authoritative exactly when it matters and merely cosmetic everywhere else.
 *
 * Audit items fixed here (ids from BUG_AUDIT.md):
 *
 *  A1) BountyHunter death spams an NRE every FixedUpdate.
 *      PlayerControlPatch.cs:654: `if (arrow != null || arrow.arrow != null)` - the `||` should be
 *      `&&`. One frame after death nulls the arrow, the right side dereferences null, every frame.
 *      TorNullGuards' Finalizer on the same method contains the throw but logs it every tick; this
 *      Prefix takes over the IsDead branch outright (with the corrected operator) so the exception
 *      never happens. The alive path stays TOR's.
 *
 *  A3) A meeting during an armed bomb crashes Bomb.update().
 *      Bomb.cs:115-119: clearBomb() nulls Bomber.bomb, the very next line dereferences it. The
 *      method is small and self-contained, so it is rebuilt here with a `return` after the clear.
 *
 *  A4) PropHunt: a dying hunter has no SpriteRenderer, MakePropImpostorPatch dereferences it.
 *      Impostors never get one attached (PlayerControlFixedUpdatePatch returns early for them).
 *      The crash sits at the very end of the method (the cosmetic dead-body sprite juggling), so a
 *      Finalizer that swallows it loses nothing that matters.
 *
 *  A5 + safety net) CustomButton bodies are the mod's minefield of unguarded lambdas: the PropHunt
 *      reveal button indexes into an empty candidate list (Buttons.cs:2272), Hacker/SecurityGuard/
 *      Yoyo effect-ends dereference possibly-null state (audit C table). Those lambdas cannot be
 *      patched individually (compiler-generated closures), but every one of them runs through
 *      exactly two managed doors: CustomButton.onClickEvent and CustomButton.Update. A Finalizer on
 *      each swallows the throw, logs it, and keeps every OTHER button updating that frame -
 *      previously one broken button aborted the whole button loop.
 *
 *  A7) `/gm` without an argument throws (ChatCommands.cs:39, Substring(4) on a 3-char string).
 *  B4) `/gm <mode>` desyncs host and clients: the RPC writes the OLD TORMapOptions.gameMode byte
 *      (ChatCommands.cs:52), then the host applies the new one locally (twice). Clients keep the
 *      old gamemode. Both live in TOR's own SendChatPatch.Prefix, which we replace wholesale (a
 *      prefix on TOR's prefix, `return false` + __result - the TiebreakerMultiple/TorPerfFixes
 *      technique; we are the only patch on that exact method). The rebuild is verbatim TOR 4.8.0
 *      except: length guard before Substring, local shareGamemode(new) exactly once, RPC carries
 *      the NEW mode byte. The /role branch goes through RoleInfo.GetRoleDescription, which
 *      TorNullGuards already made freeze-proof.
 *
 *  A8) resetNightVision throws when nightVisionOverlays is already null (UsablesPatch.cs:678) -
 *      and it IS nulled by resetVariables and by resetNightVision itself, while the Destroy hooks
 *      call it unconditionally. Prefix: swap a null list for an empty one, then let TOR run.
 *
 *  A9, client half) Role draft softlock when the currently picking player disconnects. The
 *      auto-pick only runs on the picker's own client (RoleDraft.cs:84), so their disconnect
 *      strands everyone: the dead id never leaves pickOrder, the outer loop never ends.
 *      HostFixPlugin already unsticks the HOST's copy; every client running THIS mod unsticks its
 *      own the same way (drop ids that no longer resolve - any position, not just the head: stale
 *      mid-list ids also NRE the picker's role filter via Helpers.playerById(x).Data at
 *      RoleDraft.cs:145/177, which kills the local draft coroutine for good). A stuck isRunning
 *      with an empty pickOrder is cleared after a grace period, mirroring HostFix's Fix 3.
 *
 * A10) Swapper: a guess/disconnect callback into swapperCheckAndReturnSwap dereferences the
 *      selections/renderers/button arrays, which only exist if the Swapper was alive when the
 *      meeting UI was built (MeetingPatch.cs:315-350) - null on the first meeting, stale-length
 *      afterwards. Prefix: when the arrays are unusable, perform the method's one non-UI duty
 *      (clearing the pending swap ids) and skip the rest.
 *
 * A12) A disconnect mid-meeting leaves a PlayerVoteArea whose id is no longer in
 *      GameData.AllPlayers; resetNameTagsAndColors' dict indexer then throws every frame
 *      (UpdatePatch.cs:56). Rebuilt with TryGetValue (and an indexer write instead of Add, so a
 *      duplicated PlayerId cannot throw either). Faithful to the original otherwise.
 *
 *  B1) Thief steals Witch: dangling else (RPC.cs:1127-1131). The `else` binds to the INNER if, so
 *      outside meetings nothing happens (the new Witch stays spelled) and inside meetings with
 *      witchVoteSavesTargets OFF the list is wrongly pruned. Snapshot in a Prefix, re-apply the
 *      intended rule in a Postfix. Host-authoritative where it matters (see above).
 *
 *  B2) Impostors never see their team in the intro once Spy spawn chance > 0: the "Role draft"
 *      block (IntroPatch.cs:172) is missing its RoleDraft.isEnabled condition and overwrites the
 *      spy-including team built directly above it - in every game, for everyone. Rebuilt with the
 *      draft condition the comment promises.
 *
 *  B3) EventUtility.Update's guard is commented out (EventUtility.cs:37), so its full target scan
 *      plus a yellow Mini outline runs every frame in every game, all year. A Prefix restores
 *      exactly the commented-out guard.
 *
 *  B8) CustomOption.updateSelection ends with `currentTabs.FirstOrDefault(x => x.active)
 *      .GetComponent(...)` (CustomOptions.cs:226) - null when no options tab is open, reachable on
 *      the host via presetSelection.updateSelection from GameStartManagerBeginGame, where the
 *      throw can abort the game start. The block is the method's last statement, so a Finalizer
 *      that swallows loses only the (impossible) menu refresh.
 *
 *  B9/B10) Witch and Arsonist charge-up buttons: the effect can end through the DeputyTimer path
 *      (CustomButton.cs:212-216), which runs BEFORE CouldUse's target-changed cancel in the same
 *      Update. A target that changed since the previous frame then slips into OnEffectEnds: the
 *      Witch writes Witch.currentTarget (NRE if null, wrong player spelled if not - the RPC at
 *      Buttons.cs:1631 sends it over the wire), the Arsonist the same shape at Buttons.cs:1439.
 *      A Prefix on CustomButton.Update replays TOR's own cancel for exactly these two buttons
 *      before any effect-end can fire, closing the one-frame window.
 *
 *  B11) PropHunt disguise preview shrinks exponentially: CouldUse multiplies localScale by
 *      1/bounds.magnitude every frame (Buttons.cs:2232) instead of setting it. A late postfix
 *      pins the preview to the absolute scale each frame, computed from the scale-independent
 *      sprite bounds.
 *
 *  E1) TOR's DebugManager postfix computes a SHA-256 of the debug-password config EVERY FRAME
 *      (Main.cs:175-182) just to conclude debug mode is off. Cached per config value; non-debug
 *      sessions skip the whole method.
 *
 * Deliberately NOT fixed here, with reasons:
 *  - B6 (Medium case 3) and B7 (double hackerUpdate guard) are dead/harmless code, nothing to fix
 *    from outside. D1/D2 (int-vs-byte RPC reads) "work" today and both sides of the wire would
 *    need the same change - an external fix on modded clients only would CREATE the desync it
 *    fears. D3 is TOR's design. The remaining audit C entries not already covered by TorNullGuards
 *    or the CustomButton net here sit mid-method in code we would have to rebuild wholesale
 *    (the "too risky from outside" rule from TorNullGuards items 10/12).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Modules;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using UnityEngine;

namespace UsefulTORStuff {
    public static class TorAuditFixes {
        // Shared throttle so a per-frame fault cannot flood the log: each distinct source logs its
        // first few hits, then every 100th.
        private static readonly Dictionary<string, int> logCounts = new();
        private static void ThrottledLog(string source, string message) {
            int n = logCounts.TryGetValue(source, out int c) ? c + 1 : 1;
            logCounts[source] = n;
            if (n <= 3 || n % 100 == 0)
                UsefulTORStuffPlugin.Logger?.LogWarning($"[TorAuditFixes] {message} (hit #{n})");
        }

        // ── A1) BountyHunter death must stop cleanly, not throw every FixedUpdate ──────────────
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "bountyHunterUpdate")]
        static class BountyHunterDeadBranchPatch {
            // TORMapOptions is internal to TOR - its icon dictionary comes via reflection, resolved
            // once. A failed resolve only costs the icon hiding, never the crash fix.
            private static readonly FieldInfo playerIconsField =
                AccessTools.Field(AccessTools.TypeByName("TheOtherRoles.TORMapOptions"), "playerIcons");

            public static bool Prefix() {
                try {
                    if (BountyHunter.bountyHunter == null || PlayerControl.LocalPlayer != BountyHunter.bountyHunter)
                        return true; // original returns immediately, nothing to guard
                    if (BountyHunter.bountyHunter.Data == null || !BountyHunter.bountyHunter.Data.IsDead)
                        return true; // alive path is TOR's (its empty-pool throw stays contained by TorNullGuards' Finalizer)

                    // TOR's IsDead branch, with the `||` corrected to `&&` (PlayerControlPatch.cs:654).
                    if (BountyHunter.arrow != null && BountyHunter.arrow.arrow != null)
                        UnityEngine.Object.Destroy(BountyHunter.arrow.arrow);
                    BountyHunter.arrow = null;
                    if (BountyHunter.cooldownText != null && BountyHunter.cooldownText.gameObject != null)
                        UnityEngine.Object.Destroy(BountyHunter.cooldownText.gameObject);
                    BountyHunter.cooldownText = null;
                    BountyHunter.bounty = null;
                    if (playerIconsField?.GetValue(null) is System.Collections.IDictionary icons)
                        foreach (object value in icons.Values)
                            if (value is PoolablePlayer p && p != null && p.gameObject != null)
                                p.gameObject.SetActive(false);
                    return false;
                } catch (Exception e) {
                    ThrottledLog("A1", $"BountyHunter dead-branch guard failed: {e.GetType().Name}: {e.Message}");
                    return false; // never fall through to the branch that throws
                }
            }
        }

        // ── A3) a meeting during an armed bomb must clear it, not crash on it ──────────────────
        [HarmonyPatch(typeof(Bomb), nameof(Bomb.update))]
        static class BombUpdatePatch {
            // Full rebuild of Bomb.cs:108-121 - the original dereferences Bomber.bomb.bomb right
            // after clearBomb() nulled Bomber.bomb. Same statements, plus the missing return.
            public static bool Prefix() {
                try {
                    if (Bomber.bomb == null || !Bomber.isActive) {
                        Bomb.canDefuse = false;
                        return false;
                    }
                    if (Bomber.bomb.background != null)
                        Bomber.bomb.background.transform.Rotate(Vector3.forward * 50 * Time.fixedDeltaTime);

                    if (MeetingHud.Instance) {
                        Bomber.clearBomb(); // nulls Bomber.bomb - the original kept dereferencing it
                        Bomb.canDefuse = false;
                        return false;
                    }

                    if (Bomber.bomb.bomb == null || PlayerControl.LocalPlayer == null) {
                        Bomb.canDefuse = false;
                        return false;
                    }
                    Bomb.canDefuse = Vector2.Distance(PlayerControl.LocalPlayer.GetTruePosition(),
                        Bomber.bomb.bomb.transform.position) <= 1f;
                    return false;
                } catch (Exception e) {
                    ThrottledLog("A3", $"Bomb.update rebuild failed: {e.GetType().Name}: {e.Message}");
                    return false; // the original would crash on exactly this path
                }
            }
        }

        // ── A4) a dying PropHunt hunter has no SpriteRenderer to juggle ────────────────────────
        [HarmonyPatch]
        static class PropHuntDeathGuardPatch {
            // PropHunt is internal to TOR - resolved by name, like TorNullGuards items 8/12.
            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                Type type = AccessTools.TypeByName("TheOtherRoles.CustomGameModes.PropHunt");
                return type == null ? null : AccessTools.Method(type, "MakePropImpostorPatch");
            }

            // The unguarded GetComponent<SpriteRenderer>() reads sit at the tail of the method
            // (dead-body sprite cosmetics, PropHunt.cs:510/517); everything that matters (revive,
            // role set, button timers) has already run by then. Swallowing loses nothing.
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null)
                    ThrottledLog("A4", $"PropHunt death cleanup threw (hunter without SpriteRenderer) - swallowed: {__exception.Message}");
                return null;
            }
        }

        // ── A5 + C net, B9/B10) every custom-button lambda runs through these two doors ────────
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.Update))]
        static class CustomButtonUpdateGuardPatch {
            // HudManagerStartPatch is internal to TOR; its button fields are public static. The
            // fields are re-read per use because HudManager.Start recreates every button object.
            private static readonly Type hudButtonsType = AccessTools.TypeByName("TheOtherRoles.HudManagerStartPatch");
            private static readonly FieldInfo witchButtonField = AccessTools.Field(hudButtonsType, "witchSpellButton");
            private static readonly FieldInfo arsonistButtonField = AccessTools.Field(hudButtonsType, "arsonistButton");

            // B9/B10: TOR's own "target changed - cancel the cast" check lives in CouldUse, but the
            // DeputyTimer effect-end (CustomButton.cs:212) fires BEFORE CouldUse in the same Update.
            // Replaying the cancel first means OnEffectEnds can only ever run with the target the
            // cast started on - never null, never a different player walked into range.
            public static bool Prefix(CustomButton __instance) {
                try {
                    if (!__instance.HasEffect || !__instance.isEffectActive) return true;

                    if (ReferenceEquals(__instance, witchButtonField?.GetValue(null))
                        && Witch.spellCastingTarget != Witch.currentTarget) {
                        // Verbatim TOR cancel, Buttons.cs:1608-1611.
                        Witch.spellCastingTarget = null;
                        __instance.Timer = 0f;
                        __instance.isEffectActive = false;
                    } else if (ReferenceEquals(__instance, arsonistButtonField?.GetValue(null))
                        && Arsonist.douseTarget != Arsonist.currentTarget) {
                        // Verbatim TOR cancel, Buttons.cs:1405-1409.
                        Arsonist.douseTarget = null;
                        __instance.Timer = 0f;
                        __instance.isEffectActive = false;
                    }
                } catch (Exception e) {
                    ThrottledLog("B9", $"button cancel guard failed: {e.GetType().Name}: {e.Message}");
                }
                return true;
            }

            // A5/C net: a throw inside one button's Update (effect-end lambdas included) previously
            // aborted the shared per-frame button loop, freezing every button after it in the list.
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null)
                    ThrottledLog("A5.update", $"a CustomButton.Update threw - swallowed so the other buttons keep running: {__exception.Message}");
                return null;
            }
        }

        // A5: the click lambdas (PropHunt reveal indexing an empty candidate list, and friends).
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.onClickEvent))]
        static class CustomButtonClickGuardPatch {
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null)
                    ThrottledLog("A5.click", $"a CustomButton click threw - swallowed: {__exception.Message}");
                return null;
            }
        }

        // ── A7 + B4) the chat command handler, rebuilt with its two bugs fixed ─────────────────
        [HarmonyPatch]
        static class ChatCommandsFixPatch {
            // TOR's SendChatPatch is a private nested class; its Prefix is the only patch on
            // ChatController.SendChat that TOR itself owns. Replacing IT (not SendChat) leaves
            // every other mod's own SendChat prefixes untouched.
            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                Type type = typeof(ChatCommands).GetNestedType("SendChatPatch", BindingFlags.NonPublic);
                return type == null ? null : AccessTools.Method(type, "Prefix");
            }

            // TOR's CustomRPC enum is internal - the ShareGamemode id is read off it once by name,
            // never hardcoded, so a renumbered enum in a future TOR breaks this loudly (fallback to
            // TOR's own handler) instead of silently sending a wrong rpc id.
            private static readonly byte? shareGamemodeRpcId = ResolveShareGamemodeId();
            private static byte? ResolveShareGamemodeId() {
                try {
                    Type rpcEnum = AccessTools.TypeByName("TheOtherRoles.CustomRPC");
                    if (rpcEnum == null) return null;
                    return Convert.ToByte(Enum.Parse(rpcEnum, "ShareGamemode"));
                } catch { return null; }
            }

            // Verbatim TOR 4.8.0 (ChatCommands.cs:14-103) except for the three marked lines.
            [HarmonyPrefix]
            public static bool Prefix([HarmonyArgument(0)] ChatController chat, ref bool __result) {
                try {
                    if (chat == null || chat.freeChatField == null || AmongUsClient.Instance == null
                        || PlayerControl.LocalPlayer == null)
                        return true; // let TOR handle the states we do not understand

                    string text = chat.freeChatField.Text ?? "";
                    string lower = text.ToLower();
                    bool handled = false;

                    if (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Started) {
                        if (lower.StartsWith("/kick ")) {
                            string playerName = text.Substring(6);
                            PlayerControl target = PlayerControl.AllPlayerControls.ToArray().FirstOrDefault(x => x.Data.PlayerName.Equals(playerName));
                            if (target != null && AmongUsClient.Instance.CanBan()) {
                                var client = AmongUsClient.Instance.GetClient(target.OwnerId);
                                if (client != null) {
                                    AmongUsClient.Instance.KickPlayer(client.Id, false);
                                    handled = true;
                                }
                            }
                        } else if (lower.StartsWith("/ban ")) {
                            string playerName = text.Substring(5);
                            PlayerControl target = PlayerControl.AllPlayerControls.ToArray().FirstOrDefault(x => x.Data.PlayerName.Equals(playerName));
                            if (target != null && AmongUsClient.Instance.CanBan()) {
                                var client = AmongUsClient.Instance.GetClient(target.OwnerId);
                                if (client != null) {
                                    AmongUsClient.Instance.KickPlayer(client.Id, true);
                                    handled = true;
                                }
                            }
                        } else if (lower.StartsWith("/gm")) {
                            // A7: "/gm" alone is 3 characters - TOR's unconditional Substring(4) threw.
                            string gm = text.Length >= 4 ? text.Substring(4).ToLower() : "";
                            CustomGamemodes gameMode = CustomGamemodes.Classic;
                            if (gm.StartsWith("prop") || gm.StartsWith("ph")) gameMode = CustomGamemodes.PropHunt;
                            else if (gm.StartsWith("guess") || gm.StartsWith("gm")) gameMode = CustomGamemodes.Guesser;
                            else if (gm.StartsWith("hide") || gm.StartsWith("hn")) gameMode = CustomGamemodes.HideNSeek;

                            if (shareGamemodeRpcId == null) return true; // enum changed - let TOR handle everything
                            if (AmongUsClient.Instance.AmHost) {
                                // B4: apply locally FIRST and put the NEW mode on the wire. TOR wrote
                                // the stale TORMapOptions.gameMode byte into the RPC and then called
                                // shareGamemode twice locally.
                                RPCProcedure.shareGamemode((byte)gameMode);
                                Hazel.MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                                    PlayerControl.LocalPlayer.NetId, shareGamemodeRpcId.Value, Hazel.SendOption.Reliable, -1);
                                writer.Write((byte)gameMode);
                                AmongUsClient.Instance.FinishRpcImmediately(writer);
                            } else {
                                chat.AddChat(PlayerControl.LocalPlayer, "Nice try, but you have to be the host to use this feature");
                            }
                            handled = true;
                        }
                    }

                    if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay) {
                        if (lower.Equals("/murder")) {
                            PlayerControl.LocalPlayer.Exiled();
                            FastDestroyableSingleton<HudManager>.Instance.KillOverlay.ShowKillAnimation(
                                PlayerControl.LocalPlayer.Data, PlayerControl.LocalPlayer.Data);
                            handled = true;
                        } else if (lower.StartsWith("/color ")) {
                            handled = true;
                            int col;
                            if (!Int32.TryParse(text.Substring(7), out col)) {
                                // AUDIT: TryParse failing must not fall through to Clamp/SetColor - that
                                // silently "succeeded" with color 0 (TryParse's out value on failure)
                                // and still printed the success message even after the usage error.
                                chat.AddChat(PlayerControl.LocalPlayer, "Unable to parse color id\nUsage: /color {id}");
                            } else {
                                col = Math.Clamp(col, 0, Palette.PlayerColors.Length - 1);
                                PlayerControl.LocalPlayer.SetColor(col);
                                chat.AddChat(PlayerControl.LocalPlayer, "Changed color succesfully");
                            }
                        }
                    }

                    if (lower.StartsWith("/tp ") && PlayerControl.LocalPlayer.Data != null && PlayerControl.LocalPlayer.Data.IsDead) {
                        string playerName = text.Substring(4).ToLower();
                        PlayerControl target = PlayerControl.AllPlayerControls.ToArray().FirstOrDefault(x => x.Data.PlayerName.ToLower().Equals(playerName));
                        if (target != null) {
                            PlayerControl.LocalPlayer.transform.position = target.transform.position;
                            handled = true;
                        }
                    }

                    if (lower.StartsWith("/role")) {
                        RoleInfo localRole = RoleInfo.getRoleInfoForPlayer(PlayerControl.LocalPlayer, false).FirstOrDefault();
                        if (localRole != RoleInfo.impostor && localRole != RoleInfo.crewmate) {
                            // Freeze-proofed by TorNullGuards' GetRoleDescription prefix.
                            string info = RoleInfo.GetRoleDescription(localRole);
                            chat.AddChat(PlayerControl.LocalPlayer, info);
                            handled = true;
                        }
                    }

                    if (handled) {
                        chat.freeChatField.Clear();
                        chat.quickChatMenu.Clear();
                    }
                    __result = !handled;
                    return false; // TOR's own prefix body is fully replaced
                } catch (Exception e) {
                    ThrottledLog("A7", $"chat command rebuild failed, falling back to TOR's handler: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── A8) resetNightVision must tolerate an already-null overlay list ────────────────────
        [HarmonyPatch]
        static class ResetNightVisionGuardPatch {
            private static readonly Type surveillanceType = AccessTools.TypeByName("TheOtherRoles.Patches.SurveillanceMinigamePatch");
            private static readonly FieldInfo overlaysField = AccessTools.Field(surveillanceType, "nightVisionOverlays");

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                return surveillanceType == null ? null : AccessTools.Method(surveillanceType, "resetNightVision");
            }

            // The original iterates the list before its own null-out; resetVariables and a previous
            // resetNightVision both leave it null, and the minigame Destroy hooks call in regardless.
            // An empty list makes the iteration a no-op and leaves TOR's own logic fully intact.
            [HarmonyPrefix]
            public static void Prefix() {
                try {
                    if (overlaysField != null && overlaysField.GetValue(null) == null)
                        overlaysField.SetValue(null, new List<GameObject>());
                } catch (Exception e) {
                    ThrottledLog("A8", $"night-vision list guard failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── A9) the client half of the draft softlock: purge unresolvable pickers ──────────────
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class DraftWatchdogPatch {
            // RoleDraft is internal to TOR - same reflection targets MultiJester and HostFixPlugin
            // already use. Resolved once; a miss disables only this watchdog.
            private static readonly Type draftType = AccessTools.TypeByName("TheOtherRoles.Modules.RoleDraft");
            private static readonly FieldInfo isRunningField = AccessTools.Field(draftType, "isRunning");
            private static readonly FieldInfo pickOrderField = AccessTools.Field(draftType, "pickOrder");

            private static float stuckTimer;

            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (isRunningField == null || pickOrderField == null) return;
                    if (AmongUsClient.Instance == null
                        || AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Started) {
                        stuckTimer = 0f;
                        return;
                    }
                    if (!(bool)isRunningField.GetValue(null)) { stuckTimer = 0f; return; }
                    if (pickOrderField.GetValue(null) is not List<byte> pickOrder) return;

                    if (pickOrder.Count > 0) {
                        stuckTimer = 0f;
                        // Every position, not just the head: the picking client's role filter runs
                        // Helpers.playerById(x).Data over the WHOLE list every frame, so one stale
                        // mid-list id kills that player's draft coroutine outright.
                        for (int i = pickOrder.Count - 1; i >= 0; i--) {
                            PlayerControl p = Helpers.playerById(pickOrder[i]);
                            if (p != null && p.Data != null && !p.Data.Disconnected) continue;
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                $"[TorAuditFixes] draft: removed disconnected player id {pickOrder[i]} from pickOrder " +
                                "(position " + i + ") - without this every client without the fix soft-locks.");
                            pickOrder.RemoveAt(i);
                        }
                    } else {
                        // Same shape as HostFixPlugin's Fix 3, but on EVERY client: isRunning stuck
                        // with nothing left to pick freezes all CustomButton cooldowns
                        // (CustomButton.cs:246 gates on !RoleDraft.isRunning). Slightly longer grace
                        // than HostFix's 15s so on the host the host-side fix stays the one that acts.
                        stuckTimer += Time.deltaTime;
                        if (stuckTimer > 20f) {
                            stuckTimer = 0f;
                            isRunningField.SetValue(null, false);
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                "[TorAuditFixes] draft: isRunning was stuck with an empty pickOrder - reset to false.");
                        }
                    }
                } catch (Exception e) {
                    ThrottledLog("A9", $"draft watchdog failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── A10) swapperCheckAndReturnSwap must survive missing/stale meeting arrays ───────────
        [HarmonyPatch]
        static class SwapperArraysGuardPatch {
            private static readonly Type meetingHudPatchType = AccessTools.TypeByName("TheOtherRoles.Patches.MeetingHudPatch");
            private static readonly FieldInfo selectionsField = AccessTools.Field(meetingHudPatchType, "selections");
            private static readonly FieldInfo renderersField = AccessTools.Field(meetingHudPatchType, "renderers");
            private static readonly FieldInfo buttonListField = AccessTools.Field(meetingHudPatchType, "swapperButtonList");

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                return meetingHudPatchType == null ? null : AccessTools.Method(meetingHudPatchType, "swapperCheckAndReturnSwap");
            }

            [HarmonyPrefix]
            public static bool Prefix([HarmonyArgument(0)] MeetingHud __instance, [HarmonyArgument(1)] byte dyingPlayerId) {
                try {
                    if (__instance == null || Swapper.swapper == null || __instance.state == MeetingHud.VoteStates.Results)
                        return true; // original returns before touching the arrays
                    if (PlayerControl.LocalPlayer != Swapper.swapper)
                        return true; // original's non-swapper path never indexes the arrays either

                    // The arrays only exist if the meeting UI was built for a LIVING Swapper
                    // (addSwapperButtons); a dead-at-meeting-start Swapper has null or last
                    // meeting's lengths here. Either way the original's loops throw.
                    int count = __instance.playerStates != null ? __instance.playerStates.Count : 0;
                    bool usable = selectionsField?.GetValue(null) is bool[] sel && sel.Length == count
                        && renderersField?.GetValue(null) is SpriteRenderer[] ren && ren.Length >= count
                        && buttonListField?.GetValue(null) is PassiveButton[] btn && btn.Length >= count;
                    if (usable) return true;

                    // Do the method's one non-UI duty ourselves (MeetingPatch.cs:321-323), skip the rest.
                    if (dyingPlayerId == Swapper.playerId1 || dyingPlayerId == Swapper.playerId2)
                        Swapper.playerId1 = Swapper.playerId2 = byte.MaxValue;
                    ThrottledLog("A10", "swapper meeting arrays missing or stale - handled the swap-id reset without them.");
                    return false;
                } catch (Exception e) {
                    ThrottledLog("A10", $"swapper array guard failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── A12) meeting name tags must survive a mid-meeting disconnect ───────────────────────
        [HarmonyPatch]
        static class NameTagsRebuildPatch {
            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                Type type = AccessTools.TypeByName("TheOtherRoles.Patches.HudManagerUpdatePatch");
                return type == null ? null : AccessTools.Method(type, "resetNameTagsAndColors");
            }

            // Rebuilt per frame anyway, so a plain local buffer with no round reset (single-frame
            // data, the TorPerfFixes item-2 precedent).
            private static readonly Dictionary<byte, (string name, Color color)> tagDict = new();

            // Faithful rebuild of UpdatePatch.cs:18-62 with two hardening changes: the dictionary
            // write uses the indexer (a duplicated PlayerId cannot throw) and the meeting loop uses
            // TryGetValue (a PlayerVoteArea whose player left GameData no longer throws every frame).
            [HarmonyPrefix]
            public static bool Prefix() {
                try {
                    var localPlayer = PlayerControl.LocalPlayer;
                    if (localPlayer == null || localPlayer.Data == null || GameData.Instance == null)
                        return false; // original would NRE on the same state
                    bool amImpostor = localPlayer.Data.Role != null && localPlayer.Data.Role.IsImpostor;
                    bool morphActive = Morphling.morphTimer > 0f && Morphling.morphTarget != null;

                    tagDict.Clear();
                    foreach (var data in GameData.Instance.AllPlayers) {
                        if (data == null) continue;
                        var player = data.Object;
                        string text = data.PlayerName;
                        Color color = Color.white;
                        if (player) {
                            string playerName = text;
                            if (morphActive && Morphling.morphling == player && Morphling.morphTarget.Data != null)
                                playerName = Morphling.morphTarget.Data.PlayerName;
                            var nameText = player.cosmetics != null ? player.cosmetics.nameText : null;
                            if (nameText != null) {
                                nameText.text = Helpers.hidePlayerName(localPlayer, player) ? "" : playerName;
                                color = amImpostor && data.Role != null && data.Role.IsImpostor ? Palette.ImpostorRed : Color.white;
                                nameText.color = new Color(color.r, color.g, color.b, Chameleon.visibility(data.PlayerId));
                            }
                        }
                        tagDict[data.PlayerId] = (text, color);
                    }

                    if (MeetingHud.Instance != null) {
                        foreach (PlayerVoteArea pva in MeetingHud.Instance.playerStates) {
                            if (pva == null || pva.NameText == null) continue;
                            if (!tagDict.TryGetValue(pva.TargetPlayerId, out var entry)) continue; // the disconnected player
                            pva.NameText.text = entry.name;
                            pva.NameText.color = entry.color;
                        }
                    }
                    return false;
                } catch (Exception e) {
                    ThrottledLog("A12", $"name-tag rebuild failed, falling back to TOR's: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── B1) Thief steals Witch: apply the rule the indentation promised ────────────────────
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.thiefStealsRole))]
        static class ThiefStealsWitchPatch {
            // C# bound the else at RPC.cs:1130 to the INNER if, so: outside meetings nothing runs
            // (intended: unspell the new Witch), and in meetings with witchVoteSavesTargets OFF the
            // RemoveAll runs (intended: nothing - "no target is saved" means the list stays).
            // The original is left running untouched; the Postfix re-applies the intended outcome
            // from a Prefix snapshot. Kills from this list are executed host-side only
            // (ExileControllerPatch.cs:57 gates on AmHost), so the fix is authoritative on the host
            // and display-only elsewhere.
            public static void Prefix(byte playerId, out (bool wasWitch, bool inMeeting, byte thiefId, List<PlayerControl> snapshot) __state) {
                __state = (false, false, byte.MaxValue, null);
                try {
                    PlayerControl target = Helpers.playerById(playerId);
                    if (target == null || Witch.witch == null || target != Witch.witch || Thief.thief == null) return;
                    __state = (true, MeetingHud.Instance != null, Thief.thief.PlayerId,
                        Witch.futureSpelled == null ? new List<PlayerControl>() : new List<PlayerControl>(Witch.futureSpelled));
                } catch (Exception e) {
                    ThrottledLog("B1", $"thief/witch snapshot failed: {e.GetType().Name}: {e.Message}");
                }
            }

            public static void Postfix((bool wasWitch, bool inMeeting, byte thiefId, List<PlayerControl> snapshot) __state) {
                try {
                    if (!__state.wasWitch || __state.snapshot == null) return;
                    // The new Witch is never left spelled by their own predecessor - in a meeting just
                    // as much as outside one (AUDIT M-15). "No target is saved" means the OTHER
                    // targets stay condemned; it cannot sensibly mean the freshly promoted Witch
                    // exiles themselves in the very meeting they stole the role. TOR's own
                    // out-of-meeting branch spells out that intent ("remove the thief from the list
                    // of spelled people, keep the rest"), so both branches now apply it.
                    __state.snapshot.RemoveAll(x => x != null && x.PlayerId == __state.thiefId);
                    if (__state.inMeeting) {
                        Witch.futureSpelled = Witch.witchVoteSavesTargets
                            ? new List<PlayerControl>()   // all targets saved (original got this right)
                            : __state.snapshot;           // "no target is saved": restore what the stray RemoveAll pruned
                    } else {
                        Witch.futureSpelled = __state.snapshot;
                    }
                } catch (Exception e) {
                    ThrottledLog("B1", $"thief/witch correction failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── B2) impostors must see their team in the intro unless the draft hides it ───────────
        [HarmonyPatch]
        static class IntroTeamIconsFixPatch {
            private static readonly PropertyInfo draftEnabledProperty =
                AccessTools.Property(AccessTools.TypeByName("TheOtherRoles.Modules.RoleDraft"), "isEnabled");

            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                Type type = AccessTools.TypeByName("TheOtherRoles.Patches.IntroPatch");
                return type == null ? null : AccessTools.Method(type, "setupIntroTeamIcons");
            }

            // Faithful rebuild of IntroPatch.cs:151-177 with the one missing condition added: the
            // hide-the-team block is commented "Role draft" but never checked the draft, so any
            // lobby with Spy chance > 0 and 2+ impostors hid the intro team from EVERYONE - it also
            // overwrote the spy-including fake team built directly above it.
            [HarmonyPrefix]
            public static bool Prefix([HarmonyArgument(1)] ref Il2CppSystem.Collections.Generic.List<PlayerControl> yourTeam) {
                try {
                    var local = PlayerControl.LocalPlayer;
                    if (local == null || local.Data == null) return true;

                    if (Helpers.isNeutral(local)) {
                        var soloTeam = new Il2CppSystem.Collections.Generic.List<PlayerControl>();
                        soloTeam.Add(local);
                        yourTeam = soloTeam;
                    }

                    bool amImpostor = local.Data.Role != null && local.Data.Role.IsImpostor;
                    if (Spy.spy != null && amImpostor) {
                        List<PlayerControl> players = PlayerControl.AllPlayerControls.ToArray().ToList().OrderBy(x => Guid.NewGuid()).ToList();
                        var fakeImpostorTeam = new Il2CppSystem.Collections.Generic.List<PlayerControl>();
                        fakeImpostorTeam.Add(local); // local player first = displayed in the center
                        foreach (PlayerControl p in players) {
                            if (local != p && (p == Spy.spy || (p.Data != null && p.Data.Role != null && p.Data.Role.IsImpostor)))
                                fakeImpostorTeam.Add(p);
                        }
                        yourTeam = fakeImpostorTeam;
                    }

                    bool draftEnabled = draftEnabledProperty != null && (bool)draftEnabledProperty.GetValue(null);
                    if (draftEnabled // B2: the condition TOR's own comment promises
                        && CustomOptionHolder.spySpawnRate.getSelection() > 0
                        && PlayerControl.AllPlayerControls.ToArray().Count(x => x.Data != null && x.Data.Role != null && x.Data.Role.IsImpostor) > 1) {
                        var hiddenTeam = new Il2CppSystem.Collections.Generic.List<PlayerControl>();
                        hiddenTeam.Add(local);
                        yourTeam = hiddenTeam;
                    }
                    return false;
                } catch (Exception e) {
                    ThrottledLog("B2", $"intro team rebuild failed, falling back to TOR's: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }

        // ── B3) event-mode targeting must not run all year ─────────────────────────────────────
        [HarmonyPatch(typeof(EventUtility), nameof(EventUtility.Update))]
        static class EventUtilityGuardPatch {
            // Restores exactly the guard TOR left commented out (EventUtility.cs:37). Without it,
            // every frame of every game scans all players and hands the Mini a yellow outline on
            // whoever is near - an April-fools leftover leaking into normal play, at per-frame cost.
            public static bool Prefix() {
                try {
                    return EventUtility.isEnabled
                        && AmongUsClient.Instance != null
                        && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started
                        && !IntroCutscene.Instance;
                } catch (Exception e) {
                    ThrottledLog("B3", $"event guard failed: {e.GetType().Name}: {e.Message}");
                    return false; // on doubt, skip the scan - it is decoration even on event days
                }
            }
        }

        // ── B8) a programmatic option change must not crash on a closed options menu ───────────
        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.updateSelection))]
        static class UpdateSelectionMenuGuardPatch {
            // The unguarded `currentTabs.FirstOrDefault(x => x.active).GetComponent(...)`
            // (CustomOptions.cs:226) is the method's LAST statement, so swallowing the throw loses
            // only the refresh of a menu that is not open - while letting it escape aborts callers
            // like GameStartManagerBeginGame's presetSelection.updateSelection (dynamic map).
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null)
                    ThrottledLog("B8", $"updateSelection threw after applying the value (options menu closed?) - swallowed: {__exception.Message}");
                return null;
            }
        }

        // ── B11) the PropHunt disguise preview must keep its size ──────────────────────────────
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class PropPreviewScalePatch {
            private static readonly Type hudButtonsType = AccessTools.TypeByName("TheOtherRoles.HudManagerStartPatch");
            private static readonly FieldInfo propHolderField = AccessTools.Field(hudButtonsType, "propSpriteHolder");
            private static readonly FieldInfo propRendererField = AccessTools.Field(hudButtonsType, "propSpriteRenderer");
            private static readonly FieldInfo isPropHuntField =
                AccessTools.Field(AccessTools.TypeByName("TheOtherRoles.CustomGameModes.PropHunt"), "isPropHuntGM");

            // TOR's CouldUse multiplies localScale by 1/bounds.magnitude EVERY frame (Buttons.cs:
            // 2232) - bounds are world-space, so the factor compounds and the preview collapses
            // toward zero. Runs late so it lands after TOR's button update in the same frame, and
            // pins the intended absolute scale from the scale-INdependent sprite bounds instead.
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (isPropHuntField == null || !(bool)isPropHuntField.GetValue(null)) return;
                    if (propRendererField?.GetValue(null) is not SpriteRenderer renderer || renderer == null) return;
                    if (renderer.sprite == null) return;
                    if (propHolderField?.GetValue(null) is not GameObject holder || holder == null) return;
                    float magnitude = renderer.sprite.bounds.size.magnitude;
                    if (magnitude <= 0f) return;
                    holder.transform.localScale = Vector3.one * (1f / magnitude);
                } catch (Exception e) {
                    ThrottledLog("B11", $"prop preview scale pin failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── E1) no SHA-256 per frame just to learn debug mode is (still) off ───────────────────
        [HarmonyPatch(typeof(DebugManager), nameof(DebugManager.Postfix))]
        static class DebugHashCachePatch {
            // TOR's own gate constant (Main.cs:168), needed to evaluate the gate without running it.
            private const string PasswordHash = "d1f51dfdfd8d38027fd2ca9dfeb299399b5bdee58e6c0b3b5e9a45cd4e502848";
            private static string lastValue;
            private static bool lastMatched;

            public static bool Prefix() {
                try {
                    string value = TheOtherRolesPlugin.DebugMode?.Value ?? "";
                    if (!string.Equals(value, lastValue, StringComparison.Ordinal)) {
                        lastValue = value;
                        using (var sha = SHA256.Create()) {
                            byte[] hashed = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                            var builder = new StringBuilder(hashed.Length * 2);
                            foreach (byte b in hashed) builder.Append(b.ToString("x2"));
                            lastMatched = builder.ToString() == PasswordHash;
                        }
                    }
                    // Debug users keep TOR's full path (it re-derives the hash itself); everyone
                    // else skips the method - and with it the per-frame SHA + string allocations.
                    return lastMatched;
                } catch (Exception e) {
                    ThrottledLog("E1", $"debug hash cache failed: {e.GetType().Name}: {e.Message}");
                    return true;
                }
            }
        }
    }
}
