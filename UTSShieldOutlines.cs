// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * UTSShieldOutlines - THE one shield painter, with a colour cycle for stacked shields.
 *
 * A player can hold several kill shields at once: the Medic's (cyan), TOR's "shield last game
 * first kill" (blue), the NewcomerShield (gold) and the AntiStartKill spawn protection (green) -
 * plus the Armored hint TOR shows to ghosts (yellow). The body sprite has exactly ONE outline
 * slot, so before this file whoever painted last simply won and the other shields were invisible.
 * Now every player's visible shields are collected into a list and, when there is more than one,
 * the outline STEPS through the colours (one every half second) - the flicker sequence IS the
 * shield inventory, readable at a glance.
 *
 * WHY THIS OVERRIDES TOR INSTEAD OF PATCHING IT
 * TOR's setBasePlayerOutlines runs every PHYSICS tick (PlayerControl.FixedUpdate) and writes
 * _Outline for everyone - painting from FixedUpdate is a lost race (the NewcomerShield autopsy,
 * playtest 2026-08-15). Unity runs all FixedUpdates before Update, so a HudManager.Update postfix
 * at Priority.Low always lands AFTER TOR's wipe-and-paint. For a single TOR-only shield we repaint
 * exactly the colour TOR just painted (a visual no-op); the moment a second shield exists, the
 * cycle takes the slot.
 *
 * WHAT COUNTS AS VISIBLE - deliberately TOR's own rules, not ours:
 *  - Medic:      Medic.shieldVisible(target), TOR's public helper - it already encodes the
 *                "Show Shielded Player" option, ghost info and the morph mapping.
 *  - First kill: everyone sees it, like TOR's base painter.
 *  - Armored:    ghosts only, unbroken armour - TOR's rule. TOR shows it only when no other
 *                shield is visible; here it joins the cycle instead, which is strictly more
 *                information for the ghost.
 *  - Newcomer / AntiStartKill: everyone. Both lists are public knowledge (announced at round
 *                start), so showing them leaks nothing.
 * During a Camouflager camo or the mushroom sabotage NOTHING is painted - all players look
 * identical, and any outline would single one of them out. TOR hides its shields there for
 * exactly that reason (and the old NewcomerShield painter got this wrong).
 *
 * The morph mapping follows TOR: shields belong to the DISPLAYED identity. A Morphling morphed as
 * a shielded player wears that player's outline; the real player's own outline hides while
 * somebody else wears their face (Medic.shieldVisible does this internally; the same displayed-id
 * is used for the other shields so the cycle can never expose the Morphling).
 *
 * We only ever clear an outline we painted ourselves - TOR owns the rest and repaints its own
 * every physics tick anyway.
 */

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;

namespace UsefulTORStuff {

    public static class UTSShieldOutlines {

        // One colour per half second. Slow enough to read, fast enough that a full four-shield
        // cycle fits into two seconds.
        private const float CyclePeriod = 0.5f;

        // Players whose outline WE set, so it can be taken back off when the shields end.
        private static readonly HashSet<byte> painted = new HashSet<byte>();

        // Scratch list, reused every frame for every player - this runs per frame, so no garbage.
        private static readonly List<Color> colors = new List<Color>(5);

        // TORMapOptions is INTERNAL to TOR, so the first-kill shield state comes via reflection
        // (fields are public static inside the internal class). Resolved once, read once per frame.
        private static System.Reflection.FieldInfo fiShieldFirstKill, fiFirstKillPlayer;
        private static bool mapOptionsResolved;

        // The player currently under TOR's "Shield Last Game First Kill", or null.
        private static PlayerControl FirstKillShielded() {
            try {
                if (!mapOptionsResolved) {
                    mapOptionsResolved = true;
                    var t = typeof(Medic).Assembly.GetType("TheOtherRoles.TORMapOptions");
                    const System.Reflection.BindingFlags flags =
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                    fiShieldFirstKill = t?.GetField("shieldFirstKill", flags);
                    fiFirstKillPlayer = t?.GetField("firstKillPlayer", flags);
                    if (fiShieldFirstKill == null || fiFirstKillPlayer == null)
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[ShieldOutlines] TORMapOptions fields not found - the first-kill "
                            + "shield stays TOR-painted (solid blue, no cycle).");
                }
                if (fiShieldFirstKill == null || fiFirstKillPlayer == null) return null;
                if (!(bool)fiShieldFirstKill.GetValue(null)) return null;
                return fiFirstKillPlayer.GetValue(null) as PlayerControl;
            } catch { return null; }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        [HarmonyPriority(Priority.Low)]   // after TOR's own HudManager patches as well
        static class OutlinePatch {
            public static void Postfix() => Tick();
        }

        private static void Tick() {
            try {
                var local = PlayerControl.LocalPlayer;
                if (local == null) return;

                // Cheap early-out: nothing shielded anywhere and nothing of ours left on screen.
                PlayerControl firstKill = FirstKillShielded();
                bool anyShield = NewcomerShield.Active || AntiStartKill.Active
                    || Medic.shielded != null || firstKill != null
                    || Armored.armored != null;
                if (!anyShield && painted.Count == 0) return;

                bool hidden = Camouflager.camouflageTimer > 0f || Helpers.MushroomSabotageActive();

                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (p == null) continue;
                    var sprite = p.cosmetics?.currentBodySprite?.BodySprite;
                    if (sprite == null || sprite.material == null) continue;
                    byte id = p.PlayerId;

                    if (hidden) { painted.Remove(id); continue; }   // TOR wipes to 0 itself

                    CollectColors(p, local, firstKill);
                    if (colors.Count == 0) {
                        // Ours to clear, and only ours - TOR repaints its own next physics tick.
                        if (painted.Remove(id)) sprite.material.SetFloat("_Outline", 0f);
                        continue;
                    }

                    int idx = colors.Count == 1 ? 0
                        : (int)(Time.time / CyclePeriod) % colors.Count;
                    sprite.material.SetFloat("_Outline", 1f);
                    sprite.material.SetColor("_OutlineColor", colors[idx]);
                    painted.Add(id);
                }
            } catch { }
        }

        // Fills `colors` with every shield visible on `target` for the local player, in a FIXED
        // order (cyan, blue, yellow, gold, green) so the cycle sequence is stable.
        private static void CollectColors(PlayerControl target, PlayerControl local, PlayerControl firstKill) {
            colors.Clear();
            if (target.Data == null || target.Data.IsDead || target.Data.Disconnected) return;

            // The displayed identity (TOR's morph rule, see the header).
            bool isMorphedMorphling = target == Morphling.morphling
                && Morphling.morphTarget != null && Morphling.morphTimer > 0f;
            // Both the real player AND a Morphling wearing their face show the shields - exactly
            // the mapping Medic.shieldVisible applies to the Medic shield.
            byte displayedId = isMorphedMorphling ? Morphling.morphTarget.PlayerId : target.PlayerId;

            if (Medic.shieldVisible(target)) colors.Add(Medic.shieldedColor);

            if (firstKill != null && firstKill.PlayerId == displayedId)
                colors.Add(Color.blue);

            // Armored stays on the REAL player (TOR paints it that way too - it is a ghost hint,
            // not something the Morphling's disguise should relocate).
            if (local.Data != null && local.Data.IsDead
                && Armored.armored != null && Armored.armored == target && !Armored.isBrokenArmor)
                colors.Add(Color.yellow);

            if (NewcomerShield.IsShielded(displayedId)) colors.Add(NewcomerShield.ShieldColor);

            if (AntiStartKill.IsProtected(displayedId)) colors.Add(AntiStartKill.ShieldColor);
        }

        // PlayerIds are per connection; never carry paint bookkeeping into another lobby.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() => painted.Clear();
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => painted.Clear();
        }
    }
}
