// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorPerfFixes - performance patches against TOR's own code (AUDIT-2026-08-16; 5/6 leak fixes 2026-11-12). None of these
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
using TheOtherRoles.Objects;
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

                    // A sub-page switch (SettingsOverlayView, 6a -> 6b) counts as a page change, so TAB
                    // redraws at once instead of after the next throttle tick.
                    int page = TheOtherRolesPlugin.optionsPage * 64 + SettingsOverlayView.SubPage;
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
                    // AUDIT-2026-08-17 (A11): outside online games TOR's own Setup returns before
                    // activating the ProceedButton, so GetComponentInChildren finds no TextMeshPro,
                    // Text stays null and TOR's Postfix throws `Text.text` on EVERY meeting Update
                    // in Freeplay/local games. Mirror Setup's own gate and skip the Postfix there.
                    if (AmongUsClient.Instance != null && AmongUsClient.Instance.NetworkMode != NetworkModes.OnlineGame)
                        return false;

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

        // ── 5) Dead CustomButtons must not keep the old HUD alive ─────────────────────────────
        // TOR registers every CustomButton's onClickEvent on its PassiveButton (OnClick.AddListener). Il2CppInterop
        // bridges that .NET delegate with an Il2CppToMonoDelegateReference: the il2cpp object holds the .NET
        // delegate (and with it the CustomButton) through a GC handle, and the CustomButton's wrappers hold the
        // il2cpp side again. Neither garbage collector can see the cycle as a whole, so a destroyed button never
        // went away - and through CustomButton.hudManager it kept the whole previous HudManager graph with it
        // (multi-client test 2026-11-12: one more HudManager, 64 more delegate bridges and ~2.5 MB of live il2cpp
        // objects per round, which also fragmented the 32-bit il2cpp heap). Right before TOR drops a dead button
        // from its list (HudUpdate / MeetingEndedUpdate both start with RemoveAll(actionButton == null)), its
        // references into the game are cut. Callbacks become empty stubs instead of null, so code that still holds
        // an old button (another mod's cached reference) gets false/no-op instead of a NullReferenceException.
        [HarmonyPatch]
        static class DeadCustomButtonRelease {
            private static readonly Func<bool> No = () => false;
            private static readonly Action Nothing = () => { };
            private static readonly AccessTools.FieldRef<CustomButton, Action> fOnClick = AccessTools.FieldRefAccess<CustomButton, Action>("OnClick");
            private static readonly AccessTools.FieldRef<CustomButton, Action> fInitialOnClick = AccessTools.FieldRefAccess<CustomButton, Action>("InitialOnClick");
            private static readonly AccessTools.FieldRef<CustomButton, Action> fOnMeetingEnds = AccessTools.FieldRefAccess<CustomButton, Action>("OnMeetingEnds");
            private static readonly AccessTools.FieldRef<CustomButton, Action> fOnEffectEnds = AccessTools.FieldRefAccess<CustomButton, Action>("OnEffectEnds");
            private static int released;

            public static IEnumerable<MethodBase> TargetMethods() {
                yield return AccessTools.Method(typeof(CustomButton), nameof(CustomButton.HudUpdate));
                yield return AccessTools.Method(typeof(CustomButton), nameof(CustomButton.MeetingEndedUpdate));
            }

            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                var list = CustomButton.buttons;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++) {
                    var b = list[i];
                    if (b == null || ReferenceEquals(b.hudManager, null) && ReferenceEquals(b.actionButtonGameObject, null)) continue;
                    // Destroyed? m_CachedPtr is read straight from the object; Unity's "!= null" would box its result on
                    // the il2cpp heap for every button twice per frame.
                    var ab = b.actionButton;
                    if (!ReferenceEquals(ab, null) && ab.m_CachedPtr != IntPtr.Zero) continue;
                    try {
                        b.actionButton = null;
                        b.actionButtonGameObject = null;
                        b.actionButtonRenderer = null;
                        b.actionButtonMat = null;
                        b.actionButtonLabelText = null;
                        b.Sprite = null;
                        b.hudManager = null;
                        b.HasButton = No;
                        b.CouldUse = No;
                        fOnClick(b) = Nothing;
                        fInitialOnClick(b) = Nothing;
                        fOnMeetingEnds(b) = Nothing;
                        fOnEffectEnds(b) = Nothing;
                        released++;
                    } catch (Exception e) {
                        UsefulTORStuffPlugin.Logger?.LogWarning($"[TorPerfFixes] releasing a dead CustomButton failed: {e.Message}");
                    }
                }
            }

            internal static int Released => released;
        }

        // ── 6) Lambdas on HUD buttons must not keep the old HUD alive ─────────────────────────
        // Same cycle as 5) for every other runtime listener on a HUD or meeting button: TOR's settings, zoom and
        // summary toggles capture the HudManager in their lambda (HudManagerUpdate.Postfix/Postfix2), the meeting
        // buttons capture the MeetingHud. The buttons die with the HUD at the end of the game, the bridges did not.
        // At the end of the game every runtime listener on the HUD's and the meeting's PassiveButtons is removed:
        // nothing can click them any more, and RemoveAllListeners leaves the prefab's own persistent listeners alone.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        static class HudListenerSweep {
            [HarmonyPriority(Priority.Last)]
            public static void Prefix() {
                int n = 0;
                try {
                    if (HudManager.InstanceExists) n += Sweep(HudManager.Instance.gameObject);
                    if (MeetingHud.Instance != null) n += Sweep(MeetingHud.Instance.gameObject);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[TorPerfFixes] HUD listener sweep failed: {e.Message}");
                }
                if (n > 0) UsefulTORStuffPlugin.Logger?.LogInfo($"[TorPerfFixes] released the runtime listeners of {n} HUD button(s) at the end of the game.");
            }

            private static int Sweep(GameObject root) {
                int n = 0;
                foreach (var pb in root.GetComponentsInChildren<PassiveButton>(true)) {
                    if (pb == null) continue;
                    try {
                        pb.OnClick?.RemoveAllListeners();
                        pb.OnMouseOver?.RemoveAllListeners();
                        pb.OnMouseOut?.RemoveAllListeners();
                        n++;
                    } catch { }
                }
                return n;
            }
        }

        // ── 7) Delegate bridges whose lambda only holds destroyed game objects are cut ───────────
        // Every .NET lambda handed to the game (button listeners, Effects.Lerp callbacks, ...) goes through an
        // Il2CppInterop Il2CppToMonoDelegateReference. That bridge and its .NET side keep each other alive across
        // the two garbage collectors, so it is never freed, and neither is the lambda's closure: TOR's settings and
        // summary toggles (HudManagerUpdate.Postfix/Postfix2) and its intro role text (SetUpRoleTextPatch) captured
        // the HudManager / IntroCutscene, which kept one complete old HUD per round alive (multi-client test
        // 2026-11-12). The bridges are recorded when they are created; on every lobby join, a bridge whose target
        // holds Unity objects that are ALL destroyed drops its reference to the .NET delegate. The bridge itself
        // stays (a few bytes), the closure and the old HUD behind it can go. A dropped bridge can only be invoked by
        // something that belonged to the destroyed objects as well; Il2CppInterop then logs a caught exception.
        [HarmonyPatch]
        static class DelegateBridgeRelease {
            private static readonly Type bridgeType = AccessTools.TypeByName("Il2CppInterop.Runtime.DelegateSupport+Il2CppToMonoDelegateReference");
            private static readonly FieldInfo fDelegate = bridgeType != null ? AccessTools.Field(bridgeType, "ReferencedDelegate") : null;
            private static readonly PropertyInfo pDelegate = bridgeType != null && fDelegate == null ? AccessTools.Property(bridgeType, "ReferencedDelegate") : null;
            private static readonly List<WeakReference> bridges = new();
            // Coroutines started with WrapToIl2Cpp() have the same cycle (Il2CppManagedEnumerator holds the .NET
            // state machine): Atlas' eject scene kept its ExileController and the HUD it touched (+1 per round).
            private static readonly Type enumType = AccessTools.TypeByName("BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator");
            private static readonly FieldInfo fEnumerator = enumType != null ? AccessTools.Field(enumType, "enumerator") : null;
            private static readonly List<WeakReference> coroutines = new();
            private static readonly System.Collections.IEnumerator EmptyEnumerator = new object[0].GetEnumerator();
            private static readonly object gate = new();

            public static bool Prepare() {
                bool ok = bridgeType != null && (fDelegate != null || (pDelegate != null && pDelegate.CanWrite));
                if (!ok) UsefulTORStuffPlugin.Logger?.LogWarning("[TorPerfFixes] Il2CppToMonoDelegateReference.ReferencedDelegate not found - delegate bridges are not released.");
                return ok;
            }

            public static IEnumerable<MethodBase> TargetMethods() {
                foreach (var c in AccessTools.GetDeclaredConstructors(bridgeType)) yield return c;
                if (fEnumerator != null)
                    foreach (var c in AccessTools.GetDeclaredConstructors(enumType))
                        if (c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(System.Collections.IEnumerator)) yield return c;
            }

            public static void Postfix(object __instance) {
                lock (gate) {
                    var list = bridgeType.IsInstanceOfType(__instance) ? bridges : coroutines;
                    if (list.Count >= 4000) list.RemoveRange(0, 1000);     // bound the bookkeeping, oldest first
                    list.Add(new WeakReference(__instance));
                }
            }

            private static Delegate Get(object b) => (fDelegate != null ? fDelegate.GetValue(b) : pDelegate.GetValue(b)) as Delegate;
            private static void Drop(object b) { if (fDelegate != null) fDelegate.SetValue(b, null); else pDelegate.SetValue(b, null); }

            // true when the delegate's target references Unity objects and every one of them is destroyed
            private static bool Dead(Delegate d) => DeadTarget(d?.Target);

            // A compiler-generated iterator that ran to its end (or was disposed) has a negative "<>1__state". The sweep
            // runs between frames, so no MoveNext is on the stack: such a coroutine can never continue, but its captured
            // parameters and locals stayed alive through the bridge.
            private static bool RanToEnd(object it) {
                var f = it.GetType().GetField("<>1__state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null && f.FieldType == typeof(int) && (int)f.GetValue(it) < 0;
            }

            private static bool DeadTarget(object t) {
                if (t == null) return false;                                    // static lambda: captures nothing
                if (t is CustomButton cb) return ReferenceEquals(cb.actionButton, null) && ReferenceEquals(cb.hudManager, null);   // released by 5)
                if (t is UnityEngine.Object own) return !ReferenceEquals(own, null) && own == null
                    && (own is HudManager || own is MeetingHud || own is IntroCutscene || own is ExileController || own is ShipStatus);
                // Only closures/coroutines that belong to a torn-down scene object: every Unity object they hold is
                // destroyed AND one of them is a per-round singleton (HUD, meeting, intro, exile, ship). Something that
                // only holds old PlayerControls may legitimately still run across a lobby change (players are
                // re-spawned there), so it is never touched (found by the multi-client harness 2026-11-12).
                int unity = 0, alive = 0, scene = 0;
                foreach (var f in t.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)) continue;
                    var v = f.GetValue(t) as UnityEngine.Object;
                    if (ReferenceEquals(v, null)) continue;
                    unity++;
                    if (v != null) alive++;
                    else if (v is HudManager || v is MeetingHud || v is IntroCutscene || v is ExileController || v is ShipStatus) scene++;
                }
                return unity > 0 && alive == 0 && scene > 0;
            }

            internal static void Sweep() {
                int dropped = 0, kept = 0;
                lock (gate) {
                    for (int i = bridges.Count - 1; i >= 0; i--) {
                        object b = bridges[i].Target;
                        if (b == null) { bridges.RemoveAt(i); continue; }
                        try {
                            var d = Get(b);
                            if (d == null) { bridges.RemoveAt(i); continue; }
                            if (Dead(d)) { Drop(b); bridges.RemoveAt(i); dropped++; }
                            else kept++;
                        } catch { }
                    }
                }
                int stopped = 0;
                lock (gate) {
                    for (int i = coroutines.Count - 1; i >= 0; i--) {
                        object e = coroutines[i].Target;
                        if (e == null) { coroutines.RemoveAt(i); continue; }
                        try {
                            var inner = fEnumerator.GetValue(e);
                            if (inner == null || ReferenceEquals(inner, EmptyEnumerator)) { coroutines.RemoveAt(i); continue; }
                            if (RanToEnd(inner) || DeadTarget(inner)) { fEnumerator.SetValue(e, EmptyEnumerator); coroutines.RemoveAt(i); stopped++; }
                        } catch { }
                    }
                }
                if (dropped > 0 || stopped > 0) UsefulTORStuffPlugin.Logger?.LogInfo($"[TorPerfFixes] released {dropped} delegate bridge(s) and {stopped} coroutine(s) of destroyed objects ({kept} bridges still in use).");
            }
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
                DelegateBridgeRelease.Sweep();   // the previous HUD is gone by now (see 7)
                // resetVariables is only ever sent by a same-version TOR host; a vanilla/mismatched
                // host would never clear ShowHostThrottle's lastHostId, so OnGameJoined (fires on
                // every lobby (re-)entry, host or not) is the one reset that always runs.
                ShowHostThrottle.EnsureInstalled();
            }
        }
    }
}
