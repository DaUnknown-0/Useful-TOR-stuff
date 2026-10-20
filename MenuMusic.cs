// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * MenuMusic - every player picks the music of the menus for themselves (the game's theme "AmongUsTheme",
 * which runs on an AudioSource of the SoundManager): the main menu and everything else outside a lobby and a
 * round (settings, finding a game, account screens), separately from draft, lobby, meeting and reactor music. The swap itself is ThemeSwap; config [MenuMusic] Track ("original", "random",
 * "playlist" or a track key) and Playlist. The music panel in the lobby has a "Menu music" tab.
 *
 * Autotest (config "Diagnostics / MenuMusic Test"): in the main menu it logs the playing sources, swaps in
 * a track and restores the original; then it hosts a local lobby (no server needed) and does the same with
 * the lobby theme (LobbyMusic), and quits the game.
 */

using System;
using System.Collections;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UsefulTORStuff {

    public static class MenuMusic {
        public const string Original = "original";

        internal static readonly MusicSlot Slot = new MusicSlot(Original, true, "menu");
        internal static readonly ThemeSwap Swap = new ThemeSwap("MenuMusic", Slot, InMenus, ThemeSwap.LongMusicSource, true);
        public static ConfigEntry<bool> DiagTest;

        public static void Bind(ConfigFile config) {
            Slot.Choice = config.Bind("MenuMusic", "Track", Original,
                "Music in the main menu, only for you. original = the game's own theme, random = a different one on every "
                + "visit, playlist = a different one of your playlist on every visit, or one of the draft music tracks "
                + "(synthwave, noir, ...) or file:<name> from BepInEx/draft-music. Easier: the music panel in the lobby "
                + "menu has a Menu music tab.");
            Slot.Playlist = config.Bind("MenuMusic", "Playlist", "",
                "Tracks for Track = playlist, separated by |, e.g. original|noir. Easier: the + buttons in the panel.");
            Slot.Changed = Swap.Reset;
            Slot.BindVolume(config, "MenuMusic", 50, 0.5f, "the menu music");
            DiagTest = config.Bind("Diagnostics", "MenuMusic Test", false,
                "Autotest only: swaps the main menu and the lobby theme once (hosts a local lobby for that) and quits.");
        }

        internal static string Selected { get => Slot.Selected; set => Slot.Selected = value; }
        internal static string CurrentKey => Swap.CurrentKey;

        // Every menu: not in a lobby, not in a round, not on the end screen (that one has its own music).
        private static bool InMenus() {
            try {
                if (LobbyBehaviour.Instance != null || ShipStatus.Instance != null) return false;
                string scene = SceneManager.GetActiveScene().name;
                return scene != "OnlineGame" && scene != "EndGame" && scene != "Tutorial";
            } catch { return false; }
        }

        internal static void Tick() => Swap.Tick();

        // ====================================================================
        // Autotest (main menu, then a local lobby)
        // ====================================================================
        private static bool diagFired;

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class DiagPatch {
            private static void Postfix() {
                if (diagFired || DiagTest == null || !DiagTest.Value || DraftMusicDriver.Instance == null) return;
                diagFired = true;
                // on the driver, not the menu: the coroutine has to survive the scene change into the lobby
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(DraftMusicDriver.Instance, DiagRun());
            }
        }

        private static void DiagLog(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[MenuMusicTest] " + s);

        private static void LogSources() {
            foreach (var a in UnityEngine.Object.FindObjectsOfType<AudioSource>()) {
                try {
                    if (a == null || a.clip == null || !a.isPlaying) continue;
                    DiagLog($"playing: '{a.clip.name}' {a.clip.length:F1} s, loop {a.loop}, group "
                            + $"'{(a.outputAudioMixerGroup != null ? a.outputAudioMixerGroup.name : "-")}', object '{a.gameObject.name}'");
                } catch { }
            }
        }

        private static IEnumerator SwapTest(string where, MusicSlot slot, ThemeSwap swap, string track) {
            string old = slot.Selected;
            slot.Selected = track;
            float t0 = Time.realtimeSinceStartup;
            while (swap.LastSwap == null && Time.realtimeSinceStartup - t0 < 15f) yield return null;
            yield return new WaitForSeconds(1.5f);
            var s = swap.Source;
            DiagLog($"{where} swap: {(swap.LastSwap ?? "NONE")}; source playing {(s != null ? s.isPlaying.ToString() : "-")}, "
                    + $"clip '{(s != null && s.clip != null ? s.clip.name : "-")}', volume {(s != null ? s.volume.ToString("F2") : "-")}");
            slot.LivePercent = 0.5f;
            yield return new WaitForSeconds(0.3f);
            DiagLog($"{where} volume at 50 %: {(s != null ? s.volume.ToString("F2") : "-")}");
            slot.LivePercent = null;
            yield return new WaitForSeconds(8f);
            DiagLog($"{where} volume 8 s later at 100 %: {(s != null ? s.volume.ToString("F2") : "-")}");
            slot.Selected = slot.BaseKey;
            yield return new WaitForSeconds(1.0f);
            DiagLog($"{where} original: clip '{(ThemeSwap.Alive(s) && s.clip != null ? s.clip.name : "-")}', "
                    + $"playing {(ThemeSwap.Alive(s) ? s.isPlaying.ToString() : "-")}, volume {(ThemeSwap.Alive(s) ? s.volume.ToString("F2") : "-")}");
            slot.Selected = old;
        }

        private static IEnumerator DiagRun() {
            float t0 = Time.time;
            while (Time.time - t0 < 45f) {
                bool loggedIn = false;
                try { loggedIn = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (loggedIn) break;
                yield return null;
            }
            yield return new WaitForSeconds(4f);
            DiagLog($"scene '{SceneManager.GetActiveScene().name}'");
            LogSources();
            yield return SwapTest("menu", Slot, Swap, "noir");

            // settings -> TOR's Mod Options -> Music: the panel opens outside a lobby, TOR's popup comes back after
            OptionsMenuBehaviour opts = null;
            try { opts = UnityEngine.Object.FindObjectOfType<OptionsMenuBehaviour>(true); opts?.Open(); } catch (Exception e) { DiagLog("options open: " + e.Message); }
            yield return new WaitForSeconds(1.5f);
            try {
                var parent = opts != null && opts.CensorChatButton != null ? opts.CensorChatButton.transform.parent : null;
                for (int i = 0; parent != null && i < parent.childCount; i++) {
                    var tb = parent.GetChild(i).GetComponent<ToggleButtonBehaviour>();
                    if (tb?.Text?.text?.Contains("Mod Options") == true) { tb.GetComponent<PassiveButton>().OnClick.Invoke(); break; }
                }
            } catch (Exception e) { DiagLog("mod options click: " + e.Message); }
            yield return new WaitForSeconds(1.2f);
            var pop = MusicOptionsButton.TorPopUp();
            bool hasButton = false;
            try { if (pop != null) foreach (var tb in pop.GetComponentsInChildren<ToggleButtonBehaviour>(true)) if (tb.name == "UTSMusicNavButton") hasButton = true; } catch { }
            DiagLog($"mod options: popup {(pop != null && pop.activeSelf)}, music button {hasButton}");
            yield return DraftMusicUI.DiagShot("options_popup");
            MusicOptionsButton.Open();
            yield return new WaitForSeconds(1.0f);
            DiagLog($"music panel open outside lobby: {DraftMusicUI.OpenOutsideLobby}, tor popup hidden {(pop != null && !pop.activeSelf)}");
            DraftMusicUI.Tab = 1;
            DraftMusic.Changed();
            yield return new WaitForSeconds(0.6f);
            DraftMusic.TogglePreview("lofi");
            float tp = Time.realtimeSinceStartup;
            while (DraftMusic.PreviewKey != "lofi" && Time.realtimeSinceStartup - tp < 15f) yield return null;
            yield return new WaitForSeconds(1.0f);
            var menuTheme = ThemeSwap.LongMusicSource(DraftMusic.PreviewClip);
            DiagLog($"preview in menu: {(DraftMusic.PreviewKey ?? "none")}; menu theme still playing: {(menuTheme != null ? menuTheme.clip.name : "no (paused)")}");
            yield return DraftMusicUI.DiagShot("options_music");
            DraftMusicUI.Instance?.Close();
            DraftMusicUI.Tab = 0;
            yield return new WaitForSeconds(1.0f);
            var s2 = ThemeSwap.LongMusicSource(null);
            DiagLog($"after close: tor popup back {(pop != null && pop.activeSelf)}, menu theme playing {(s2 != null ? s2.clip.name : "NONE")}");
            try { if (pop != null) pop.SetActive(false); opts?.Close(); } catch { }
            yield return new WaitForSeconds(0.5f);

            try {
                var host = UnityEngine.Object.FindObjectOfType<HostLocalGameButton>(true);
                if (host == null) DiagLog("no HostLocalGameButton");
                else {
                    // OnClick runs a coroutine on the button itself: an inactive menu would swallow it silently
                    for (var tr = host.transform; tr != null; tr = tr.parent) if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                    host.NetworkMode = NetworkModes.LocalGame;
                    host.OnClick();
                    DiagLog("hosting a local lobby");
                }
            } catch (Exception e) { DiagLog("host failed: " + e.Message); }
            t0 = Time.realtimeSinceStartup;
            while (LobbyBehaviour.Instance == null && Time.realtimeSinceStartup - t0 < 45f) yield return null;
            yield return new WaitForSeconds(4f);
            string theme = "-";
            try { theme = LobbyBehaviour.Instance?.MapTheme?.name ?? "-"; } catch { }
            DiagLog($"lobby {(LobbyBehaviour.Instance != null)}, MapTheme '{theme}'");
            for (int k = 0; k < 4; k++) {                                  // the game's own fade-in, untouched
                var lt = ThemeSwap.LongMusicSource(null);
                DiagLog($"lobby theme by itself: volume {(lt != null ? lt.volume.ToString("F2") : "-")}");
                yield return new WaitForSeconds(3f);
            }
            LogSources();
            if (LobbyBehaviour.Instance != null) yield return SwapTest("lobby", LobbyMusic.Slot, LobbyMusic.Swap, "space");

            DiagLog("done");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }
    }
}
