// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorPerfFixes - four performance patches against TOR's own code (AUDIT-2026-08-16). None of these
 * change what the player sees; they only make TOR rebuild the same output less often. All four are
 * option-less and NOT behind UTSGate: a purely local reduction in per-frame work can never hand
 * anyone an advantage, so there is nothing to gate.
 *
 *  1) The F1 settings overlay rebuilds its entire text EVERY frame while open.
 *     HudManagerUpdate.Prefix2 (Modules/CustomOptions.cs) is TOR's own Harmony prefix on
 *     HudManager.Update. Its only gate is "is the overlay open" (`if (!settingsTMPs[0]) return;`);
 *     once open it calls GameOptionsDataPatch.buildAllOptions() and reassigns up to four
 *     TextMeshPro.text fields (each a full mesh rebuild) on every single Update - in the lobby AND
 *     mid-round. Worse, buildAllOptions() itself calls CurrentGameOptions.ToHudString(), whose own
 *     Postfix (same file) calls buildAllOptions() a SECOND time, so the whole rebuild runs twice per
 *     frame. HudManagerUpdate and Prefix2 both turned out to be public (verified by reading
 *     Modules/CustomOptions.cs directly - the task brief that assigned this fix expected reflection
 *     here, but it is not needed), so we patch Prefix2 itself the same way TiebreakerMultiple wraps
 *     TOR's own MeetingCalculateVotesPatch.Prefix: a prefix on TOR's prefix, `return false` skips its
 *     body deterministically because we are the only patch on that exact method.
 *     Throttle: at most one rebuild every 0.25s (imperceptible for a settings text), but an immediate,
 *     unthrottled pass-through whenever the page just changed (TAB/number keys must stay
 *     delay-free) or the overlay was closed on the previous check (the very first frame after opening
 *     must never show blank/stale text). Open/closed state is read from TOR's private `settingsTMPs`
 *     field via one cached FieldInfo, mirroring TOR's own gate exactly; if that field is ever renamed
 *     the reflection fails closed (isOpen stays false forever) and every call simply passes through
 *     unthrottled - TOR's original behaviour, never a broken one.
 *
 *  2) Helpers.MushroomSabotageActive() allocates a fresh array every call.
 *     `PlayerControl.LocalPlayer.myTasks.ToArray().Any(...)` copies the whole task list even though
 *     the result depends only on the local player and cannot change within a single frame. It is
 *     called from multiple per-player loops (resetNameTagsAndColors via hidePlayerName,
 *     setBasePlayerOutlines twice per player), roughly 2400 needless allocations/second at 15
 *     players. We cache the result for the current Time.frameCount: first call in a frame runs the
 *     original and a Postfix records the result; later calls in the same frame are served from the
 *     cache via a Prefix that sets __result and returns false. The cache only ever holds one frame's
 *     worth of data, so per REGEL 2 it needs no round reset - but it is still cleared on lobby join so
 *     no stale value from a previous session can ever be read even for a single frame.
 *
 *  3) RoleInfo.GetRolesString is rebuilt twice per player, every fixed tick, once the local player
 *     is dead and spectating.
 *     PlayerControlPatch.updatePlayerInfo (Patches/PlayerControlPatch.cs:533-535) runs the inner
 *     "show role text" branch for every player once `PlayerControl.LocalPlayer.Data.IsDead` is true,
 *     calling `RoleInfo.GetRolesString(p, true, false)` and
 *     `RoleInfo.GetRolesString(p, true, TORMapOptions.ghostsSeeModifier)` for each one.
 *     GetRolesString calls getRoleInfoForPlayer (RoleInfo.cs:166-247), which allocates a fresh List
 *     and runs about nine LINQ .Any() closures per player before Select/ToArray/String.Join build the
 *     final string. We cache the built string per (PlayerId, useColors, showModifier,
 *     suppressGhostInfo) - the exact parameter tuple GetRolesString is keyed on, read straight from
 *     its real signature (RoleInfo.cs:249) rather than guessed - with a 0.25s TTL: a Prefix serves a
 *     fresh-enough entry via __result/return false, a Postfix records every real computation.
 *     This cache holds per-player round state (a stale entry would show last round's role text for up
 *     to 0.25s into the next one), so it is cleared on BOTH RPCProcedure.resetVariables (round reset)
 *     and AmongUsClient.OnGameJoined (lobby reset, PlayerIds are reused across lobbies) - this is the
 *     one reset in this file that actually matters.
 *
 *  4) MeetingPatch.cs ShowHost.Postfix rebuilds the host string and re-sets material colors every
 *     single frame of every meeting, even though the host essentially never changes mid-meeting.
 *     ShowHost is `public class ShowHost` nested inside `class MeetingHudPatch`, which has no access
 *     modifier and is therefore internal to TOR's own assembly - unlike case (1), this one genuinely
 *     cannot be named from UsefulTORStuff at compile time (CS0122), so it is resolved by reflection,
 *     confirmed to exist exactly as described by reading Patches/MeetingPatch.cs:799-829. We wrap its
 *     Postfix(MeetingHud) with our own prefix that passes through only when the host's PlayerId
 *     changed since the last call, or when TOR's own cached `Text` field (also resolved by reflection)
 *     is unset/destroyed - which is exactly the condition under which TOR's own code would otherwise
 *     leave a fresh meeting's ProceedButton text unset, so a meeting-start rebuild is never skipped.
 *     Manual `harmony.Patch(...)` needs a live method handle from the already-loaded TOR assembly, so
 *     it cannot be attribute-based like the other three fixes here, and this file may not add a call
 *     into UsefulTORStuffPlugin.Awake (out of scope: only this file may change). Instead the wrap is
 *     installed lazily, once, from this file's own AmongUsClient.OnGameJoined postfix - by the time a
 *     player can ever join a lobby, BepInEx has long finished loading every plugin's assembly and
 *     applying every other mod's Harmony patches, so the reflection lookup and the manual patch are
 *     guaranteed to run against a fully initialized TOR assembly. If the lookup ever fails (a TOR
 *     rename) it logs a warning once and leaves TOR's own Postfix completely unpatched - never a
 *     half-installed throttle. The remembered host id is a round/lobby-scoped fact about who is host,
 *     so it is cleared on both resetVariables and OnGameJoined like case (3).
 *
 * Deliberately NOT touched (bau NICHTS auf Verdacht):
 *  - TOR's lobby-countdown loop that runs `AmongUsClient.Instance.allClients.ToArray()` every frame,
 *    and CustomButton.Update(), which reassigns sprite/text/material unconditionally every frame.
 *    Both sit inside large methods that also drive the kick timer, the lobby countdown and RPCs
 *    (allClients loop) or cooldown timers and hotkeys (CustomButton.Update) and must keep running.
 *    An outside Harmony patch could only skip them by fully reimplementing the surrounding method,
 *    which is a correctness risk out of proportion to the (comparatively small) gain, so this file
 *    leaves both alone.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    public static class TorPerfFixes {
        // ── 1) F1 settings overlay must not rebuild its text every frame ───────────────────────
        [HarmonyPatch(typeof(HudManagerUpdate), nameof(HudManagerUpdate.Prefix2))]
        static class SettingsOverlayThrottlePatch {
            private const float RebuildInterval = 0.25f;

            // TOR's own open/closed gate (`if (!settingsTMPs[0]) return;`) reached the same way TOR
            // reads it - settingsTMPs is private static TMPro.TextMeshPro[4] on HudManagerUpdate.
            private static readonly FieldInfo settingsTmpsField =
                AccessTools.Field(typeof(HudManagerUpdate), "settingsTMPs");

            private static bool wasOpen;
            private static float lastRebuildTime = float.NegativeInfinity;
            private static int lastPage = int.MinValue;

            public static bool Prefix() {
                try {
                    var arr = settingsTmpsField?.GetValue(null) as TMPro.TextMeshPro[];
                    bool isOpen = arr != null && arr.Length > 0 && arr[0] != null;
                    if (!isOpen) {
                        // Overlay closed (or the field could not be resolved) - TOR's own early
                        // return is already cheap, and forgetting "wasOpen" here means the next
                        // open is always treated as fresh, never throttled on its first frame.
                        wasOpen = false;
                        return true;
                    }

                    int page = TheOtherRolesPlugin.optionsPage;
                    float now = Time.time;
                    bool justOpened = !wasOpen;
                    bool pageChanged = page != lastPage;
                    if (justOpened || pageChanged || now - lastRebuildTime >= RebuildInterval) {
                        wasOpen = true;
                        lastPage = page;
                        lastRebuildTime = now;
                        return true; // let TOR's own Prefix2 rebuild the text
                    }
                    return false; // too soon, same page, overlay was already open - skip the rebuild
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorPerfFixes] settings overlay throttle failed: {e}");
                    return true; // never risk hiding a legitimate rebuild
                }
            }
        }

        // ── 2) MushroomSabotageActive must not reallocate the task array every call ────────────
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.MushroomSabotageActive))]
        static class MushroomSabotageActiveCachePatch {
            private static int cachedFrame = int.MinValue;
            private static bool cachedResult;

            public static bool Prefix(ref bool __result) {
                try {
                    if (Time.frameCount != cachedFrame) return true; // stale/unset - let the original run
                    __result = cachedResult;
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorPerfFixes] MushroomSabotageActive cache read failed: {e}");
                    return true;
                }
            }

            public static void Postfix(bool __result) {
                cachedFrame = Time.frameCount;
                cachedResult = __result;
            }

            // No round-state here (Regel 2 exempts a single-frame buffer), but a lobby switch must
            // never let a value computed for a different session's PlayerControl.LocalPlayer live on.
            public static void ResetOnLobbyJoin() => cachedFrame = int.MinValue;
        }

        // ── 3) RoleInfo.GetRolesString must not be rebuilt twice per player every fixed tick ───
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.GetRolesString))]
        static class GetRolesStringCachePatch {
            private const float Ttl = 0.25f;

            // Keyed on exactly the parameters GetRolesString itself is keyed on (RoleInfo.cs:249),
            // so the two different call shapes from updatePlayerInfo (showModifier true vs. false)
            // never overwrite each other's cached text.
            private static readonly Dictionary<(byte playerId, bool useColors, bool showModifier, bool suppressGhostInfo), (string text, float time)>
                cache = new();

            // __state carries "this call was served from the cache" over to the Postfix. It has to:
            // HarmonyX runs the Postfix even when our Prefix returned false, so without this flag the
            // Postfix would stamp Time.time onto the entry on every cache HIT as well. Since
            // updatePlayerInfo asks far more often than once per TTL, the entry would keep renewing
            // itself and never expire - the text would freeze at its first value for the whole round,
            // and a role change (Bloody, Vip, Sidekick promotion, ...) would never show up again.
            public static bool Prefix(PlayerControl p, bool useColors, bool showModifier, bool suppressGhostInfo,
                                      ref string __result, out bool __state) {
                __state = false;
                try {
                    if (p == null) return true; // let the original handle it exactly as before

                    var key = (p.PlayerId, useColors, showModifier, suppressGhostInfo);
                    if (cache.TryGetValue(key, out var entry) && Time.time - entry.time < Ttl) {
                        __result = entry.text;
                        __state = true;   // served from cache - the Postfix must not touch the timestamp
                        return false;
                    }
                    return true; // Postfix below records the freshly computed result
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorPerfFixes] GetRolesString cache read failed: {e}");
                    return true;
                }
            }

            public static void Postfix(PlayerControl p, bool useColors, bool showModifier, bool suppressGhostInfo,
                                       string __result, bool __state) {
                try {
                    if (__state) return; // cache hit, not a real computation - leave the TTL alone
                    if (p == null) return;
                    cache[(p.PlayerId, useColors, showModifier, suppressGhostInfo)] = (__result, Time.time);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorPerfFixes] GetRolesString cache write failed: {e}");
                }
            }

            // The important reset in this file: PlayerIds are reused across rounds and lobbies, and a
            // stale entry here would show a player's PREVIOUS role for up to 0.25s under their new one.
            public static void Clear() => cache.Clear();
        }

        // ── 4) Meeting host text/colors must not be rebuilt every frame of a meeting ───────────
        static class ShowHostThrottle {
            // Resolved once, lazily, from OnGameJoined (see LobbyResetAndInstallPatch below) - by
            // then every plugin's Awake/PatchAll has already run, so TOR's assembly is fully loaded.
            private static bool installAttempted;
            private static FieldInfo textField; // MeetingHudPatch+ShowHost.Text (private static TextMeshPro)
            private static byte lastHostId = byte.MaxValue; // sentinel: no real AU player ever has this id

            public static void EnsureInstalled() {
                if (installAttempted) return;
                installAttempted = true;
                try {
                    Type torAsmShowHost = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.MeetingHudPatch+ShowHost");
                    if (torAsmShowHost == null) {
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[TorPerfFixes] MeetingHudPatch+ShowHost not found - host-text throttle disabled.");
                        return;
                    }

                    MethodInfo postfixMethod = torAsmShowHost.GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static);
                    textField = torAsmShowHost.GetField("Text", BindingFlags.NonPublic | BindingFlags.Static);
                    if (postfixMethod == null || textField == null) {
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[TorPerfFixes] ShowHost.Postfix/Text not found - host-text throttle disabled.");
                        textField = null;
                        return;
                    }

                    // Own Harmony instance: we only have a reflected MethodInfo here, not the shared
                    // instance UsefulTORStuffPlugin.Awake keeps local to itself (out of scope to expose).
                    var harmony = new Harmony("com.tormod.usefultorstuff.torperffixes.showhost");
                    harmony.Patch(postfixMethod, prefix: new HarmonyMethod(typeof(ShowHostThrottle), nameof(Prefix)));
                    UsefulTORStuffPlugin.Logger?.LogInfo("[TorPerfFixes] ShowHost.Postfix throttle installed.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorPerfFixes] ShowHost throttle install failed: {e}");
                    textField = null;
                }
            }

            public static bool Prefix() {
                try {
                    var host = GameData.Instance?.GetHost();
                    if (host == null) return true; // TOR's own null-check path, nothing to throttle

                    // Mirrors Unity's fake-null: a destroyed TextMeshPro still equals null through its
                    // overloaded operator, exactly like TOR's own `if (Text == null)` check.
                    bool textMissing = true;
                    if (textField != null) {
                        var currentText = textField.GetValue(null) as TMPro.TextMeshPro;
                        textMissing = currentText == null;
                    }

                    if (!textMissing && host.PlayerId == lastHostId) return false; // unchanged - skip

                    lastHostId = host.PlayerId;
                    return true; // host changed, or the text isn't set up yet for this meeting
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorPerfFixes] ShowHost throttle read failed: {e}");
                    return true;
                }
            }

            public static void Clear() => lastHostId = byte.MaxValue;
        }

        // ── Reset hooks (REGEL 2: every cache above that carries round/lobby state clears here) ─
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class RoundResetPatch {
            public static void Postfix() {
                GetRolesStringCachePatch.Clear();
                ShowHostThrottle.Clear();
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetAndInstallPatch {
            public static void Postfix() {
                MushroomSabotageActiveCachePatch.ResetOnLobbyJoin();
                GetRolesStringCachePatch.Clear();
                ShowHostThrottle.Clear();
                // resetVariables is only ever sent by a same-version TOR host; a vanilla/mismatched
                // host would never clear ShowHostThrottle's lastHostId, so OnGameJoined (fires on
                // every lobby (re-)entry, host or not) is the one reset that always runs.
                ShowHostThrottle.EnsureInstalled();
            }
        }
    }
}
