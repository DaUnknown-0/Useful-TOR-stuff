// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ShieldPeaceGate - the NewcomerShield's and AntiStartKill's targeting gate now only closes for
 * attacks, never for peaceful abilities.
 *
 * THE PROBLEM THIS SOLVES
 * Both kill shields carry a setTarget gate (their "enforcement 0"): a protected player is dropped
 * from TOR's targeting helper, so no role's bespoke kill path can even acquire him. That gate is
 * the layer that demonstrably works in the field - but it knows nothing about WHY somebody is being
 * targeted. TOR funnels every role's targeting through the one helper
 * PlayerControlFixedUpdatePatch.setTarget, so shielding, shifting, sampling, tracking, handcuffing,
 * erasing, dousing and blanking all died with the kills. AntiStartKill's own header lists that as an
 * accepted cost; this file removes the cost instead of the protection.
 *
 * HOW THE CALLER IS IDENTIFIED
 * setTarget takes no "who is asking" argument and reading the call stack every physics tick is far
 * too expensive. So the PEACEFUL callers announce themselves: each of TOR's peaceful <role>SetTarget
 * methods gets a prefix that raises a depth counter and a FINALIZER that lowers it again (a
 * finalizer runs even when the original throws, so the counter can never stick). While the counter
 * is up, both shields skip their gate.
 *
 * WHY THE PEACEFUL SIDE IS THE ONE THAT IS MARKED
 * The list is deliberately inverted against the obvious one: marking the KILL callers would leave
 * every role nobody thought about (a new TOR role, a new Unknown's Collection ability) unprotected
 * by default. Marking the peaceful ones fails toward protection - an unknown caller stays gated,
 * and a TOR rename simply restores today's behaviour instead of opening a hole.
 *
 * WHAT IS NOT ON THE LIST, ON PURPOSE
 * The impostor kill button, Sheriff, Vampire, Jackal, Sidekick, Warlock, Witch, Ninja and Thief
 * targeting stay gated, as does the Maniac's bomb (planting a bomb IS the attack, the explosion is
 * just its delay). The Medium's seance and the Security Guard's vent work never target a living
 * player at all, so neither ever reached the gate.
 *
 * OTHER MODS
 * Unknown's Collection is a separate plugin with no compile-time reference to this one, so it opens
 * the same window through an AppDomain contract instead of a Harmony patch on its internals:
 *
 *   AppDomain "UTS.Shield.SetPeaceful"     -> Action<bool>    open/close the peaceful window
 *   AppDomain "UTS.Shield.IsKillProtected" -> Func<byte,bool> does this player hold a UTS kill shield?
 *
 * The second key exists for kills that never pass a targeting helper at all - the Maniac's blast
 * reads it the way it already reads the Medic and Time Master shields. Both keys are set once at
 * load; a mod that finds them missing simply behaves as if this mod were not installed.
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles.Patches;

namespace UsefulTORStuff {

    public static class ShieldPeaceGate {

        // AppDomain contract keys - see the header. Public so the sibling mods can be pointed at the
        // literal strings in a review without guessing them.
        public const string AppKeySetPeaceful = "UTS.Shield.SetPeaceful";
        public const string AppKeyIsProtected = "UTS.Shield.IsKillProtected";

        // Depth, not a bool: the peaceful methods do not nest today, but a counter costs nothing and
        // means a future nested call can never close the window early for its caller.
        private static int depth;

        // The one question both shields ask before they close their gate.
        public static bool Peaceful => depth > 0;

        public static void Open() { depth++; }

        public static void Close() { if (depth > 0) depth--; }

        // Belt and braces for the RPC-driven round reset: a stuck counter would silently disable both
        // gates for the rest of the session, which is the one failure mode worth an explicit reset.
        public static void ResetDepth() { depth = 0; }

        // True while the player holds one of THIS mod's two kill shields. Read by Unknown's Collection
        // through the AppDomain contract; the sibling shields (Medic, Time Master, first kill, Mini)
        // are TOR's own and are checked by the caller directly.
        public static bool IsKillProtected(byte playerId) {
            try {
                return NewcomerShield.IsShielded(playerId) || AntiStartKill.IsProtected(playerId);
            } catch { return false; }
        }

        // Called once from the plugin's Load(). Registering delegates (not raw state) keeps the
        // answer live: a mod that cached the delegate at its own load time still sees today's shields.
        public static void RegisterContract() {
            try {
                AppDomain.CurrentDomain.SetData(AppKeySetPeaceful,
                    (Action<bool>)(on => { if (on) Open(); else Close(); }));
                AppDomain.CurrentDomain.SetData(AppKeyIsProtected,
                    (Func<byte, bool>)IsKillProtected);
                UsefulTORStuffPlugin.Logger?.LogInfo("[ShieldPeaceGate] AppDomain contract registered.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ShieldPeaceGate] contract registration failed: {e}");
            }
        }

        // ====================================================================
        // The peaceful callers.
        //
        // All of them are PRIVATE static methods on a PUBLIC class, so they are patched by name
        // rather than by nameof(). A name that no longer resolves is logged once at load and leaves
        // that single ability gated - the fail-safe direction (see the header).
        //
        // arsonistSetTarget is public in TOR today, the rest are private; TargetMethod() looks both
        // up the same way so a future visibility change on either side changes nothing here.
        // ====================================================================

        private static readonly string[] PeacefulMethods = {
            "medicSetTarget",      // placing the Medic shield
            "shifterSetTarget",    // swapping roles
            "morphlingSetTarget",  // taking a sample
            "deputySetTarget",     // handcuffs
            "trackerSetTarget",    // attaching the tracker
            "eraserSetTarget",     // erasing a role
            "arsonistSetTarget",   // dousing
            "pursuerSetTarget"     // handing out a blank
        };

        // Manual patching instead of eight near-identical attribute classes: the shared prefix and
        // finalizer below are the entire payload, and the list above then reads as the feature spec.
        public static void TryPatch(Harmony harmony) {
            try {
                var owner = typeof(PlayerControlFixedUpdatePatch);
                var prefix = new HarmonyMethod(typeof(ShieldPeaceGate)
                    .GetMethod(nameof(PeacefulPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                var finalizer = new HarmonyMethod(typeof(ShieldPeaceGate)
                    .GetMethod(nameof(PeacefulFinalizer), BindingFlags.NonPublic | BindingFlags.Static));

                int patched = 0;
                foreach (string name in PeacefulMethods) {
                    MethodInfo m = AccessTools.Method(owner, name);
                    if (m == null) {
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            $"[ShieldPeaceGate] {name} not found - that ability stays blocked by the shields.");
                        continue;
                    }
                    harmony.Patch(m, prefix: prefix, finalizer: finalizer);
                    patched++;
                }
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[ShieldPeaceGate] {patched}/{PeacefulMethods.Length} peaceful targeting paths freed.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ShieldPeaceGate] TryPatch failed: {e}");
            }
        }

        private static void PeacefulPrefix() => Open();

        // Finalizer, not postfix: it runs even when the original (or another mod's prefix) throws,
        // so an exception in TOR's targeting can never leave the window stuck open.
        private static void PeacefulFinalizer() => Close();

        // The round reset is the natural place to drop any leftover depth. It also runs while the
        // window is provably closed (no targeting happens during a round setup).
        [HarmonyPatch(typeof(TheOtherRoles.RPCProcedure), nameof(TheOtherRoles.RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => ResetDepth();
        }
    }
}
