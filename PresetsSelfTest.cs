// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * PresetsSelfTest - autotest only (config "Diagnostics / Presets Test", off by default): settings presets, the
 * music tracks of other plugins and the titles of other plugins, in freeplay on The Skeld.
 *
 *  1. Presets: saves "Diag Base" with every option, changes 60 options and the kill cooldown, saves three more,
 *     opens the panel (shot), loads "Diag Base" and checks every option and the cooldown against it (shot), renames
 *     and deletes one, then deletes the test presets again.
 *  2. Music: every tab of the music panel on its last page, where the tracks of other plugins stand (shots), one of
 *     them through the decoder onto the music channel, and a track with the "tempo" flag on the reactor tab with a
 *     simulated countdown (the pitch has to rise).
 *  3. Titles: the session statistics with sample numbers plus whatever other plugins counted (shot of the titles).
 * Shots land in <game>/UTSShots/presets_*.png; "[PresetsTest] all done" ends the run, then the game quits.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using BepInEx.Configuration;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {

    internal static class PresetsSelfTest {
        internal static ConfigEntry<bool> Enabled;
        private static bool menuFired, gameFired;

        internal static void Bind(ConfigFile config) {
            Enabled = config.Bind("Diagnostics", "Presets Test", false,
                "Autotest only: in freeplay, saves/loads/renames/deletes settings presets, shows the music tracks and titles of "
                + "other plugins, saves screenshots to UTSShots and quits.");
        }

        private static bool On => Enabled != null && Enabled.Value;
        internal static bool Running { get; private set; }
        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[PresetsTest] " + s);

        private static IEnumerator Shot(string tag) {
            yield return new WaitForEndOfFrame();
            try {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UTSShots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"presets_{tag}.png");
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(tex));
                UnityEngine.Object.Destroy(tex);
                Log($"shot {tag} -> {file}");
            } catch (Exception e) { Log($"shot {tag} failed: {e.Message}"); }
        }

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class MenuPatch {
            private static void Postfix(MainMenuManager __instance) {
                if (menuFired || !On || UIGallery.Active) return;
                menuFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, MenuRun());
            }
        }

        private static IEnumerator MenuRun() {
            float t0 = Time.time;
            while (true) {
                bool done = false;
                try { done = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (done || Time.time - t0 > 45f) break;
                yield return null;
            }
            yield return new WaitForSeconds(2f);
            try {
                var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                if (popover == null) { Log("no FreeplayPopover"); yield break; }
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap(MapNames.Skeld);
                Log("freeplay started");
            } catch (Exception e) { Log("freeplay start failed: " + e.Message); }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class GamePatch {
            private static void Postfix(HudManager __instance) {
                if (gameFired || !On) return;
                var ac = AmongUsClient.Instance;
                if (ac == null || ac.NetworkMode != NetworkModes.FreePlay || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
                gameFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, GameRun());
            }
        }

        private static IGameOptions Opts() => GameManager.Instance?.LogicOptions?.currentGameOptions;

        private static IEnumerator GameRun() {
            yield return new WaitForSeconds(6f);
            Running = true;
            Log("begin");
            yield return PresetsPart();
            yield return MusicPart();
            yield return TitlesPart();
            Log("all done");
            yield return new WaitForSeconds(1f);
            Application.Quit();
        }

        // ---------------------------------------------------------------- presets
        private static IEnumerator PresetsPart() {
            SettingsPresetsUI.DiagActive = true;
            foreach (var old in SettingsPresets.List().Where(p => p.Name.StartsWith("Diag ")).ToList()) SettingsPresets.Delete(old);

            var opts = CustomOption.options.Where(o => o != null && o.id != 0 && o.selections != null).ToList();
            float cdBefore = -1f;
            try { cdBefore = Opts().GetFloat(FloatOptionNames.KillCooldown); } catch { }
            var baseP = SettingsPresets.SaveNew("Diag Base");
            Log($"saved Diag Base: {baseP?.Options.Count ?? -1} option(s) of {opts.Count}, vanilla {(baseP?.Vanilla != null ? "yes" : "no")}, kill cooldown {cdBefore}");

            // 60 options to a different value, and the kill cooldown
            var touched = opts.Where(o => o.selections.Length > 1).Take(60).ToList();
            var before = touched.ToDictionary(o => o.id, o => o.selection);
            foreach (var o in touched) o.selection = (o.selection + 1) % o.selections.Length;
            try { Opts().SetFloat(FloatOptionNames.KillCooldown, cdBefore + 7.5f); } catch (Exception e) { Log("cooldown set failed: " + e.Message); }
            Log($"changed {touched.Count} option(s), kill cooldown now {Opts()?.GetFloat(FloatOptionNames.KillCooldown)}");
            SettingsPresets.SaveNew("Diag Changed");
            SettingsPresets.SaveNew("Diag Evening Chaos Mode");
            SettingsPresets.SaveNew("Diag Rename Me");

            var ui = SettingsPresetsUI.Instance;
            if (ui != null) {
                ui.Open();
                ui.DiagType("Diag Typing Test");
                yield return new WaitForSeconds(1.2f);
                yield return Shot("1_list");
            }

            var target = SettingsPresets.List().FirstOrDefault(p => p.Name == "Diag Base");
            Log($"Diag Base differs in {SettingsPresets.Differences(target)} option(s) before loading (expected {touched.Count})");
            var r = SettingsPresets.Load(target);
            int wrong = touched.Count(o => o.selection != before[o.id]);
            float cdAfter = -1f;
            try { cdAfter = Opts().GetFloat(FloatOptionNames.KillCooldown); } catch { }
            Log($"loaded Diag Base: {r.Changed} changed, {r.Same} same, {r.Unknown} unknown, {r.Packets} packet(s), vanilla {r.VanillaApplied}; "
                + $"{wrong} option(s) not back, kill cooldown {cdAfter} (expected {cdBefore}) -> {(wrong == 0 && Mathf.Abs(cdAfter - cdBefore) < 0.01f && r.Changed == touched.Count ? "PASS" : "FAIL")}");
            Log($"differences after loading: {SettingsPresets.Differences(SettingsPresets.List().FirstOrDefault(p => p.Name == "Diag Base"))}");
            float waitT = Time.realtimeSinceStartup;
            while (PacedOptionSync.Sending && Time.realtimeSinceStartup - waitT < 5f) yield return null;
            Log($"paced send finished after {Time.realtimeSinceStartup - waitT:F2} s");
            if (ui != null) {
                ui.DiagRefresh();
                yield return new WaitForSeconds(1f);
                yield return Shot("2_loaded");
            }

            var ren = SettingsPresets.List().FirstOrDefault(p => p.Name == "Diag Rename Me");
            bool renOk = ren != null && SettingsPresets.Rename(ren, "Diag Renamed");
            bool found = SettingsPresets.List().Any(p => p.Name == "Diag Renamed") && !SettingsPresets.List().Any(p => p.Name == "Diag Rename Me");
            Log($"rename: {(renOk && found ? "PASS" : "FAIL")}");
            var del = SettingsPresets.List().FirstOrDefault(p => p.Name == "Diag Renamed");
            bool delOk = del != null && SettingsPresets.Delete(del) && !SettingsPresets.List().Any(p => p.Name == "Diag Renamed");
            Log($"delete: {(delOk ? "PASS" : "FAIL")}");
            if (ui != null) { ui.DiagRefresh(); yield return new WaitForSeconds(0.8f); yield return Shot("3_after_delete"); ui.Close(); }

            foreach (var p in SettingsPresets.List().Where(p => p.Name.StartsWith("Diag ")).ToList()) SettingsPresets.Delete(p);
            try { if (System.IO.Directory.Exists(SettingsPresets.Folder) && !System.IO.Directory.EnumerateFileSystemEntries(SettingsPresets.Folder).Any()) System.IO.Directory.Delete(SettingsPresets.Folder); } catch { }
            SettingsPresetsUI.DiagActive = false;
            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------------- music of other plugins
        private static IEnumerator MusicPart() {
            var ext = DraftMusic.ExternalFor(null);
            Log($"tracks of other plugins: {ext.Count}: {string.Join(", ", ext.Select(t => $"{t.Key} [{(t.Slots == null ? "all" : string.Join("/", t.Slots))}{(t.Tempo ? ", tempo" : "")}]"))}");
            var ui = DraftMusicUI.Instance;
            if (ui != null) {
                DraftMusicUI.DiagActive = true;
                for (int tab = 0; tab < 5; tab++) {
                    DraftMusicUI.Tab = tab; DraftMusicUI.Page = 999; DraftMusicUI.OpenKind = null;
                    ui.Open();
                    yield return new WaitForSeconds(0.9f);
                    yield return Shot($"4_music_{DraftMusic.SlotNames[tab]}");
                    ui.Close();
                }
                DraftMusicUI.Tab = 0; DraftMusicUI.Page = 0;
            }

            foreach (var t in ext) {
                float t0 = Time.realtimeSinceStartup;
                DraftMusic.TogglePreview(t.Key);
                while (DraftMusic.PreviewKey != t.Key && !DraftMusic.Failed(t.Key) && Time.realtimeSinceStartup - t0 < 25f) yield return null;
                var clip = DraftMusic.Get(t);
                Log($"{t.Key}: {(DraftMusic.PreviewKey == t.Key ? "playing" : "NOT playing")}{(DraftMusic.Failed(t.Key) ? " (" + DraftMusic.FailReason(t.Key) + ")" : "")}"
                    + $"{(clip != null ? $", {clip.length:F1} s, {clip.channels} ch, {clip.frequency} Hz" : "")} after {Time.realtimeSinceStartup - t0:F2} s");
                yield return new WaitForSeconds(0.6f);
                DraftMusic.StopPreview();
            }

            // the reactor tab speeds a "tempo" track up with the countdown
            var tempo = ext.FirstOrDefault(t => t.Tempo);
            if (tempo != null) {
                var clip = DraftMusic.Get(tempo);
                float t0 = Time.realtimeSinceStartup;
                while (clip == null && Time.realtimeSinceStartup - t0 < 20f) { yield return null; clip = DraftMusic.Get(tempo); }
                if (clip != null) {
                    var sm = SoundManager.instance;
                    sm.PlaySound(clip, true, 0.4f, sm.MusicChannel);           // as Unknown's Collection does with the pick
                    ReactorTrack.DiagTake(tempo);
                    ReactorTrack.DiagTotal = 30f;
                    var seen = new List<string>();
                    foreach (float left in new[] { 30f, 20f, 10f, 3f, 0f }) {
                        ReactorTrack.DiagLeft = left;
                        yield return new WaitForSeconds(0.4f);
                        float src = -1f;
                        try { if (sm.allSources.TryGetValue(clip, out var s) && s != null) src = s.pitch; } catch { }
                        seen.Add($"{left:F0}s->{src:F2}");
                    }
                    ReactorTrack.DiagLeft = -1f;
                    yield return new WaitForSeconds(0.3f);
                    float after = -1f;
                    try { if (sm.allSources.TryGetValue(clip, out var s) && s != null) after = s.pitch; } catch { }
                    try { sm.StopSound(clip); } catch { }
                    Log($"tempo {tempo.Key}: {string.Join("  ", seen)}; after the sabotage {after:F2}");
                } else Log($"tempo {tempo.Key}: clip not decoded");
            }
            DraftMusicUI.DiagActive = false;
        }

        // ---------------------------------------------------------------- titles of other plugins
        private static IEnumerator TitlesPart() {
            var ext = SessionStats.ExtTitlesOrdered();
            Log($"titles of other plugins: {ext.Count}: {string.Join(", ", ext.Select(x => $"{x.Id} (counted {SessionStats.PendingCount(x.Id)})"))}");
            SessionStats.DiagSample();
            var t = SessionStats.LastTable;
            if (t != null)
                foreach (var ti in t.ExtTitles)
                    Log($"title '{ti.Name}': {ti.ValueText}, holders {string.Join(",", ti.Holders)}");
            var st = SessionStatsUI.Instance;
            if (st != null) {
                st.Toggle();
                yield return new WaitForSeconds(1.2f);
                st.DiagShowTitles();
                yield return new WaitForSeconds(1.2f);
                yield return Shot("5_titles");
                st.Close();
            }
        }
    }
}
