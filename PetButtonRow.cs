// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * PetButtonRow - the pet button gets a slot of its own in the third row, above the use / kill column (User 2026-10-25).
 *
 * Vanilla swaps the use button for the pet button whenever there is nothing to use (HudManager.ToggleUseAndPetButton,
 * every physics step): with a pet the use button vanished, and mods that offer their own use actions had to swap it
 * back. Now the pet button sits at TOR's highRowRight (two rows above the use button) and stays visible while the local
 * player is alive and has a pet; the use button keeps its place and is greyed out when there is no target, as without a
 * pet. Both hidden (meeting, minigame, intro) is left alone.
 */

using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    internal static class PetButtonRow {
        private static readonly Vector3 ThirdRow = new Vector3(0f, 2.06f, 0f);   // TOR CustomButton.ButtonPositions.highRowRight

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(HudManager __instance) {
            try {
                var pet = __instance.PetButton;
                var use = __instance.UseButton;
                if (pet == null || use == null) return;
                if (!use.gameObject.activeSelf && !pet.gameObject.activeSelf) return;     // HUD hidden: leave it
                var lp = PlayerControl.LocalPlayer;
                bool hasPet = lp != null && lp.Data != null && !lp.Data.IsDead && lp.cosmetics != null
                              && lp.cosmetics.currentPet != null && lp.Data.DefaultOutfit != null
                              && !string.IsNullOrEmpty(lp.Data.DefaultOutfit.PetId) && lp.Data.DefaultOutfit.PetId != "pet_EmptyPet";
                if (!hasPet) return;
                pet.transform.localPosition = use.transform.localPosition + ThirdRow;
                if (!pet.gameObject.activeSelf) pet.gameObject.SetActive(true);
                if (!use.gameObject.activeSelf) {
                    use.gameObject.SetActive(true);
                    if (use.currentTarget == null) use.SetDisabled();
                }
                // Vanilla only switches the use button on (target + canInteract) when it swaps it in for the pet button
                // itself. Since we keep it visible, that swap never happens: the button showed a target but clicks and
                // the use key did nothing until the player had petted once (User 2026-10-28). Mirror the player's
                // closest usable onto the button; with no target leave it alone (Atlas offers its own actions then).
                var closest = lp.closest;
                if (closest != null && lp.CanMove) {
                    bool stale = use.currentTarget == null || use.currentTarget.Pointer != closest.Pointer;
                    bool locked = !use.canInteract;
                    if (stale) use.SetTarget(closest);
                    if (!use.canInteract) { use.canInteract = true; use.SetEnabled(); }
                    if (stale || locked) Note($"use button repaired (stale target {stale}, locked {locked})");
                }
                if (!pet.canInteract && lp.CanPet()) { pet.canInteract = true; Note("pet button unlocked"); }
            } catch { }
        }

        private static int _notes;

        // Only the first few repairs per session go to the log: enough to see in a player's log that it happened.
        private static void Note(string what) {
            if (_notes >= 5) return;
            _notes++;
            UsefulTORStuffPlugin.Logger?.LogInfo("[UTS/PetRow] " + what);
        }
    }
}
