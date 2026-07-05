// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SoundAtPositionFix - positional TOR sounds corrupt the vanilla SoundManager and break the UI.
 *
 * TOR's SoundEffectsManager.playAtPosition() plays through SoundManager.PlaySound() but tears the
 * sound down with source.Destroy() - destroying an AudioSource the SOUND MANAGER owns and keeps in
 * its bookkeeping. From then on every SoundManager sweep touching that entry throws
 * NullReferenceException: ShipStatus.OnDestroy -> SoundManager.StopAllSound() crashes at round end,
 * and the destroyed entry keeps throwing afterwards - the visible symptom is a dead end-game /
 * lobby UI ("Play Again" / "Leave" do nothing, their click path goes through SoundManager sounds).
 * Trigger in practice: the Bomber's bomb (fuse loop + explosion both run through playAtPosition).
 *
 * Fix: replace playAtPosition with a faithful copy whose teardown uses the vanilla-managed
 * SoundManager.StopSound(clip) instead of destroying the shared source (same call the rest of the
 * modpack already uses for loop teardown). Bool prefix on TOR's own method - TOR does not patch
 * its own SoundEffectsManager, so there is no patch-order hazard; on any unexpected error the
 * original runs (TOR behavior, at worst the old bug).
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    [HarmonyPatch]
    public static class SoundAtPositionFix {
        // TORMapOptions is internal to TOR - read its enableSoundEffects flag via reflection.
        private static FieldInfo enableSfxField;
        private static bool sfxFieldResolved;
        private static bool EnableSoundEffects() {
            try {
                if (!sfxFieldResolved) {
                    sfxFieldResolved = true;
                    var t = typeof(SoundEffectsManager).Assembly.GetType("TheOtherRoles.TORMapOptions");
                    enableSfxField = t?.GetField("enableSoundEffects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                return enableSfxField == null || (bool)enableSfxField.GetValue(null);
            } catch { return true; }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SoundEffectsManager), nameof(SoundEffectsManager.playAtPosition))]
        public static bool PlayAtPositionPrefix(string path, Vector2 position, float maxDuration, float range, bool loop) {
            try {
                if (!EnableSoundEffects() || !Constants.ShouldPlaySfx()) return false;
                AudioClip clip = SoundEffectsManager.get(path);
                if (clip == null || SoundManager.Instance == null || HudManager.Instance == null) return false;

                AudioSource source = SoundManager.Instance.PlaySound(clip, false, 1f);
                if (source == null) return false;
                source.loop = loop;

                HudManager.Instance.StartCoroutine(Effects.Lerp(maxDuration, new Action<float>((p) => {
                    try {
                        if (source == null) return;
                        if (p == 1f) {
                            // Vanilla-managed teardown (keeps SoundManager's bookkeeping intact) -
                            // the original destroyed the shared source here and corrupted it.
                            if (source.isPlaying) SoundManager.Instance?.StopSound(clip);
                            return;
                        }
                        var local = PlayerControl.LocalPlayer;
                        if (local == null) { source.volume = 0f; return; }
                        float distance = Vector2.Distance(position, local.GetTruePosition());
                        source.volume = distance < range ? 1f - distance / range : 0f;
                    } catch { }
                })));
                return false;   // original replaced
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SoundAtPositionFix] fell back to TOR original: {e.Message}");
                return true;
            }
        }
    }
}
