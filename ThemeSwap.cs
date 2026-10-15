// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ThemeSwap - gives a theme the game already plays (main menu, lobby) the clip the player picked in a
 * MusicSlot, scales it with the slot's volume (also the original), and puts clip and volume back afterwards.
 * Used by MenuMusic and LobbyMusic.
 *
 * Nothing in the game is patched. While the context is active (main menu scene, lobby), Tick looks for the
 * AudioSource that plays the theme (the context's finder) and swaps its clip; the original clip is
 * remembered. "original" (the slot's base) restores it right away; leaving the context restores it silently,
 * because the source may live on in the sound pool. Random and playlist draw again on every visit.
 * Optionally the source pauses while the music panel previews a track, so the two do not play over each other.
 */

using System;
using UnityEngine;

namespace UsefulTORStuff {

    internal sealed class ThemeSwap {
        private readonly string tag;
        private readonly MusicSlot slot;
        private readonly Func<bool> inContext;
        private readonly Func<AudioClip, AudioSource> find;
        private readonly bool pauseForPreview;

        private AudioSource src;          // the theme's source while the context is active
        private AudioClip original;       // its own clip
        private float baseVolume = 1f;    // the volume the game wants (it fades its themes in): ours is base * level
        private float? written;           // what we wrote last; anything else on the source came from the game
        private AudioClip ourClip;        // the clip we put on it (null: the original plays)
        private DraftMusic.Track wanted;  // what this visit plays (null: the original)
        private bool decided;
        private AudioSource pausedSrc;    // paused for a preview in the music panel
        private float nextScan;

        public string LastSwap { get; private set; }
        public AudioSource Source => Alive(src) ? src : null;
        public string CurrentKey => wanted?.Key;

        public ThemeSwap(string tag, MusicSlot slot, Func<bool> inContext, Func<AudioClip, AudioSource> find, bool pauseForPreview) {
            this.tag = tag;
            this.slot = slot;
            this.inContext = inContext;
            this.find = find;
            this.pauseForPreview = pauseForPreview;
        }

        private void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo($"[{tag}] {s}");

        /// The slot's choice changed: draw again.
        public void Reset() { decided = false; wanted = null; }

        internal static bool Alive(AudioSource a) {
            try { return a != null && a.gameObject != null; } catch { return false; }
        }

        /// The usual theme: playing, looping, on the game's music channel, longer than 15 s, not ours.
        internal static AudioSource LongMusicSource(AudioClip ours) {
            UnityEngine.Audio.AudioMixerGroup music = null;
            try { music = SoundManager.Instance != null ? SoundManager.Instance.MusicChannel : null; } catch { }
            foreach (var a in UnityEngine.Object.FindObjectsOfType<AudioSource>()) {
                try {
                    if (a == null || !a.isPlaying || !a.loop || a.clip == null || a.clip == ours) continue;
                    if (music != null && a.outputAudioMixerGroup != music) continue;
                    if (a.clip.length < 15f) continue;
                    return a;
                } catch { }
            }
            return null;
        }

        public void Tick() {
            bool active;
            try { active = inContext(); } catch { active = false; }
            if (!active) {
                if (src != null || decided || pausedSrc != null) Leave();
                return;
            }
            if (pauseForPreview && HandlePreview()) return;

            AudioClip ours = null;
            if (slot.Selected != slot.BaseKey) {
                if (!decided) { wanted = slot.Resolve(); decided = true; }
                if (wanted != null) ours = DraftMusic.Get(wanted);      // null while decoding: the original keeps playing
            }

            // the game put something else on the source (sound pool): let go of it
            if (Alive(src) && src.clip != original && src.clip != ourClip) Release();
            if (!Alive(src)) {
                src = null;
                if (Time.realtimeSinceStartup < nextScan) return;
                nextScan = Time.realtimeSinceStartup + 0.5f;
                src = find(ours);
                if (src == null) return;
                original = src.clip;
                baseVolume = src.volume;
                written = null;
                ourClip = null;
                Log($"theme found: '{original.name}' ({original.length:F1} s) on '{src.gameObject.name}'");
            }

            var target = ours ?? original;
            if (target != null && src.clip != target) {
                src.clip = target;
                src.loop = true;
                src.time = 0f;
                src.Play();
                if (target == ours) { LastSwap = ours.name; Log($"music -> {wanted.Title}"); }
                else Log("original theme restored");
            }
            ourClip = ours;
        }

        /// After every Update (DraftMusicDriver.LateUpdate): the slot's level on top of the volume the game gives
        /// the theme. The game fades its themes in (measured 13.10.: menu 0.13 right after the start, settling at
        /// 0.5; the lobby radio sits at 0.07) and may set it every frame, so a value we did not write is the
        /// game's new base, and writing late means ours is the one that is heard. The music slider of the game
        /// works on the channel on top.
        public void LateTick() {
            if (!Alive(src) || pausedSrc != null) return;
            try {
                float cur = src.volume;
                if (!written.HasValue || !Mathf.Approximately(cur, written.Value)) baseVolume = cur;
                float v = Mathf.Clamp01(baseVolume * slot.Level);
                if (!Mathf.Approximately(cur, v)) src.volume = v;
                written = v;
            } catch { }
        }

        // While the music panel previews a track, the theme (ours or the original) pauses; true = skip the rest.
        private bool HandlePreview() {
            bool preview = DraftMusic.PreviewKey != null;
            if (preview) {
                if (pausedSrc == null) {
                    var s = Alive(src) ? src : find(DraftMusic.PreviewClip);
                    try { if (s != null && s.isPlaying) { s.Pause(); pausedSrc = s; } } catch { }
                }
                return true;
            }
            if (pausedSrc != null) {
                try { if (Alive(pausedSrc)) pausedSrc.UnPause(); } catch { }
                pausedSrc = null;
            }
            return false;
        }

        // Let go of the source (the game reuses it): its own volume back, nothing else touched.
        private void Release() {
            try { if (Alive(src)) src.volume = baseVolume; } catch { }
            src = null; original = null; ourClip = null; written = null;
        }

        // Leaving the context: the source may live on (sound pool), so it gets its own clip and volume back, silent.
        private void Leave() {
            if (Alive(src)) {
                try {
                    if (original != null && src.clip != original) { src.Stop(); src.clip = original; }
                    src.volume = baseVolume;
                } catch { }
            }
            try { if (Alive(pausedSrc)) pausedSrc.UnPause(); } catch { }
            src = null; original = null; ourClip = null; wanted = null; decided = false; pausedSrc = null; written = null;
        }
    }
}
