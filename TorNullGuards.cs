// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorNullGuards - crash guards for three unguarded dereferences in TOR itself (AUDIT-2026-08-15).
 *
 * All three are option-less bugfixes in the same spirit as the Bloody throttle: they only ever turn
 * a crash or a freeze into normal behaviour, they can hand nobody an advantage, and they are
 * therefore deliberately NOT behind UTSGate. A client running this mod is protected even when the
 * host does not have it - which is the whole point, since two of the three take down that one client.
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
 */

using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using TheOtherRoles;

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
    }
}
