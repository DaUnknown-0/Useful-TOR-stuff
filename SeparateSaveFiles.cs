// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * SeparateSaveFiles - the modded game keeps its own settings.amogus and player.amogus.
 *
 * Among Us stores the vanilla game settings (host options, graphics, sound, language, controls) in
 * settings.amogus and the player (name, colour, worn cosmetics) in player.amogus, both in
 * AppData\LocalLow\Innersloth\Among Us. A modded and a plain Among Us on the same computer shared
 * those files, so the plain game picked up modded host settings and the other way round.
 * Town of Us solves the same thing with settings.amogus_TOU / player.amogus_TOU; this does the
 * same with "_TOR".
 *
 * How: SettingsData.FileName and PlayerData.FileName return the file name the save code uses
 * ("settings.amogus", "player.amogus"); a postfix appends "_TOR". The first time a name is handed
 * out and the _TOR file does not exist yet, the vanilla file is copied once, so name, cosmetics and
 * settings carry over instead of falling back to the defaults. From then on both live apart.
 * playerStats3 (the statistics) stays shared on purpose.
 *
 * Config "SaveFiles / Separate" (on by default). Switching it off returns to the vanilla files; the
 * _TOR files stay where they are for the next time.
 */

using System;
using System.Collections.Generic;
using System.IO;
using AmongUs.Data.Player;
using AmongUs.Data.Settings;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public static class SeparateSaveFiles {
        public const string Suffix = "_TOR";
        public static ConfigEntry<bool> Enabled;

        private static readonly HashSet<string> prepared = new HashSet<string>();

        public static void Bind(ConfigFile config) {
            Enabled = config.Bind("SaveFiles", "Separate", true,
                "Keep this modded game's settings.amogus and player.amogus apart from the plain game " +
                "(files settings.amogus_TOR / player.amogus_TOR). The first start copies the plain files once.");
        }

        /*
         * The game loads settings.amogus very early, before BepInEx runs this plugin (Player.log of
         * 2026-10-07: "loaded data from JSON file 'settings.amogus'", player.amogus_TOR only later).
         * The patch alone then only redirected the SAVING, and every start read the plain settings
         * again. So once the patch is in place, the settings are loaded a second time; that load
         * asks for the file name and gets settings.amogus_TOR (copied from the plain file the first
         * time). The player data loads later and needs no help.
         */
        public static void ReloadIfLoadedEarly() {
            if (Enabled == null || !Enabled.Value) return;
            try {
                if (!AmongUs.Data.DataManager.IsSettingsLoaded) return;
                AmongUs.Data.DataManager.Settings.ForceLoad();
                float music = -1f;
                try { music = AmongUs.Data.DataManager.Settings.Audio.MusicVolume; } catch { }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[SaveFiles] settings had been read before the patch; reloaded from settings.amogus_TOR (music volume {music:0.####})");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SaveFiles] reloading the settings failed: {e.Message}");
            }
        }

        private static string Redirect(string name) {
            if (string.IsNullOrEmpty(name) || Enabled == null || !Enabled.Value) return name;
            if (name.EndsWith(Suffix, StringComparison.Ordinal)) return name;
            string own = name + Suffix;
            if (prepared.Add(name)) CopyOnce(name, own);
            return own;
        }

        // the vanilla file -> the _TOR file, only when the _TOR file does not exist yet
        private static void CopyOnce(string vanilla, string own) {
            try {
                string dir = Application.persistentDataPath;
                string from = Path.Combine(dir, vanilla), to = Path.Combine(dir, own);
                if (File.Exists(to)) {
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SaveFiles] using {own}");
                    return;
                }
                if (File.Exists(from)) {
                    File.Copy(from, to);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SaveFiles] first start: copied {vanilla} -> {own}");
                } else {
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SaveFiles] {own} starts fresh (no {vanilla} to copy)");
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SaveFiles] copy {vanilla} -> {own} failed: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(SettingsData), nameof(SettingsData.FileName), MethodType.Getter)]
        private static class SettingsNamePatch {
            private static void Postfix(ref string __result) => __result = Redirect(__result);
        }

        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.FileName), MethodType.Getter)]
        private static class PlayerNamePatch {
            private static void Postfix(ref string __result) => __result = Redirect(__result);
        }
    }
}
