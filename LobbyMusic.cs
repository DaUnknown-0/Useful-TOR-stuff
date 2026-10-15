// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * LobbyMusic - every player picks the music of the lobby for themselves: the game's lobby theme
 * (LobbyBehaviour.MapTheme) gets the chosen clip while the lobby exists (ThemeSwap). It pauses while the
 * music panel previews a track, so the two never play over each other. Config [LobbyMusic] Track
 * ("original", "random", "playlist" or a track key) and Playlist; the music panel has a "Lobby music" tab.
 */

using BepInEx.Configuration;
using UnityEngine;

namespace UsefulTORStuff {

    public static class LobbyMusic {
        public const string Original = "original";

        internal static readonly MusicSlot Slot = new MusicSlot(Original, true);
        internal static readonly ThemeSwap Swap = new ThemeSwap("LobbyMusic", Slot, InLobby, FindTheme, true);

        public static void Bind(ConfigFile config) {
            Slot.Choice = config.Bind("LobbyMusic", "Track", Original,
                "Music in the lobby, only for you. original = the game's own lobby theme, random = a different one in every "
                + "lobby, playlist = a different one of your playlist, or one of the draft music tracks (synthwave, noir, "
                + "...) or file:<name> from BepInEx/draft-music. Easier: the music panel in the lobby menu has a Lobby music tab.");
            Slot.Playlist = config.Bind("LobbyMusic", "Playlist", "",
                "Tracks for Track = playlist, separated by |, e.g. original|lofi. Easier: the + buttons in the panel.");
            Slot.Changed = Swap.Reset;
            // the game plays its lobby theme like a quiet radio (0.07); the default lifts it to 0.2 (user, 13.10.)
            Slot.BindVolume(config, "LobbyMusic", 20, 0.07f, "the lobby music");
        }

        internal static string Selected { get => Slot.Selected; set => Slot.Selected = value; }
        internal static string CurrentKey => Swap.CurrentKey;

        private static bool InLobby() {
            try { return LobbyBehaviour.Instance != null; } catch { return false; }
        }

        // The lobby's own theme clip first; otherwise the usual long looping music source.
        private static AudioSource FindTheme(AudioClip ours) {
            AudioClip theme = null;
            try { theme = LobbyBehaviour.Instance != null ? LobbyBehaviour.Instance.MapTheme : null; } catch { }
            if (theme != null) {
                foreach (var a in Object.FindObjectsOfType<AudioSource>()) {
                    try { if (a != null && a.clip == theme && a.isPlaying) return a; } catch { }
                }
            }
            return ThemeSwap.LongMusicSource(ours);
        }

        internal static void Tick() => Swap.Tick();
    }
}
