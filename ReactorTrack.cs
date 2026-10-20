// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ReactorTrack - the player's own pick for Unknown's Collection's reactor meltdown music, chosen
 * in the music panel (Reactor tab) like the draft, menu, lobby and meeting music.
 *
 * Unknown's Collection owns the score (intro, one of six loops, the finale that lands on the blast, the
 * all-clear) and asks once per reactor sabotage through the AppDomain contract "Unknowns.Music.ReactorLoop"
 * (a Func<object> returning an AudioClip or null). A pick replaces the whole score (intro and outro too) and
 * loops from the alarm until the sabotage ends; "default" (null) keeps its own score with six loops. The
 * next pick is drawn and decoded during the round, so it is ready when the alarm goes off; a pick that is not decoded yet in that moment falls back to the default for this one sabotage.
 * Without Unknown's Collection (or with its Reactor Music off) nobody asks, and nothing happens.
 *
 * Tempo (2026-10-20): a track another plugin added with the flag "tempo" (DraftMusic.AddTrack) plays faster the
 * less time the critical sabotage (reactor, seismic stabilizers, crash course) has left: AudioSource.pitch from 1.0
 * at the alarm up to MaxPitch at zero, on the source the game's SoundManager keeps for that clip (Unknown's
 * Collection plays the loop through SoundManager.PlaySound). The source gets pitch 1 back when the sabotage ends.
 * Purely local, like the pick itself.
 */

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace UsefulTORStuff {

    public static class ReactorTrack {
        public const string Default = "default";
        private const string ContractKey = "Unknowns.Music.ReactorLoop", VolumeKey = "Unknowns.Music.ReactorVolume";

        /// Choice and playlist of the reactor music (the default score may sit on the playlist).
        internal static readonly MusicSlot Slot = new MusicSlot(Default, true, "reactor");

        public static void Bind(ConfigFile config) {
            Slot.Choice = config.Bind("ReactorMusic", "Track", Default,
                "Unknown's Collection's reactor meltdown music, only for you. default = its own score, random = a "
                + "different one in every meltdown, playlist = a different one of your playlist, or one of the draft "
                + "music tracks or file:<name> from BepInEx/draft-music (plays from the alarm to the end). Easier: the "
                + "music panel (lobby menu or Mod Options) has a Reactor tab.");
            Slot.Playlist = config.Bind("ReactorMusic", "Playlist", "",
                "Tracks for Track = playlist, separated by |, e.g. default|chase. Easier: the + buttons in the panel.");
            Slot.Changed = () => { decided = false; next = null; };
            Slot.BindVolume(config, "ReactorMusic", 60, 0.6f, "the reactor music");
            try {
                AppDomain.CurrentDomain.SetData(ContractKey, (Func<object>)Take);
                AppDomain.CurrentDomain.SetData(VolumeKey, (Func<object>)(() => Slot.Level));   // a boxed float, read every frame
            } catch { }
        }

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[ReactorTrack] " + s);

        internal static string Selected { get => Slot.Selected; set => Slot.Selected = value; }

        private static DraftMusic.Track next;     // the pick for the next meltdown (null: the default score)
        private static DraftMusic.Track current;  // the pick handed out last
        private static bool decided;

        /// The pick handed out for the running (or last) meltdown.
        internal static DraftMusic.Track Current => current;

        /// Autotest: hand out a track as if a meltdown had asked for it.
        internal static object DiagTake(DraftMusic.Track t) { next = t; decided = true; return Take(); }

        /// Track keys DraftMusic must not evict from its clip cache.
        internal static bool Uses(string key) => key != null && (key == next?.Key || key == current?.Key);

        private static bool InRound() {
            try { return ShipStatus.Instance != null; } catch { return false; }
        }

        /// Called every frame by DraftMusicDriver: during a round, draw and decode the next meltdown's loop.
        internal static void Tick() {
            if (!InRound()) {
                if (tempoSource != null) ResetTempo();
                if (decided || current != null) { decided = false; next = null; current = null; }
                return;
            }
            if (!decided) { next = Slot.Resolve(); decided = true; }
            if (next != null) DraftMusic.Get(next);
            ApplyTempo();
        }

        // ---- tempo follows the countdown (tracks with the "tempo" flag) ----
        internal const float MaxPitch = 1.4f;
        private static AudioSource tempoSource;
        private static float tempoTotal;
        internal static float LastPitch { get; private set; } = 1f;   // diagnostics
        internal static float DiagLeft = -1f, DiagTotal;              // autotest: a countdown without a sabotage

        private static readonly SystemTypes[] Critical = { SystemTypes.Reactor, SystemTypes.Laboratory, SystemTypes.HeliSabotage };

        /// Seconds left on the active critical sabotage, or -1 if none runs.
        private static float CriticalLeft() {
            if (DiagLeft >= 0f) return DiagLeft;
            try {
                var ship = ShipStatus.Instance;
                if (ship == null || ship.Systems == null) return -1f;
                foreach (var st in Critical) {
                    if (!ship.Systems.ContainsKey(st)) continue;
                    var c = ship.Systems[st].TryCast<ICriticalSabotage>();
                    if (c != null && c.IsActive) return Mathf.Max(0f, c.Countdown);
                }
            } catch { }
            return -1f;
        }

        private static void ResetTempo() {
            if (tempoSource != null) { try { tempoSource.pitch = 1f; } catch { } }
            tempoSource = null;
            tempoTotal = 0f;
            LastPitch = 1f;
        }

        private static void ApplyTempo() {
            var t = current;
            float left = t != null && t.Tempo ? CriticalLeft() : -1f;
            if (left < 0f) { if (tempoSource != null || tempoTotal > 0f) ResetTempo(); return; }
            if (DiagLeft >= 0f) tempoTotal = DiagTotal;
            else if (left > tempoTotal) tempoTotal = left;     // the countdown's start is its length
            try {
                var clip = DraftMusic.Get(t);
                var sm = SoundManager.instance;
                if (clip == null || sm == null || !sm.allSources.TryGetValue(clip, out var src) || src == null) return;
                float p = tempoTotal > 0.1f ? Mathf.Clamp01(1f - left / tempoTotal) : 0f;
                float pitch = Mathf.Lerp(1f, MaxPitch, Mathf.Pow(p, 1.4f));
                if (!Mathf.Approximately(src.pitch, pitch)) src.pitch = pitch;
                tempoSource = src;
                LastPitch = pitch;
            } catch { }
        }

        // Unknown's Collection asks this when a reactor sabotage starts.
        private static object Take() {
            try {
                var t = next;
                current = t;
                decided = false;                        // the meltdown after this one draws again
                next = null;
                if (t == null) return null;
                var clip = DraftMusic.Get(t);
                Log(clip != null ? $"reactor loop -> {t.Title}" : $"{t.Title} not decoded yet, the default score plays this time");
                return clip;
            } catch { return null; }
        }
    }
}
