// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorLeakFixes - the object leaks, cross-round state leaks and a couple of per-frame costs from the
 * 2026-08-23 full-source audit (Audits\TOR-AUDIT-2026-08-23.md, section 3) that no other Tor* file
 * reaches. Every finding below was re-read against the current TheOtherRoles-main source before a
 * single line was written here; several turned out to already be handled correctly by TOR itself and
 * are reported as such rather than patched (REGEL 1). All fixes are option-less and NOT behind
 * UTSGate: a purely local reduction in leaked objects or wasted work can never hand anyone an
 * advantage.
 *
 * FIXED HERE:
 *
 *  1) MapBehaviourPatch.Postfix (Patches/MapBehaviourPatch.cs:166-168): the vent-cleaning task's map
 *     marker does `MapIcon.transform.localScale *= 0.6f` every single FixedUpdate the condition
 *     holds, instead of setting it once. At ~40-50 FixedUpdate calls/second the icon shrinks toward
 *     zero within about a second and stays invisible for the rest of the task. The multiply itself is
 *     buried inside a large private Postfix we cannot edit, so instead of guessing at the icon's
 *     original (pre-shrink) scale, VentCleanIconShrinkPatch runs its own Postfix on the same
 *     MapBehaviour.FixedUpdate at Priority.Last (guaranteed to observe TOR's multiply having already
 *     happened this frame - HarmonyX sorts postfixes by priority the same way it sorts prefixes, see
 *     EndScreenLeavers.cs/TorRoundFixes.cs for the established precedent) and cancels every multiply
 *     AFTER the first one for the same vent by dividing back out the redundant 0.6 factor. The first
 *     application is left completely alone, so the intended "shrink to 0.6x while this vent is my
 *     active target" cue still happens exactly once. No reflection needed: MapBehaviour, PlayerControl,
 *     TaskTypes and the icon's GameObject name (`$"vent {id} icon"`, set explicitly by TOR itself) are
 *     all public vanilla API, already used the same way elsewhere in this codebase (TorPerfFixes #2).
 *
 *  2) SurveillanceMinigame's SecurityGuard camera extension (Patches/UsablesPatch.cs:513-524,
 *     550-556) leaks two different Unity resource types every time the security camera minigame is
 *     used, neither of which is cleaned up by TOR's own OnDestroy handler (which only resets night
 *     vision, UsablesPatch.cs:591-596):
 *       - Begin() calls RenderTexture.GetTemporary() once per camera beyond the vanilla 4 and never
 *         releases them. Temporary render textures are not freed just because the referencing object
 *         is destroyed - they must be explicitly released back to Unity's pool.
 *       - Update()'s page-turn branch does `ViewPorts[i].sharedMaterial = DefaultMaterial;` followed
 *         immediately by `.material.SetTexture(...)`. Resetting sharedMaterial to the raw shared asset
 *         invalidates the "already instanced" flag Unity tracks per-renderer, so the very next
 *         `.material` access clones a brand new Material instance - one per view slot, every page
 *         turn (automatic every 3s, or on every arrow-key press). The clone from the previous page is
 *         never referenced again and is never Destroyed.
 *     SecurityCameraCleanupPatch (Prefix on OnDestroy, runs before the object dies) releases every
 *     RenderTexture at index >=4 (indices 0-3 are vanilla's own, never touched) and destroys any
 *     Material clone this file is still tracking. SecurityCameraMaterialChurnPatch (Postfix on
 *     Update, runs after TOR's own Prefix - a Postfix always runs regardless of what a Prefix
 *     returned, see HarmonyX-Prefix-Fallstrick in project memory) never calls `.material` itself
 *     (that getter is what clones), it only reads `.sharedMaterial` to notice TOR replaced a
 *     tracked clone with something else, and destroys the orphaned one. SecurityCameraBeginResetPatch
 *     is a defensive-only clear of the tracking table in case OnDestroy is ever skipped.
 *
 *  3) JackInTheBox.clearJackInTheBoxes() (Objects/JackInTheBox.cs:129-132), called from every single
 *     RPCProcedure.resetVariables() (RPC.cs:183, i.e. every round start), replaces AllJackInTheBoxes
 *     with a new empty list without ever destroying a single box's marker GameObject or its
 *     Instantiate-cloned ghost Vent. Confirmed as a real cross-round leak (not just a same-round one):
 *     ShipStatus itself IS destroyed and recreated every round (Utilities/MapUtilities.cs:52-61 hooks
 *     ShipStatus.OnDestroy precisely because of this), but these box/vent GameObjects are created
 *     with no parent at all (`new GameObject(...)`, `Instantiate<Vent>(referenceVent)` with no parent
 *     argument) - they sit at scene root, independent of ShipStatus's hierarchy, and a scene-root
 *     object survives a child object's destruction. Every Trickster who ever played leaves their
 *     boxes and ghost vents behind forever. JackInTheBoxClearLeakPatch is a Prefix (runs before the
 *     list reference is dropped) that Destroys box.vent.gameObject (public field, no reflection) and,
 *     via one cached FieldInfo, the box's own private `gameObject` field - the exact shape Trap.cs's
 *     own (correct) clearTraps() uses for its two objects.
 *
 *  4) NinjaTrace.clearTraces() (Objects/NinjaTrace.cs:64-66) has the identical bug, one class over:
 *     `traces = new List<NinjaTrace>()` with no Destroy. Silhouette.cs in the very same folder
 *     (clearSilhouettes(), :55-59) shows the correct shape for what is otherwise a near-identical
 *     class - foreach + Destroy before dropping the list. NinjaTrace is an internal class (unlike the
 *     public JackInTheBox/Silhouette), so NinjaTraceClearLeakPatch resolves it via
 *     TargetMethod()/Prepare() like the rest of this codebase's internal-type patches, and reaches the
 *     private instance field `trace` through one cached FieldInfo.
 *
 *  5) Trap.clearRevealedTraps() (Objects/Trap.cs:64-71), called once per meeting end
 *     (MeetingPatch.cs:763), destroys a revealed trap's marker GameObject but not its HUD Arrow
 *     (`t.arrow.arrow`) - unlike Trap's own clearTraps() (the full-clear sibling, :54-62), which
 *     destroys both. Every trap a Trapper fully reveals during a round leaks one Arrow GameObject.
 *     TrapRevealedArrowLeakPatch reaches Trap (internal) via TargetMethod()/Prepare(); once it has a
 *     `Trap` instance, `.arrow` (Arrow, public class, TheOtherRoles.Objects) is read straight off the
 *     reflected instance value without further reflection, since Arrow itself is a public type.
 *     The audit's second half of this finding - RPC.cs writing the trap id with Write(int) while
 *     RPCProcedure reads it with ReadByte() - is a wire-format mismatch, not an object leak. A
 *     one-sided fix on either end would desync against unmodified TOR clients reading/writing the
 *     other type, so per the task brief this half is reported only, never touched.
 *
 *  6) Garlic.clearGarlics() (Objects/Garlic.cs:44-46): identical shape again - `garlics = new
 *     List<Garlic>()`, no Destroy, called from the same resetVariables() as (3). Garlic is internal,
 *     so GarlicClearLeakPatch resolves it the same way as NinjaTrace. Destroying `garlic.garlic`
 *     (public field once reached by reflection) also destroys its `background` child (SetParent'd to
 *     it in the constructor), so a single Destroy per instance is enough.
 *
 *  7) Portal.clearPortals() (Objects/Portal.cs:157-164) nulls out `firstPortal`/`secondPortal`
 *     without ever destroying `portalGameObject` (or its child `portalFgAnimationGameObject`) on
 *     either one - the only two references to those objects vanish in the same statement that would
 *     be needed to reach them. Portal is a public class (unlike Garlic/NinjaTrace/Trap), so
 *     PortalClearLeakPatch is a plain Prefix with no reflection at all.
 *
 *  8) Tracker.DangerMeterParent / Tracker.Meter (TheOtherRoles.cs, Tracker.clearAndReload) are
 *     Destroyed but never set back to null. Every check that reads them afterwards
 *     (Objects/Arrow.cs:48 `if (Tracker.DangerMeterParent == null)`, and clearAndReload's own guard)
 *     uses Unity's overloaded null/bool operators, which correctly treat a destroyed object as null -
 *     so this was never an observable crash. It is still the exact "holds a stale wrapper around a
 *     destroyed native object longer than necessary" pattern this file exists to close, and the fix
 *     is a one-line Postfix (Tracker is public), so it is included for hygiene.
 *
 *  9) RoleManagerSelectRolesPatch.playerRoleMap (Patches/RoleAssignmentPatch.cs:50) is appended to by
 *     every setRoleToRandomPlayer() call (:531) - i.e. once per role assigned, every single round,
 *     forever, on the host (only the host ever runs RoleManager.SelectRoles/assignRoles). Its only
 *     consumer, setRolesAgain() (:648-665, itself a fully-drained `while` loop that would have kept it
 *     bounded), is never called - the one call site is commented out (:71, `//setRolesAgain();`). This
 *     is genuinely unbounded, dead-code-adjacent growth, not a false positive. Since the sole
 *     consumer is permanently inert, clearing the list can never discard data anything still needs.
 *     RoleAssignmentMapLeakPatch is a Postfix on RoleManager.SelectRoles itself (the public vanilla
 *     method both TOR's own patch and this one target) at Priority.Last, so it runs once TOR's own
 *     Postfix - which calls assignRoles() synchronously - has already added this round's entries, and
 *     clears them via one cached FieldInfo (RoleManagerSelectRolesPatch is internal).
 *
 * 10) RegionMenuOpenPatch.serverWarning (Patches/RegionMenuPatch.cs) is instantiated once
 *     (`if (serverWarning == null)`) and cached forever in a static field, unlike its two siblings
 *     ipField/portField in the very same method, which ARE parented to `__instance.transform`.
 *     serverWarning is created with no parent at all, so if RegionMenu is ever destroyed and rebuilt
 *     while the surrounding scene survives, the cached reference keeps pointing at a genuine orphan
 *     that the "already created" guard will never replace. RegionMenuServerWarningParentPatch is a
 *     Postfix on RegionMenu.Open, Priority.Last, that reparents the (reflectively read, since the
 *     field is private on an otherwise-public patch class) serverWarning under `__instance.transform`
 *     whenever it is not already there - from then on it shares RegionMenu's own lifecycle exactly
 *     like ipField/portField do, and a future rebuild of the menu correctly produces a fresh warning
 *     instead of adopting a stale one.
 *
 * 11) PlayerTabEnablePatch.Postfix (Modules/CustomColors.cs:216-238) does
 *     `chip.transform.localScale *= 0.76f` in PlayerTab.OnEnable, which fires every time the player
 *     re-opens the cosmetics/color tab, not just once - so the color chips shrink a little further
 *     every single time the tab is opened, eventually collapsing to nothing. ColorChipShrinkGuardPatch
 *     is a Postfix on the same PlayerTab.OnEnable at Priority.Last. Rather than tracking "have we seen
 *     this tab open before" (which breaks if the tab's own GameObject is ever recreated instead of
 *     just re-enabled), it inspects the actual scale after TOR's multiply just ran: a chip shrunk
 *     exactly once from a fresh 1x prefab lands at exactly 0.76x; anything smaller proves at least a
 *     second multiply already landed on an already-shrunk value, so it is snapped back to exactly
 *     0.76x. A hidden chip's scale is exactly 0 either way (0 * 0.76 stays 0) and is left alone.
 *
 * VERIFIED AS FEHLBEFUND (real fields, but already correctly scoped - not touched):
 *
 *  - TORMapOptions.gameMode: NOT reset by resetVariables(), but that is correct - it is a
 *    lobby-scoped setting (which game mode the host is running), analogous to any other persistent
 *    CustomOption, and resetting it every round would force every "Play Again" in a HideNSeek/
 *    PropHunt/Guesser lobby back to Classic. Its actual, correct reset point already exists:
 *    Patches/MainMenuPatch.cs:157-160 sets it back to Classic every time the "MatchMaking" scene
 *    loads (i.e. every time a player leaves a lobby back to the main menu) - exactly the
 *    lobby-scoped reset this kind of field needs, just not the round-scoped one resetVariables() is.
 *
 *  - Mini.timeOfMeetingStart: read once, at MeetingPatch.cs:243 (MeetingHud.VotingComplete), and it
 *    is unconditionally overwritten with DateTime.UtcNow at MeetingPatch.cs:685 every time a meeting
 *    STARTS, strictly before VotingComplete for that same meeting can ever fire. There is no path
 *    where a stale value from a previous round (or even a previous meeting) is ever observed.
 *
 *  - GameStartManagerPatch.timer: also not reset by resetVariables(), also correctly scoped
 *    elsewhere - GameStartManagerStartPatch.Postfix (Patches/GameStartManagerPatch.cs:33-37) resets
 *    it to 600 every time GameStartManager.Start fires, which is every time the lobby's ready-up
 *    screen is (re)entered. GameStartManager.Update (the only place that ticks it down) does not run
 *    during actual gameplay, so resetVariables() (which fires once gameplay begins) never needs to
 *    touch it - by the time it would matter again, Start() already has.
 *
 * CONFIRMED REAL, DELIBERATELY NOT PATCHED (no minimal-risk external hook exists):
 *
 *  - setTarget()'s many call sites (Patches/PlayerControlPatch.cs, e.g. :222/:226/:337/:341) build a
 *    fresh `new List<PlayerControl> { ... }` (sometimes with null entries) inline, every FixedUpdate,
 *    for whichever role is active. Each literal list is local to one of a dozen-plus large per-role
 *    dispatch methods with no separate hook point Harmony could intercept without replacing the whole
 *    surrounding method - a rebuild far more invasive than the (very small, 1-3 element) allocation
 *    it would save.
 *
 *  - PlayerPhysicsFixedUpdate.Postfix (PlayerControlPatch.cs:1451) computes
 *    `Invert.invert.FindAll(x => x.PlayerId == PlayerControl.LocalPlayer.PlayerId).Count > 0` every
 *    physics tick, allocating a list purely to check membership. It sits inside TOR's own Postfix on
 *    PlayerPhysics.FixedUpdate; HarmonyX runs every postfix for a method unconditionally (there is no
 *    "skip this other postfix" mechanism the way a prefix can skip the original), so the only way to
 *    prevent the allocation itself would be an IL transpiler rewriting TOR's method body - out of
 *    proportion to a few bytes of garbage at ~50 Hz.
 *
 *  - ninjaUpdate() and vultureUpdate() (PlayerControlPatch.cs:385, :716-724) both call
 *    `UnityEngine.Object.FindObjectsOfType<DeadBody>()` unconditionally every FixedUpdate while their
 *    role has a live target/is alive with arrows shown - a genuine scene-wide query every tick.
 *    trackerUpdate() in the very same file (:401-438) shows the correct fix: gate the same call
 *    behind an interval timer (Tracker.timeUntilUpdate). That fix cannot be transplanted from outside
 *    without either fully replacing both methods (they also drive visible Arrow updates, not just a
 *    boolean) or patching the shared UnityEngine.Object.FindObjectsOfType API globally, which would
 *    affect every mod and the base game alike - far too broad a blast radius for this file.
 *
 *  - ShipStatusPatch.Prefix on CalculateLightRadius (ShipStatusPatch.cs:77) ends with
 *    `Sunglasses.sunglasses.FindAll(x => x.PlayerId == player.PlayerId).Count > 0`, called for every
 *    player's light radius, likely every frame. Same shape as the two points above: buried inside one
 *    large existing Prefix with several early `return false` branches (the same method
 *    TorUpstreamFixes.LightRadiusFinalizerPatch already guards for a different defect, M-35) that
 *    would need a full, risky reimplementation to intercept mid-method from outside.
 *
 *  - CosmeticsCachePatches.GetHatPrefix (Modules/CustomHats/Patches/CosmeticsCachePatches.cs:13)
 *    unconditionally logs `TheOtherRolesPlugin.Logger.LogMessage($"trying to load hat {id}...")` on
 *    every CosmeticsCache.GetHat call. A second Harmony prefix cannot suppress a sibling prefix's own
 *    body (HarmonyX-Prefix-Fallstrick again: every prefix always runs, `return false` only skips the
 *    ORIGINAL), and the only way to intercept the LogMessage call itself would be to patch
 *    BepInEx.Logging.ManualLogSource.LogMessage - a type shared by every plugin's logger, which is a
 *    blast radius nowhere near "kleinstmoeglicher Eingriff" for one noisy line.
 *
 *  - Objects/CustomMessage.cs:27-36: the list-removal `customMessages.Remove(this)` is guarded by the
 *    same null-check as the Destroy call, so a CustomMessage whose `text.gameObject` is destroyed
 *    externally mid-animation is never removed from the static `customMessages` list. The bug lives
 *    entirely inside an anonymous `Action<float>` lambda passed to a coroutine (Effects.Lerp) - there
 *    is no named method boundary Harmony can attach to. Its only call site in the whole codebase
 *    (RPC.cs:893, the Trickster's "Lights are out" message) fires at most a handful of times per
 *    round, so the real-world cost of the leaked C# object is negligible even though the defect
 *    itself is real.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    public static class TorLeakFixes {

        private static float lastLogAt = -100f;
        private static void ThrottledLog(string tag, string message) {
            if (Time.realtimeSinceStartup - lastLogAt < 5f) return;
            lastLogAt = Time.realtimeSinceStartup;
            UsefulTORStuffPlugin.Logger?.LogWarning($"[TorLeakFixes/{tag}] {message}");
        }

        // ── 1) Vent-cleaning map icon must not shrink towards invisibility ─────────────────────
        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        static class VentCleanIconShrinkPatch {
            // The vent this player's active VentCleaning task targets, the last time we checked -
            // used only to tell "TOR just applied the FIRST shrink this session" (nothing to do)
            // from "TOR just multiplied an already-shrunk icon AGAIN" (undo the redundant factor).
            private static string shrunkIconKey;

            // PERF (this runs every physics tick while the map is open):
            //  - the task is found by walking myTasks in place, not ToArray()+FirstOrDefault (an
            //    Il2Cpp list copy plus a closure per tick);
            //  - FindConsoles() is a full scene scan - TOR already runs it once PER VENT per tick
            //    in the postfix this one follows - so our own copy is resolved once per task
            //    instance and step, and the key string with it;
            //  - the icon comes out of TOR's own mapIcons table (a managed dictionary, the same
            //    key TOR builds) instead of GameObject.Find, which walks the whole scene by name.
            //    The table is inside an internal TOR class, hence reflection, resolved once; a
            //    miss falls back to the old Find.
            private static IntPtr cachedTaskPtr;
            private static int cachedTaskStep = -1;
            private static string cachedTaskKey;
            private static readonly FieldInfo mapIconsField =
                AccessTools.Field(AccessTools.TypeByName("TheOtherRoles.Patches.MapBehaviourPatch"), "mapIcons");

            [HarmonyPriority(Priority.Last)] // after MapBehaviourPatch.Postfix, which does the shrinking
            public static void Postfix() {
                try {
                    var localPlayer = PlayerControl.LocalPlayer;
                    if (localPlayer == null || localPlayer.myTasks == null) { shrunkIconKey = null; return; }

                    PlayerTask task = null;
                    var tasks = localPlayer.myTasks;
                    for (int i = 0; i < tasks.Count; i++) {
                        var t = tasks[i];
                        if (t != null && t.TaskType == TaskTypes.VentCleaning) { task = t; break; }
                    }
                    if (task == null || task.IsComplete) { shrunkIconKey = null; return; }

                    string key;
                    int step = task.TaskStep;
                    if (cachedTaskKey != null && task.Pointer == cachedTaskPtr && step == cachedTaskStep) {
                        key = cachedTaskKey;
                    } else {
                        var consoles = task.FindConsoles();
                        if (consoles == null || consoles.Count == 0) { shrunkIconKey = null; return; }
                        key = $"vent {consoles[0].ConsoleId} icon";
                        cachedTaskPtr = task.Pointer;
                        cachedTaskStep = step;
                        cachedTaskKey = key;
                    }

                    if (key != shrunkIconKey) {
                        // Freshly became the shrink target (new task, or the map was just opened) -
                        // TOR's own multiply that just ran is the one intended shrink.
                        shrunkIconKey = key;
                        return;
                    }

                    GameObject icon = null;
                    if (mapIconsField?.GetValue(null) is Dictionary<string, GameObject> icons)
                        icons.TryGetValue(key, out icon);
                    if (icon == null) icon = GameObject.Find(key);
                    if (icon != null) icon.transform.localScale /= 0.6f;
                } catch (Exception e) {
                    ThrottledLog("VentIcon", $"shrink guard failed: {e.GetType().Name}: {e.Message}");
                    shrunkIconKey = null;
                }
            }
        }

        // ── 2) SecurityGuard security camera RenderTextures and Material clones must be freed ──
        [HarmonyPatch(typeof(SurveillanceMinigame), nameof(SurveillanceMinigame.OnDestroy))]
        static class SecurityCameraCleanupPatch {
            // Session-scoped table of Material clones tracked by SecurityCameraMaterialChurnPatch
            // below. Cleared here (and defensively in SecurityCameraBeginResetPatch), so a stale
            // reference from a dead minigame session is never compared against a live one.
            public static readonly Dictionary<int, Material> trackedMaterials = new();

            public static void Prefix(SurveillanceMinigame __instance) {
                try {
                    ReleaseExtraTextures(__instance);
                    foreach (var mat in trackedMaterials.Values)
                        if (mat != null) UnityEngine.Object.Destroy(mat);
                    trackedMaterials.Clear();
                } catch (Exception e) {
                    ThrottledLog("SecCam", $"OnDestroy cleanup failed: {e.GetType().Name}: {e.Message}");
                }
            }

            public static void ReleaseExtraTextures(SurveillanceMinigame instance) {
                if (instance?.textures == null) return;
                // Indices 0-3 are vanilla's own 4 camera slots; TOR only ever calls GetTemporary for
                // the SecurityGuard extension at index 4 and up (Begin, UsablesPatch.cs:513-524).
                for (int i = 4; i < instance.textures.Length; i++) {
                    var rt = instance.textures[i];
                    if (rt != null) RenderTexture.ReleaseTemporary(rt);
                }
            }
        }

        [HarmonyPatch(typeof(SurveillanceMinigame), nameof(SurveillanceMinigame.Begin))]
        static class SecurityCameraBeginResetPatch {
            // Defensive only: OnDestroy above already clears the tracking table at the end of every
            // normal session. This only guards the path where OnDestroy is skipped (e.g. the object
            // is torn down without a clean Unity destroy callback), so a leftover reference from a
            // dead session can never be mistaken for a live one.
            public static void Prefix() {
                try {
                    foreach (var mat in SecurityCameraCleanupPatch.trackedMaterials.Values)
                        if (mat != null) UnityEngine.Object.Destroy(mat);
                    SecurityCameraCleanupPatch.trackedMaterials.Clear();
                } catch (Exception e) {
                    ThrottledLog("SecCam", $"Begin reset failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(SurveillanceMinigame), nameof(SurveillanceMinigame.Update))]
        static class SecurityCameraMaterialChurnPatch {
            // Runs after TOR's own Prefix, which fully replaces vanilla Update and returns false - a
            // Postfix still runs regardless (HarmonyX only skips the ORIGINAL, never other patches).
            // We never touch ViewPorts[i].material ourselves (that getter is what clones) - only
            // sharedMaterial, which just reads the current reference without side effects.
            public static void Postfix(SurveillanceMinigame __instance) {
                try {
                    if (__instance?.ViewPorts == null) return;
                    var tracked = SecurityCameraCleanupPatch.trackedMaterials;
                    for (int i = 0; i < __instance.ViewPorts.Length; i++) {
                        var renderer = __instance.ViewPorts[i];
                        if (renderer == null) continue;
                        Material current = renderer.sharedMaterial;

                        if (tracked.TryGetValue(i, out var prevOwned) && prevOwned != null && !ReferenceEquals(prevOwned, current)) {
                            // TOR's page-turn logic reset sharedMaterial to the shared DefaultMaterial
                            // asset and immediately re-cloned via `.material` - the clone from the
                            // PREVIOUS page is referenced nowhere now and would otherwise sit until
                            // this whole minigame is destroyed.
                            UnityEngine.Object.Destroy(prevOwned);
                            tracked.Remove(i);
                        }

                        // Only track references that are genuine clones, never a shared asset, so a
                        // slot parked on StaticMaterial/DefaultMaterial is never mistaken for ours.
                        if (current != null && current != __instance.DefaultMaterial && current != __instance.StaticMaterial)
                            tracked[i] = current;
                    }
                } catch (Exception e) {
                    ThrottledLog("SecCam", $"material churn guard failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 3) JackInTheBox.clearJackInTheBoxes must destroy what it forgets ────────────────────
        [HarmonyPatch(typeof(JackInTheBox), nameof(JackInTheBox.clearJackInTheBoxes))]
        static class JackInTheBoxClearLeakPatch {
            private static readonly FieldInfo boxGameObjectField =
                AccessTools.Field(typeof(JackInTheBox), "gameObject");

            public static void Prefix() {
                try {
                    foreach (var box in JackInTheBox.AllJackInTheBoxes) {
                        if (box == null) continue;
                        try { if (box.vent != null) UnityEngine.Object.Destroy(box.vent.gameObject); }
                        catch (Exception e) { ThrottledLog("JackInBox", $"vent destroy failed: {e.Message}"); }
                        try {
                            var boxObj = boxGameObjectField?.GetValue(box) as GameObject;
                            if (boxObj != null) UnityEngine.Object.Destroy(boxObj);
                        } catch (Exception e) { ThrottledLog("JackInBox", $"box destroy failed: {e.Message}"); }
                    }
                } catch (Exception e) {
                    ThrottledLog("JackInBox", $"cleanup failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 4) NinjaTrace.clearTraces must destroy active traces, like Silhouette does ──────────
        [HarmonyPatch]
        static class NinjaTraceClearLeakPatch {
            private static Type ninjaTraceType;
            private static FieldInfo tracesField;   // public static List<NinjaTrace>
            private static FieldInfo traceGoField;  // private instance GameObject "trace"

            public static MethodBase TargetMethod() {
                ninjaTraceType ??= typeof(CustomOption).Assembly.GetType("TheOtherRoles.Objects.NinjaTrace");
                return ninjaTraceType?.GetMethod("clearTraces", BindingFlags.Public | BindingFlags.Static);
            }

            public static bool Prepare(MethodBase original) {
                ninjaTraceType ??= typeof(CustomOption).Assembly.GetType("TheOtherRoles.Objects.NinjaTrace");
                tracesField = ninjaTraceType?.GetField("traces", BindingFlags.Public | BindingFlags.Static);
                traceGoField = ninjaTraceType?.GetField("trace", BindingFlags.NonPublic | BindingFlags.Instance);
                if (tracesField == null || traceGoField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TorLeakFixes/NinjaTrace] traces/trace field not found - clearTraces leak guard disabled.");
                return TargetMethod() != null && tracesField != null && traceGoField != null;
            }

            public static void Prefix() {
                try {
                    if (tracesField.GetValue(null) is not System.Collections.IEnumerable list) return;
                    foreach (var traceObj in list) {
                        if (traceObj == null) continue;
                        var go = traceGoField.GetValue(traceObj) as GameObject;
                        if (go != null) UnityEngine.Object.Destroy(go);
                    }
                } catch (Exception e) {
                    ThrottledLog("NinjaTrace", $"cleanup failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 5) Trap.clearRevealedTraps must destroy the HUD arrow too (clearTraps already does) ─
        [HarmonyPatch]
        static class TrapRevealedArrowLeakPatch {
            private static Type trapType;
            private static FieldInfo trapArrowField;  // private instance Arrow "arrow"
            private static FieldInfo revealedField;   // public bool "revealed"
            private static FieldInfo trapsField;      // public static List<Trap> "traps"

            public static MethodBase TargetMethod() {
                trapType ??= typeof(CustomOption).Assembly.GetType("TheOtherRoles.Objects.Trap");
                return trapType?.GetMethod("clearRevealedTraps", BindingFlags.Public | BindingFlags.Static);
            }

            public static bool Prepare(MethodBase original) {
                trapType ??= typeof(CustomOption).Assembly.GetType("TheOtherRoles.Objects.Trap");
                trapArrowField = trapType?.GetField("arrow", BindingFlags.NonPublic | BindingFlags.Instance);
                revealedField = trapType?.GetField("revealed", BindingFlags.Public | BindingFlags.Instance);
                trapsField = trapType?.GetField("traps", BindingFlags.Public | BindingFlags.Static);
                if (trapArrowField == null || revealedField == null || trapsField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TorLeakFixes/Trap] arrow/revealed/traps field not found - revealed-trap arrow leak guard disabled.");
                return TargetMethod() != null && trapArrowField != null && revealedField != null && trapsField != null;
            }

            public static void Prefix() {
                try {
                    if (trapsField.GetValue(null) is not System.Collections.IEnumerable list) return;
                    foreach (var trap in list) {
                        if (trap == null) continue;
                        if (revealedField.GetValue(trap) is not bool revealed || !revealed) continue;
                        // Arrow is a public type (TheOtherRoles.Objects.Arrow); once the Trap instance
                        // is reached by reflection, its `arrow` field can be read directly.
                        var arrow = trapArrowField.GetValue(trap) as Arrow;
                        if (arrow?.arrow != null) UnityEngine.Object.Destroy(arrow.arrow);
                    }
                } catch (Exception e) {
                    ThrottledLog("Trap", $"revealed-trap arrow cleanup failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 6) Garlic.clearGarlics must destroy what it forgets ─────────────────────────────────
        [HarmonyPatch]
        static class GarlicClearLeakPatch {
            private static Type garlicType;
            private static FieldInfo garlicsField;   // public static List<Garlic>
            private static FieldInfo garlicGoField;  // public instance GameObject "garlic"

            public static MethodBase TargetMethod() {
                garlicType ??= typeof(CustomOption).Assembly.GetType("TheOtherRoles.Objects.Garlic");
                return garlicType?.GetMethod("clearGarlics", BindingFlags.Public | BindingFlags.Static);
            }

            public static bool Prepare(MethodBase original) {
                garlicType ??= typeof(CustomOption).Assembly.GetType("TheOtherRoles.Objects.Garlic");
                garlicsField = garlicType?.GetField("garlics", BindingFlags.Public | BindingFlags.Static);
                garlicGoField = garlicType?.GetField("garlic", BindingFlags.Public | BindingFlags.Instance);
                if (garlicsField == null || garlicGoField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TorLeakFixes/Garlic] garlics/garlic field not found - clearGarlics leak guard disabled.");
                return TargetMethod() != null && garlicsField != null && garlicGoField != null;
            }

            public static void Prefix() {
                try {
                    if (garlicsField.GetValue(null) is not System.Collections.IEnumerable list) return;
                    foreach (var g in list) {
                        if (g == null) continue;
                        // Destroying the "garlic" root also destroys its "background" child (SetParent
                        // in the constructor), so a single Destroy per instance is enough.
                        var go = garlicGoField.GetValue(g) as GameObject;
                        if (go != null) UnityEngine.Object.Destroy(go);
                    }
                } catch (Exception e) {
                    ThrottledLog("Garlic", $"cleanup failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 7) Portal.clearPortals must destroy the two portal GameObjects it drops ────────────
        [HarmonyPatch(typeof(Portal), nameof(Portal.clearPortals))]
        static class PortalClearLeakPatch {
            public static void Prefix() {
                try {
                    // portalFgAnimationGameObject is parented to portalGameObject in the constructor,
                    // so destroying the root takes the animation child with it.
                    if (Portal.firstPortal?.portalGameObject != null) UnityEngine.Object.Destroy(Portal.firstPortal.portalGameObject);
                    if (Portal.secondPortal?.portalGameObject != null) UnityEngine.Object.Destroy(Portal.secondPortal.portalGameObject);
                } catch (Exception e) {
                    ThrottledLog("Portal", $"cleanup failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 8) Tracker.DangerMeterParent/Meter should be nulled after TOR's own Destroy ────────
        [HarmonyPatch(typeof(Tracker), nameof(Tracker.clearAndReload))]
        static class TrackerDangerMeterNullOutPatch {
            public static void Postfix() {
                try {
                    Tracker.DangerMeterParent = null;
                    Tracker.Meter = null;
                } catch (Exception e) {
                    ThrottledLog("Tracker", $"DangerMeterParent null-out failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 9) RoleManagerSelectRolesPatch.playerRoleMap must not grow forever ─────────────────
        [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
        static class RoleAssignmentMapLeakPatch {
            private static FieldInfo playerRoleMapField;
            private static bool resolved;

            [HarmonyPriority(Priority.Last)] // after RoleManagerSelectRolesPatch.Postfix, which populates the map via assignRoles()
            public static void Postfix() {
                try {
                    if (!resolved) {
                        resolved = true;
                        var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.RoleManagerSelectRolesPatch");
                        playerRoleMapField = t?.GetField("playerRoleMap", BindingFlags.NonPublic | BindingFlags.Static);
                        if (playerRoleMapField == null)
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                "[TorLeakFixes/RoleMap] playerRoleMap field not found - leak guard disabled.");
                    }
                    if (playerRoleMapField?.GetValue(null) is System.Collections.IList list && list.Count > 0)
                        list.Clear();
                } catch (Exception e) {
                    ThrottledLog("RoleMap", $"playerRoleMap clear failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 10) RegionMenu's serverWarning must share the menu's own lifecycle ─────────────────
        [HarmonyPatch(typeof(RegionMenu), nameof(RegionMenu.Open))]
        static class RegionMenuServerWarningParentPatch {
            private static FieldInfo serverWarningField;
            private static bool resolved;

            [HarmonyPriority(Priority.Last)] // after RegionMenuOpenPatch.Postfix, which may just have created it
            public static void Postfix(RegionMenu __instance) {
                try {
                    if (__instance == null) return;
                    if (!resolved) {
                        resolved = true;
                        var t = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.RegionMenuOpenPatch");
                        serverWarningField = t?.GetField("serverWarning", BindingFlags.NonPublic | BindingFlags.Static);
                        if (serverWarningField == null)
                            UsefulTORStuffPlugin.Logger?.LogWarning(
                                "[TorLeakFixes/RegionMenu] serverWarning field not found - orphan guard disabled.");
                    }
                    var warning = serverWarningField?.GetValue(null) as GameObject;
                    if (warning == null) return; // not created yet, or already a genuine fake-null - nothing to adopt
                    if (warning.transform.parent != __instance.transform)
                        warning.transform.SetParent(__instance.transform, true);
                } catch (Exception e) {
                    ThrottledLog("RegionMenu", $"serverWarning reparent failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // ── 11) Color chips must not shrink further every time the cosmetics tab is opened ─────
        [HarmonyPatch(typeof(PlayerTab), nameof(PlayerTab.OnEnable))]
        static class ColorChipShrinkGuardPatch {
            private const float IntendedScale = 0.76f;
            private const float Epsilon = 0.01f;

            [HarmonyPriority(Priority.Last)] // after PlayerTabEnablePatch.Postfix, which just multiplied localScale by 0.76 again
            public static void Postfix(PlayerTab __instance) {
                try {
                    if (__instance?.ColorChips == null) return;
                    foreach (var chip in __instance.ColorChips.ToArray()) {
                        if (chip == null) continue;
                        var scale = chip.transform.localScale;
                        // A chip TOR just positioned (not hidden) lands at exactly 0.76x starting from
                        // a fresh 1x prefab scale. Anything smaller proves this OnEnable multiplied an
                        // already-shrunk scale again - snap it back to the one intended factor instead
                        // of letting further tab-opens compound it towards zero. A hidden chip's scale
                        // is exactly 0 either way (0 * 0.76 stays 0), so it never matches this check.
                        if (scale.x > Epsilon && scale.x < IntendedScale - Epsilon)
                            chip.transform.localScale = new Vector3(IntendedScale, IntendedScale, IntendedScale);
                    }
                } catch (Exception e) {
                    ThrottledLog("ColorChip", $"shrink guard failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }
    }
}
