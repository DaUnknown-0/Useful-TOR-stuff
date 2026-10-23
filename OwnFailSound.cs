// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * OwnFailSound - an own, synthetic "fail" sound instead of TOR's recording.
 *
 * TOR plays its "fail" clip whenever something is refused: a kill on the Medic's shield
 * (Helpers.checkMuderAttempt), a Guesser shot the options do not allow, a refused option change.
 * The clip lives in TOR's sound bundle under "assets/audio/fail.ogg" and every play() goes through
 * SoundEffectsManager.get(), which looks the clip up in the private static dictionary soundEffects.
 * This file swaps that dictionary entry for the embedded clip uts_fail.raw and puts TOR's own clip
 * back when the feature is off. A data swap, no patch on any TOR method, so it cannot get lost to
 * tiered compilation and keeps TOR's stop()/stopAll() working.
 *
 * The clip is generated from scratch (TOR Mod\Assets\FailSound\gen_fail_sound.py: nasal hum, a sharp
 * burst, a low puff of breath, a creaky tail), shaped after the original without using any recording.
 *
 * Two switches, both default on (User 2026-10-23): the host's option 1398 "Own Fail Sound" (synced like
 * every TOR option) and the local switch [Sounds] OwnFailSound, also on the Unknown's Collection page of
 * TOR's Mod Options next to the VIP scream. Purely cosmetic, nothing is sent.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class OwnFailSound {
        private const string Tag = "[OwnFailSound]";
        private const string FailKey = "assets/audio/fail.ogg";

        public static CustomOption HostOption;
        public static ConfigEntry<bool> Enabled;

        private static Dictionary<string, AudioClip> table;        // TOR's SoundEffectsManager.soundEffects
        private static AudioClip original;                         // TOR's own clip, kept for switching back
        private static bool applied, tableTried, warned;

        public static void Bind(ConfigFile config) {
            Enabled = config.Bind("Sounds", "OwnFailSound", true,
                "Replace TOR's \"fail\" sound (kill on a shield, refused Guesser shot, refused option) with the mod's "
                + "own synthetic one. Only affects your own game; the host's option decides whether the lobby uses it at all.");
            ExternalToggles.Register(() => UTSLocalization.Tr("uts.options.failsound_toggle"),
                () => Enabled.Value, v => Enabled.Value = v);
        }

        public static void CreateOptions() {
            try {
                HostOption = CustomOption.Create(1398, Types.General, "Own Fail Sound", true, null, true);
                UTSLocalization.BindOptionTitle(HostOption, "uts.failsound.option_name");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"{Tag} CreateOptions failed: {e}");
            }
        }

        private static bool Wanted {
            get {
                if (Enabled != null && !Enabled.Value) return false;
                try { return HostOption == null || HostOption.getBool(); } catch { return true; }
            }
        }

        private static Dictionary<string, AudioClip> Table() {
            if (table != null || tableTried) return table;
            tableTried = true;
            try {
                var field = typeof(SoundEffectsManager).GetField("soundEffects", BindingFlags.NonPublic | BindingFlags.Static);
                table = field?.GetValue(null) as Dictionary<string, AudioClip>;
                if (table == null) UsefulTORStuffPlugin.Logger?.LogWarning($"{Tag} TOR's sound table not found, feature off");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"{Tag} sound table: {e.Message}");
            }
            return table;
        }

        /// <summary>Puts the wanted clip into TOR's table (ours or TOR's own); cheap when nothing changed.</summary>
        internal static void Apply() {
            bool want = Wanted;
            if (want == applied) return;
            var t = Table();
            if (t == null) return;
            if (want) {
                var clip = UTSAssets.FailClip;
                if (clip == null) {
                    if (!warned) { warned = true; UsefulTORStuffPlugin.Logger?.LogWarning($"{Tag} clip missing, keeping TOR's sound"); }
                    return;
                }
                if (original == null && t.TryGetValue(FailKey, out var own)) original = own;
                t[FailKey] = clip;
                applied = true;
                UsefulTORStuffPlugin.Logger?.LogInfo($"{Tag} own clip is the fail sound now");
            } else {
                if (original != null) t[FailKey] = original;
                applied = false;
                UsefulTORStuffPlugin.Logger?.LogInfo($"{Tag} TOR's fail sound is back");
            }
        }

        // The table is filled in TOR's plugin Load; HudManager exists in the lobby and in every round,
        // which is everywhere the fail sound can play. A game method, so no detour on TOR code.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try { Apply(); } catch (Exception e) { if (!warned) { warned = true; UsefulTORStuffPlugin.Logger?.LogWarning($"{Tag} {e.Message}"); } }
            }
        }
    }
}
