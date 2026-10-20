// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * MusicSlot - one place where the player's own music can play (Role Draft, menus, lobby, meetings, reactor), with
 * its own choice and its own playlist. DraftMusic, MenuMusic, LobbyMusic, MeetingMusic and ReactorTrack each own one slot; the
 * tracks themselves (built-ins and own files), decoding and the clip cache stay in DraftMusic.
 *
 * Choices: the slot's base key (what plays without us: "tor" = TOR's draft track, "original" = the game's
 * menu theme, "off" = nothing in meetings), "random" (any track, never the same twice in a row),
 * "playlist" (only the tracks the player put on this slot's playlist, same rule; an empty playlist plays
 * like random) or one track key. The base key may sit on the playlist where that makes sense (TOR's draft
 * track, the menu theme), so a playlist can mix the original with own tracks.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace UsefulTORStuff {

    internal sealed class MusicSlot {
        public readonly string BaseKey;
        public readonly bool BaseOnPlaylist;
        public ConfigEntry<string> Choice, Playlist;
        /// The player's volume for this slot: 0..100 % of full volume, the same top for every slot; only the start
        /// differs (user, 13.10.: lobby 20, menu 50, meetings 60, reactor 60, draft 100). The game's music slider
        /// works on top (music channel).
        public ConfigEntry<int> Volume;
        public const int MaxPercent = 100;
        /// The volume the game (or Unknown's Collection) itself gives this music: menu theme 0.5, lobby radio 0.07,
        /// reactor score 0.6, draft and meetings 1. The owners scale THAT volume, which keeps the game's fade-ins.
        public float GameStandard = 1f;
        /// While the panel's slider is held: the value it shows (0..1), heard right away; saved on release.
        public float? LivePercent;

        /// The slot's volume as a factor of full volume, 0..1.
        public float Percent01 {
            get {
                if (LivePercent.HasValue) return Mathf.Clamp01(LivePercent.Value);
                try { return Mathf.Clamp(Volume?.Value ?? 100, 0, MaxPercent) / 100f; } catch { return 1f; }
            }
        }

        /// The factor on the volume the game gives this music.
        public float Level => Percent01 / Mathf.Max(0.01f, GameStandard);

        public void BindVolume(ConfigFile config, string section, int defaultPercent, float gameStandard, string what) {
            GameStandard = gameStandard;
            // "VolumePercent", not "Volume": until 13.10. the meeting slot had a "Volume" of 60 relative to something else
            Volume = config.Bind(section, "VolumePercent", defaultPercent,
                new ConfigDescription($"Volume of {what} in percent of full volume (0..100). Easier: the slider in the music panel.",
                                      new AcceptableValueRange<int>(0, MaxPercent)));
        }

        /// The owner forgets what it prepared for the old choice.
        public Action Changed;

        private readonly System.Random rng = new System.Random();
        private string lastPick;

        /// "draft", "menu", "lobby", "meeting" or "reactor": which tracks of other plugins belong to this slot.
        public readonly string Name;

        public MusicSlot(string baseKey, bool baseOnPlaylist, string name = null) {
            BaseKey = baseKey;
            BaseOnPlaylist = baseOnPlaylist;
            Name = name;
        }

        public string Selected {
            get { try { return Choice?.Value ?? BaseKey; } catch { return BaseKey; } }
            set {
                if (Choice == null || Choice.Value == value) return;
                Choice.Value = value;
                Changed?.Invoke();
                DraftMusic.Changed();
            }
        }

        /// Keys on the playlist, in the order they were added.
        public List<string> PlaylistList() {
            try {
                return (Playlist?.Value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                                              .Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();
            } catch { return new List<string>(); }
        }

        public bool OnPlaylist(string key) => PlaylistList().Contains(key);

        public bool CanList(string key) =>
            !string.IsNullOrEmpty(key) && key != DraftMusic.Random && key != DraftMusic.Playlist && (key != BaseKey || BaseOnPlaylist);

        /// Adds or removes a track. Adding switches the slot to its playlist, which is what the player means
        /// by it; removing the last one keeps the choice (an empty playlist plays like random).
        public void TogglePlaylist(string key) {
            if (Playlist == null || !CanList(key) && !OnPlaylist(key)) return;
            var list = PlaylistList();
            bool add = !list.Remove(key);
            if (add) list.Add(key);
            Playlist.Value = string.Join("|", list);
            if (add) Selected = DraftMusic.Playlist;
            Changed?.Invoke();
            DraftMusic.Changed();
        }

        /// Playlist entries that can play right now (own files that were deleted drop out).
        private List<string> Playable() =>
            PlaylistList().Where(k => (k == BaseKey && BaseOnPlaylist) || (DraftMusic.Find(k) != null && !DraftMusic.Failed(k))).ToList();

        public int PlaylistCount => Playable().Count;

        /// What plays next: a track, or null for the base (TOR's track, the game's theme, silence).
        public DraftMusic.Track Resolve() {
            string sel = Selected;
            if (sel == BaseKey) return null;
            if (sel == DraftMusic.Playlist) {
                var keys = Playable();
                if (keys.Count > 0) {
                    if (keys.Count > 1) keys.Remove(lastPick);
                    string pick = keys[rng.Next(keys.Count)];
                    lastPick = pick;
                    return pick == BaseKey ? null : DraftMusic.Find(pick);
                }
                sel = DraftMusic.Random;
            }
            if (sel == DraftMusic.Random) {
                // every kind that has a version on this computer, plus the own files
                var pool = MusicLibrary.Kinds.Where(MusicLibrary.Playable).Select(k => k.Key)
                                       .Concat(DraftMusic.Files().Where(t => !DraftMusic.Failed(t.Key)).Select(t => t.Key))
                                       .Concat(DraftMusic.ExternalFor(Name).Where(t => t.InRandom && !DraftMusic.Failed(t.Key)
                                                                               && DraftMusic.Find(t.Key) != null).Select(t => t.Key)).ToList();
                if (pool.Count == 0) return null;
                if (pool.Count > 1) pool.Remove(lastPick);
                string p = pool[rng.Next(pool.Count)];
                lastPick = p;
                return DraftMusic.Find(p);
            }
            var t = DraftMusic.Find(sel);
            if (t == null) UsefulTORStuffPlugin.Logger?.LogWarning(MusicLibrary.KindOf(sel) != null
                ? $"[Music] '{sel}' is not downloaded, the original plays"
                : $"[Music] choice '{sel}' not found, the original plays");
            return t;
        }
    }
}
