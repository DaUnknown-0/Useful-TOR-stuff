// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * MeetingMusic - optional background music during meetings, chosen by every player for themselves, separately
 * from draft, menu and lobby music. The game plays nothing there, so the
 * default is off.
 *
 * While a MeetingHud exists and no exile cutscene runs, the chosen track plays on the game's music channel
 * (SoundManager.PlaySound, looping), fades in over 1.5 s and fades out over 1 s when the meeting ends or
 * the exile starts. "random" draws a different track for every meeting. Same tracks, decoder and cache as
 * DraftMusic; the clip of the next meeting is decoded ahead of time during the round.
 *
 * Config [MeetingMusic] Track ("off", "random" or a track key) and Volume (percent of the music volume,
 * default 60: people talk in meetings). The lobby panel of DraftMusic has a "Meetings" tab.
 */

using System;
using System.Collections;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace UsefulTORStuff {

    public static class MeetingMusic {
        public const string Off = "off";

        /// Choice and playlist of the meetings ("off" never sits on the playlist).
        internal static readonly MusicSlot Slot = new MusicSlot(Off, false, "meeting");

        public static void Bind(ConfigFile config) {
            Slot.Choice = config.Bind("MeetingMusic", "Track", Off,
                "Background music during meetings, only for you. off = none (like the game), random = a different one in "
                + "every meeting, playlist = a different one of your playlist in every meeting, or one of the draft music "
                + "tracks (synthwave, noir, ...) or file:<name> from BepInEx/draft-music. Easier: the Draft music panel in "
                + "the lobby menu has a Meetings tab.");
            Slot.Playlist = config.Bind("MeetingMusic", "Playlist", "",
                "Tracks for Track = playlist, separated by |, e.g. noir|lofi. Easier: the + buttons in the panel.");
            Slot.Changed = () => { next = null; nextFor = null; };
            Slot.BindVolume(config, "MeetingMusic", 60, 1f, "the meeting music (people talk in meetings)");
        }

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[MeetingMusic] " + s);

        internal static string Selected { get => Slot.Selected; set => Slot.Selected = value; }

        private static float Level => Slot.Level;

        private static DraftMusic.Track next;      // decided during the round, decoded before the meeting
        private static string nextFor;
        private static DraftMusic.Track playing;
        private static AudioClip clip;
        private static AudioSource source;
        private static float fade;                 // 0..1
        private static bool ending;

        /// Track key of the meeting music in use or prepared (DraftMusic keeps its clip in the cache).
        internal static string CurrentKey => playing?.Key ?? next?.Key;

        private static bool MeetingOn() {
            try { return MeetingHud.Instance != null && ExileController.Instance == null; } catch { return false; }
        }

        private static bool InRound() {
            try { return ShipStatus.Instance != null; } catch { return false; }
        }

        /// Called every frame by DraftMusicDriver.
        internal static void Tick() {
            string sel = Selected;
            bool meeting = MeetingOn();

            if (!meeting) {
                if (playing != null) FadeOut();
                // during the round: pick and decode the next meeting's track ahead of time
                if (sel != Off && InRound() && nextFor != sel) { next = Slot.Resolve(); nextFor = sel; }
                if (next != null && InRound()) DraftMusic.Get(next);
                return;
            }

            if (sel == Off) { if (playing != null) FadeOut(); return; }
            if (playing == null && !ending) {
                playing = nextFor == sel ? next : Slot.Resolve();
                next = null; nextFor = null;
                if (playing == null) return;
                fade = 0f;
                Log($"meeting music: {playing.Title}");
            }
            if (playing == null) return;
            if (source == null) {
                var c = DraftMusic.Get(playing);
                if (c == null) return;                                 // still decoding
                try {
                    var sm = SoundManager.instance;
                    if (sm == null) return;
                    source = sm.PlaySound(c, true, 0f, sm.MusicChannel);
                    clip = c;
                } catch (Exception e) { Log("play failed: " + e.Message); playing = null; return; }
            }
            fade = Mathf.Min(1f, fade + Time.deltaTime / 1.5f);
            Apply();
        }

        private static void Apply() {
            try { if (source != null) source.volume = Mathf.Clamp01(fade * Level); } catch { source = null; }
        }

        private static void FadeOut() {
            ending = true;
            fade = Mathf.Max(0f, fade - Time.deltaTime / 1.0f);
            Apply();
            if (fade > 0f && source != null) return;
            try { if (clip != null) SoundManager.instance?.StopSound(clip); } catch { }
            source = null; clip = null; playing = null; ending = false;
        }
    }
}
