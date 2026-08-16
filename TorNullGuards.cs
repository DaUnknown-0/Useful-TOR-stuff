// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorNullGuards - crash guards for twelve unguarded dereferences in TOR itself (1-6: AUDIT-2026-08-15,
 * 7-12: AUDIT-2026-08-16).
 *
 * All twelve are option-less bugfixes in the same spirit as the Bloody throttle: they only ever turn
 * a crash or a freeze into normal behaviour, they can hand nobody an advantage, and they are
 * therefore deliberately NOT behind UTSGate. A client running this mod is protected even when the
 * host does not have it - which is the whole point, since several of the twelve take down that one
 * client.
 *
 * (7-9, 11) and (10, 12) share one root cause: Helpers.playerById(...) returns null once the
 * referenced player has left the lobby, and TOR dereferences the result unchecked. (10) and (12)
 * are guarded with a Finalizer instead of a Prefix - see their own entries for why a full rebuild
 * of those two methods was judged too risky to attempt from the outside.
 *
 *  1) RoleInfo.GetRoleDescription - HARD FREEZE (high)
 *     TOR spins on `while (ReadmePage == "") { }` with no yield, no timeout and no error handling.
 *     ReadmePage is filled by loadReadme(), started fire-and-forget in Main.cs without try/catch. If
 *     the GitHub fetch fails (offline, blocked, rate-limited) or has simply not finished yet, the
 *     string stays empty forever and the first player to type /role locks up the Unity main thread
 *     for good - the game has to be killed via the task manager. On top of that, even a loaded page
 *     is unsafe: IndexOf returns -1 for a role the README does not list, and Substring(-1) throws.
 *     We replace the method outright: no spin, both index checks guarded, and a throttled retry of
 *     the download so /role starts working once the network comes back.
 *
 *  2) RPCProcedure.uncheckedCmdReportDeadBody - NullReference (high)
 *     `Helpers.playerById(targetId).Data` is dereferenced without a null check, unlike every
 *     neighbouring handler in that file. The Bait modifier reports its own body after a random delay
 *     (Bait.reportDelayMin..Max); if the victim leaves the lobby before the timer fires, its
 *     PlayerControl is gone and the RPC throws on every client that already processed the disconnect.
 *     We drop the report instead. Passing the null body through would NOT be equivalent: a null body
 *     means "emergency meeting" to ReportDeadBody, so the guard has to skip the call, not soften it.
 *
 *  3) RPCProcedure.stopStart - NullReference (low)
 *     `Helpers.playerById(playerId).Data.PlayerName` again without a null check, reachable with the
 *     non-default option "anyPlayerCanStopStart" via a forged id or a disconnect race. Only the host
 *     is hit, and only on the chat line - the actual effect (ResetStartState) has already happened by
 *     then. So we reproduce everything the original does and only replace the unresolvable name.
 *
 *  4) AmongUsClient.OnGameEnd (via EndGamePatch.cs) - NullReference (high)
 *     "Sidekick Gets Promoted To Jackal On Jackal Death" defaults to OFF (CustomOptionHolder.cs:560),
 *     so when the Jackal dies, RPC.cs's erasePlayerRoles() (778-784) only clears Jackal.jackal -
 *     Sidekick.sidekick survives untouched. If that lone Sidekick then reaches parity alone,
 *     PlayerStatistics.GetPlayerCounts (EndGamePatch.cs:606-624) counts Jackal and Sidekick in two
 *     separate if-blocks, so TeamJackalAlive is 1 and TeamJackalWin fires normally - and
 *     EndGamePatch.cs:192 unconditionally reads Jackal.jackal.Data to build the winner entry.
 *     Jackal.jackal is null, so TOR's own OnGameEnd postfix throws on every client. Our own Sidekick
 *     Can Kill Jackal makes the underlying scenario (Jackal dead, Sidekick alone alive) more common.
 *     We promote the Sidekick into the Jackal slot for the duration of TOR's postfix (and clear
 *     Sidekick.sidekick so the postfix's separate Sidekick branch cannot add the same winner twice);
 *     resetVariables() at the very end of that same postfix (EndGamePatch.cs:233) undoes both via
 *     clearAndReload() before the next round. Our prefix runs at Priority.Last so TOR's own OnGameEnd
 *     prefix - which stamps OnGameEndPatch.gameOverReason AND overwrites endGameResult.GameOverReason
 *     itself for reasons >= 10 (EndGamePatch.cs:69-71) - has always already run; we read the reason
 *     from that stamped field, never from the (by then rewritten) raw argument.
 *
 *  5) PlayerControlPatch.bountyHunterUpdate - IndexOutOfRange (medium)
 *     The BountyHunter's target filter (Impostor/Spy/team-red Sidekick or Jackal/an immature Mini/the
 *     BountyHunter's own Lover, all excluded) can legitimately empty the candidate pool, and TOR
 *     indexes straight into it - `possibleTargets[TheOtherRoles.rnd.Next(0, possibleTargets.Count)]`
 *     - with no `Count == 0` guard, unlike the structurally identical spot TOR itself guards in
 *     RoleAssignmentPatch.cs:405. ExileControllerPatch.cs resets the retry timer after every meeting,
 *     so an empty pool throws on every single tick and, because this runs mid-sequence in
 *     PlayerControlPatch.cs:1069, takes every role update after it (Vulture, Medium,
 *     Morphling/Camouflager, Lawyer, Pursuer) down with it for that frame. A finalizer on the method
 *     swallows the exception and repeats the cleanup TOR's own IsDead branch performs two lines above
 *     the crash site (PlayerControlPatch.cs:653-663).
 *
 *  6) Bomb.explode - MissingReference (medium)
 *     The nested Effects.Lerp coroutines started in Bomb's constructor (~Bomb.cs:62-77) capture the
 *     Bomb instance and keep running on the persistent HudManager. update() (Bomb.cs:115-117) calls
 *     Bomber.clearBomb() the instant a meeting starts, which Destroy()s the bomb's GameObjects
 *     without stopping that coroutine; our own Bomber Can Cancel Bomb triggers the same path on
 *     demand. When the coroutine's timer elapses regardless, explode(b) still dereferences
 *     b.bomb.transform.position (Bomb.cs:83) on the now-destroyed GameObject. We skip the original
 *     whenever b or b.bomb is gone (Unity's == null catches destroyed references too, not just
 *     unassigned ones) and only replay its own cleanup tail (clearBomb()/canDefuse/isActive) when
 *     Bomber.bomb still points at this exact stale instance - bombCooldown (default 15s) can be
 *     shorter than destructionTime (default 20s), so a fresh, legitimate bomb may already be planted
 *     and must not be torn down by a stale coroutine's leftover cleanup.
 *
 *  7) PlayerControlFixedUpdatePatch.bloodyUpdate - NullReference, repeats every tick (critical)
 *     Patches/PlayerControlPatch.cs:896-909. `PlayerControl player = Helpers.playerById(entry.Key)`
 *     is dereferenced two lines later (`Bloody.bloodyKillerMap[player.PlayerId]`) with no null check.
 *     Bloody.active is keyed by the killer's PlayerId; if that Bloody-marked killer leaves the lobby
 *     before Bloody.duration runs out, player is null. Worse, TOR only removes the dictionary entry
 *     AFTER this dereference (in the `if (entry.Value <= 0 || player.Data.IsDead)` branch further
 *     down), so a null entry is never cleaned up and throws again on every single FixedUpdate for
 *     the rest of the round. Bloody.active is populated via RPC (RPC.cs's bloody()), so it hits every
 *     client that received the broadcast, not just the host.
 *     A Finalizer cannot fix this: the stale entry would keep throwing every following tick even
 *     after being swallowed once. Instead we Prefix the method and remove every Bloody.active (and
 *     matching Bloody.bloodyKillerMap) entry whose key no longer resolves through Helpers.playerById
 *     BEFORE TOR's own body runs, then let the now-safe original execute normally. Collect the stale
 *     keys into a list first and remove them after the scan - Bloody.active is a Dictionary, and
 *     removing while enumerating it throws InvalidOperationException.
 *
 *  8) GameHistory.overrideDeathReasonAndKiller - NullReference (medium)
 *     GameHistory.cs:48. `deadPlayers.FirstOrDefault(x => x.player.PlayerId == player.PlayerId)`
 *     dereferences `player.PlayerId` immediately. The method DOES null-check `player` - but only six
 *     lines later, in the `else if (player != null)` branch that decides whether to add a fresh
 *     DeadPlayer - long after the crash already happened. Reached via RPC.cs:1298
 *     (receiveGhostInfo/ShareGhostInfo), which passes `Helpers.playerById(reader.ReadByte())` straight
 *     through unchecked; WitchExile, LawyerSuicide and LoverSuicide are the deaths most likely to
 *     reference a player who has since disconnected. GameHistory is internal to TOR's assembly (no
 *     InternalsVisibleTo), so unlike items 2/3/9/11 the target type cannot be named with typeof() and
 *     is resolved via AccessTools.TypeByName + HarmonyTargetMethod instead - the same technique TOR's
 *     own Players/CachedPlayer.cs uses to patch its own private nested iterator type.
 *     Fix: Prefix that skips the original (return false) when player == null.
 *
 *  9) EventUtility.handleKick - NullReference (medium)
 *     Utilities/EventUtility.cs:89-100. `source`/`target` come straight from RPC.cs:1680
 *     (`EventUtility.handleKick(Helpers.playerById(kickSource), Helpers.playerById(kickTarget), ...)`)
 *     with no null check on either side, and handleKick immediately reads `target.GetTruePosition()`
 *     and, further down, `.transform.position` on both. A kicker or a kicked player who disconnects
 *     between the kick decision and the RPC arriving is enough to throw on every receiving client.
 *     The method later swaps source and target with Mini.mini under the "boing flip" condition
 *     (heavy/over-the-limit kicks bounce off Mini instead) - that swap happens inside the original
 *     body, after our guard, so checking both incoming parameters up front covers every path into it.
 *     Fix: Prefix that skips the original (return false) when source == null or target == null.
 *
 *  10) RPCProcedure.guesserShoot - NullReference (medium)
 *      RPC.cs:1029-1034. Inside the loop over `MeetingHud.Instance.playerStates`, TOR resolves
 *      `var voteAreaPlayer = Helpers.playerById(pva.TargetPlayerId);` and reads `voteAreaPlayer.AmOwner`
 *      the very next line, with no null check - unlike every other Helpers.playerById() call in this
 *      same file. If the voter that the current PlayerVoteArea entry still refers to has already left
 *      (the UI entry outlives the disconnect), this throws and aborts the loop, so nobody after that
 *      entry gets their vote returned for the rest of that meeting.
 *      A Prefix cannot help here: the crash is buried inside a loop in the middle of a much larger
 *      method (killer/lover/lawyer death handling, kill sound, overlay, CheckForEndVoting), and
 *      reimplementing all of that from the outside to reach one dereference is exactly the kind of
 *      "too risky" rebuild the project brief warns against. We use a Finalizer instead: it does not
 *      recover the skipped vote returns for that one meeting, but it stops the exception from
 *      propagating out of guesserShoot entirely, so CheckForEndVoting and everything else downstream
 *      of the RPC handler still runs. This treats the symptom, not the cause - the cause is only
 *      reachable by rebuilding this method end to end.
 *
 *  11) Portal.startTeleport - NullReference (medium)
 *      Objects/Portal.cs:46-47. `PlayerControl playerControl = Helpers.playerById(playerId);` is
 *      dereferenced on the very next line (`playerControl.cosmetics.currentBodySprite...`) with no
 *      null check. A player who steps into a Portal and disconnects before the teleport RPC is
 *      processed on some other client leaves that client with an unresolvable id.
 *      Fix: Prefix that skips the original (return false) when Helpers.playerById(playerId) == null.
 *
 *  12) MeetingHudPatch.guesserOnClick - NullReference (medium)
 *      Patches/MeetingPatch.cs:455. While building the per-role button list for the Guesser UI, TOR
 *      re-resolves the meeting target for every role in RoleInfo.allRoleInfos and reads
 *      `.Data.IsDead` straight off it: `!Helpers.playerById((byte)__instance.playerStates[buttonTarget]
 *      .TargetPlayerId).Data.IsDead`. The very same method DOES guard this exact lookup six lines
 *      further down (line 461, `focusedTarget == null`) - just not here, at the point where the
 *      button's OnClick listener is being decided. If the targeted player has left between opening
 *      the meeting and the Guesser menu being built, this throws while iterating role buttons.
 *      Same reasoning as (10): this line sits in the middle of a long UI-construction loop that
 *      builds and wires up buttons via closures captured over local state (selectedButton, buttons,
 *      container, ...); a Prefix skipping the whole method would just leave the Guesser UI half-open
 *      with no way to close it, which is worse than the crash. A Finalizer swallows the exception so
 *      the meeting is not taken down with it - again the symptom, not the cause. MeetingHudPatch is
 *      internal to TOR's assembly, so - like (8) - the target is resolved via AccessTools.TypeByName +
 *      HarmonyTargetMethod rather than typeof().
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;

namespace UsefulTORStuff {
    public static class TorNullGuards {
        // TOR's ReadmePage is private static - the only member here we cannot reach directly.
        private static readonly FieldInfo readmePageField = AccessTools.Field(typeof(RoleInfo), "ReadmePage");

        // Retry throttle for the README download. TOR starts it exactly once at boot and never again,
        // so a single failed fetch would otherwise disable /role for the whole session.
        private static bool retryRunning;
        private static DateTime lastRetry = DateTime.MinValue;
        private static readonly TimeSpan retryCooldown = TimeSpan.FromSeconds(30);

        private static string ReadmePage {
            get { try { return readmePageField?.GetValue(null) as string ?? ""; } catch { return ""; } }
        }

        // Fire-and-forget like TOR's own call, but with the try/catch TOR is missing: loadReadme()
        // calls EnsureSuccessStatusCode(), so a failed fetch throws inside the task and would
        // otherwise surface as an unobserved task exception.
        private static void TryReloadReadme() {
            if (retryRunning || DateTime.UtcNow - lastRetry < retryCooldown) return;
            retryRunning = true;
            lastRetry = DateTime.UtcNow;
            _ = Task.Run(async () => {
                try { await RoleInfo.loadReadme(); } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorNullGuards] README reload failed: {e.Message}");
                } finally { retryRunning = false; }
            });
        }

        // ── 1) /role must never freeze the client ──────────────────────────────────────────────
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.GetRoleDescription))]
        static class RoleDescriptionPatch {
            public static bool Prefix(RoleInfo roleInfo, ref string __result) {
                try {
                    if (roleInfo == null) { __result = ""; return false; }

                    string page = ReadmePage;
                    if (string.IsNullOrEmpty(page)) {
                        // Not loaded (yet, or at all). TOR would spin here forever.
                        TryReloadReadme();
                        __result = $"{roleInfo.name}: description unavailable (the role list could not be "
                                 + "downloaded). Retrying in the background - try /role again in a moment.";
                        return false;
                    }

                    int index = page.IndexOf($"## {roleInfo.name}", StringComparison.Ordinal);
                    if (index < 0) {
                        // Role not documented in the README (a newer role, a renamed one, a mod role).
                        // TOR would call Substring(-1) here and throw.
                        __result = $"{roleInfo.name}: no description found in the role list.";
                        return false;
                    }

                    int endIndex = page.Substring(index).IndexOf("### Game Options", StringComparison.Ordinal);
                    // Last section of the page: no "### Game Options" behind it, take the remainder.
                    __result = endIndex < 0 ? page.Substring(index) : page.Substring(index, endIndex);
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] GetRoleDescription guard failed: {e}");
                    __result = "";
                    return false; // never fall through to TOR's spin loop
                }
            }
        }

        // ── 2) delayed Bait report must not throw when the victim already left ──────────────────
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.uncheckedCmdReportDeadBody))]
        static class UncheckedCmdReportDeadBodyPatch {
            public static bool Prefix(byte sourceId, byte targetId) {
                try {
                    // byte.MaxValue is TOR's own "emergency meeting, no body" marker - leave it alone.
                    if (targetId == byte.MaxValue) return true;

                    PlayerControl target = Helpers.playerById(targetId);
                    if (target != null && target.Data != null) return true; // original is safe

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[TorNullGuards] dropped report for missing player {targetId} (reporter {sourceId}) - " +
                        "the body's owner left before the delayed report fired.");
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] report guard failed: {e}");
                    return true;
                }
            }
        }

        // ── 3) stopStart must not throw on an unresolvable player id ───────────────────────────
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.stopStart))]
        static class StopStartPatch {
            public static bool Prefix(byte playerId) {
                try {
                    PlayerControl p = Helpers.playerById(playerId);
                    if (p != null && p.Data != null) return true; // original is safe

                    // Reproduce the original faithfully, minus the name it cannot resolve. Doing
                    // nothing instead would leave the start state stuck for everyone.
                    if (!CustomOptionHolder.anyPlayerCanStopStart.getBool()) return false;
                    if (SoundManager.Instance != null && GameStartManager.Instance != null)
                        SoundManager.Instance.StopSound(GameStartManager.Instance.gameStartSound);
                    if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) {
                        GameStartManager.Instance?.ResetStartState();
                        PlayerControl.LocalPlayer?.RpcSendChat("A player stopped the game start!");
                    }
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[TorNullGuards] stopStart from unresolvable player id {playerId} - handled without the name.");
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] stopStart guard failed: {e}");
                    return false; // the original would throw on exactly this path
                }
            }
        }

        // ── 4) a lone surviving Sidekick must not crash TeamJackalWin ──────────────────────────
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)]
        static class TeamJackalLoneSidekickPatch {
            // TOR's own CustomGameOverReason enum (EndGamePatch.cs) is internal to TOR's assembly -
            // mirrored here as a local constant, the same way UnknownsCollection/Bug.cs already does
            // for this exact value.
            private const int TeamJackalWinReasonValue = 11;

            // Priority.Last guarantees TOR's own OnGameEnd prefix (default priority) has already run,
            // so OnGameEndPatch.gameOverReason is stamped and safe to read - the raw argument is not,
            // since that same TOR prefix rewrites it to ImpostorByKill for any reason >= 10.
            public static void Prefix() {
                try {
                    if ((int)TheOtherRoles.Patches.OnGameEndPatch.gameOverReason != TeamJackalWinReasonValue) return;
                    if (Jackal.jackal != null) return; // Jackal alive (or valid) - TOR's own path is safe

                    PlayerControl sk = Sidekick.sidekick;
                    if (sk == null || sk.Data == null || sk.Data.IsDead) return; // no lone survivor either

                    // Promote the sole surviving Sidekick into the Jackal slot for the duration of
                    // TOR's postfix, so EndGamePatch.cs:192 (Jackal.jackal.Data) has somebody to read
                    // instead of throwing. Clear Sidekick.sidekick too, so the postfix's separate
                    // Sidekick branch (EndGamePatch.cs:196-200) does not add the same player as a
                    // winner a second time. No manual reset afterwards: TOR's own postfix ends in
                    // resetVariables() (EndGamePatch.cs:233), which restores both fields via
                    // clearAndReload() before the next round starts.
                    Jackal.jackal = sk;
                    Sidekick.sidekick = null;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        "[TorNullGuards] lone surviving Sidekick promoted to Jackal for TeamJackalWin end-game processing.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] TeamJackalWin guard failed: {e}");
                }
            }
        }

        // ── 5) BountyHunter must not take every later role update down with it ─────────────────
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "bountyHunterUpdate")]
        static class BountyHunterUpdatePatch {
            // TOR's own filtered target pool can legitimately come up empty; RoleAssignmentPatch.cs:405
            // guards the structurally identical case with `if (possibleTargets.Count == 0)`, this
            // method does not. __exception is non-null exactly when rnd.Next(0, 0) fed the indexer a
            // 0 into an empty list.
            public static Exception Finalizer(Exception __exception) {
                if (__exception == null) return null; // healthy tick, nothing to clean up

                try {
                    // Same cleanup TOR's own IsDead branch performs two lines above the crash site
                    // (PlayerControlPatch.cs:653-663) - there is no living bounty to keep pointing at.
                    if (BountyHunter.arrow != null && BountyHunter.arrow.arrow != null)
                        UnityEngine.Object.Destroy(BountyHunter.arrow.arrow);
                    BountyHunter.arrow = null;
                    if (BountyHunter.cooldownText != null && BountyHunter.cooldownText.gameObject != null)
                        UnityEngine.Object.Destroy(BountyHunter.cooldownText.gameObject);
                    BountyHunter.cooldownText = null;
                    BountyHunter.bounty = null;
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[TorNullGuards] bountyHunterUpdate threw (empty target pool) - reset for the next tick: {__exception.Message}");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] BountyHunter cleanup after crash failed: {e}");
                }
                return null; // swallow: the rest of this frame's role updates (Vulture, Medium, ...) must still run
            }
        }

        // ── 6) a stale bomb coroutine must not explode a destroyed GameObject ──────────────────
        [HarmonyPatch(typeof(Bomb), nameof(Bomb.explode))]
        static class BombExplodePatch {
            public static bool Prefix(Bomb b) {
                try {
                    if (b != null && b.bomb != null) return true; // healthy bomb, TOR's own explode is safe

                    // b is null, or b.bomb has already been Destroy()'d (Unity's == null catches
                    // destroyed references, not just unassigned ones) - TOR would dereference
                    // b.bomb.transform.position next and throw a MissingReferenceException.
                    if (b != null && Bomber.bomb == b) {
                        // The static reference still points at exactly this stale instance (no fresh
                        // bomb was planted since) - finish the cleanup the destroyed GameObjects never
                        // received, mirroring explode()'s own tail (Bomb.cs:103-105) for a healthy bomb.
                        Bomber.clearBomb();
                        Bomb.canDefuse = false;
                        Bomber.isActive = false;
                    }
                    // else: Bomber.bomb already points elsewhere (null, or a genuinely new bomb) -
                    // touching it here would tear down an unrelated, legitimately planted bomb.
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] Bomb.explode guard failed: {e}");
                    return false; // never risk the crash this guard exists to prevent
                }
            }
        }

        // ── 7) a Bloody killer who left must not throw on every FixedUpdate ────────────────────
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch), "bloodyUpdate")]
        static class BloodyUpdatePatch {
            public static bool Prefix() {
                try {
                    if (Bloody.active.Count == 0) return true; // nothing to scan, original is safe

                    // Collect first, mutate after: Bloody.active is a Dictionary, removing entries
                    // while enumerating it throws InvalidOperationException.
                    List<byte> staleKeys = null;
                    foreach (byte key in Bloody.active.Keys) {
                        if (Helpers.playerById(key) != null) continue;
                        staleKeys ??= new List<byte>();
                        staleKeys.Add(key);
                    }
                    if (staleKeys == null) return true; // every entry still resolves, original is safe

                    for (int i = 0; i < staleKeys.Count; i++) {
                        byte key = staleKeys[i];
                        Bloody.active.Remove(key);
                        Bloody.bloodyKillerMap.Remove(key);
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[TorNullGuards] dropped stale Bloody entry for missing killer {key} - " +
                            "the marked killer left before Bloody.duration ran out.");
                    }
                    return true; // pool is clean now, TOR's own loop is safe
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] bloodyUpdate cleanup guard failed: {e}");
                    return true; // let the original try anyway - our cleanup failing is not a reason to freeze it
                }
            }
        }

        // ── 8) overrideDeathReasonAndKiller must not throw on a null player ────────────────────
        [HarmonyPatch]
        static class OverrideDeathReasonAndKillerPatch {
            // GameHistory is internal to TOR's assembly (no InternalsVisibleTo) - resolve by name
            // instead of typeof(), same technique TOR's own Players/CachedPlayer.cs uses for its
            // private nested Start-coroutine type.
            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                Type type = AccessTools.TypeByName("TheOtherRoles.GameHistory");
                return type == null ? null : AccessTools.Method(type, "overrideDeathReasonAndKiller");
            }

            [HarmonyPrefix]
            public static bool Prefix(PlayerControl player) {
                try {
                    if (player != null) return true; // original is safe

                    // TOR's own null check for this parameter exists (GameHistory.cs:54) but sits six
                    // lines below the dereference that already throws - it never runs on this path.
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        "[TorNullGuards] dropped overrideDeathReasonAndKiller for a null player - " +
                        "the referenced player left before the death-reason RPC was processed.");
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] overrideDeathReasonAndKiller guard failed: {e}");
                    return false; // never risk the crash this guard exists to prevent
                }
            }
        }

        // ── 9) handleKick must not throw when kicker or target already left ────────────────────
        [HarmonyPatch(typeof(TheOtherRoles.Utilities.EventUtility), nameof(TheOtherRoles.Utilities.EventUtility.handleKick))]
        static class HandleKickPatch {
            public static bool Prefix(PlayerControl source, PlayerControl target) {
                try {
                    if (source != null && target != null) return true; // original is safe

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        "[TorNullGuards] dropped Event kick - source or target left before the delayed kick RPC fired.");
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] handleKick guard failed: {e}");
                    return false; // never risk the crash this guard exists to prevent
                }
            }
        }

        // ── 10) guesserShoot must not take the rest of vote handling down with it ──────────────
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        static class GuesserShootPatch {
            // Symptom fix, not a cause fix: the unguarded dereference (RPC.cs:1032,
            // `voteAreaPlayer.AmOwner` on a possibly-null Helpers.playerById lookup) sits inside a
            // long vote-return loop in the middle of a much larger method (killer/lover/lawyer death
            // handling, sounds, overlays, CheckForEndVoting). Rebuilding all of that from the outside
            // to reach one dereference was judged too risky - a Finalizer at least keeps
            // CheckForEndVoting and everything after it running instead of losing the whole meeting.
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null)
                    UsefulTORStuffPlugin.Logger?.LogError(
                        $"[TorNullGuards] guesserShoot threw (likely a disconnected voter) - swallowed: {__exception}");
                return null;
            }
        }

        // ── 11) startTeleport must not throw for a player who already left ─────────────────────
        [HarmonyPatch(typeof(Portal), nameof(Portal.startTeleport))]
        static class StartTeleportPatch {
            public static bool Prefix(byte playerId) {
                try {
                    if (Helpers.playerById(playerId) != null) return true; // original is safe

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[TorNullGuards] skipped startTeleport for missing player {playerId} - " +
                        "they left before the portal teleport RPC fired.");
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorNullGuards] startTeleport guard failed: {e}");
                    return false; // never risk the crash this guard exists to prevent
                }
            }
        }

        // ── 12) guesserOnClick must not crash while building the role-selection list ───────────
        [HarmonyPatch]
        static class GuesserOnClickPatch {
            // MeetingHudPatch is internal to TOR's assembly - resolved by name like (8).
            [HarmonyTargetMethod]
            static MethodBase TargetMethod() {
                Type type = AccessTools.TypeByName("TheOtherRoles.Patches.MeetingHudPatch");
                return type == null ? null : AccessTools.Method(type, "guesserOnClick");
            }

            // Same reasoning as (10): the crash (MeetingPatch.cs:455) sits in the middle of a loop
            // that builds and wires up UI buttons via closures capturing local state (selectedButton,
            // buttons, container, ...). A Prefix skipping the whole method would leave the Guesser UI
            // half-built with no way to close it - worse than the crash. A Finalizer swallows the
            // exception instead, symptom fix rather than cause fix, same as (10).
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception) {
                if (__exception != null)
                    UsefulTORStuffPlugin.Logger?.LogError(
                        $"[TorNullGuards] guesserOnClick threw while building the role list (likely a disconnected target) - swallowed: {__exception}");
                return null;
            }
        }
    }
}
