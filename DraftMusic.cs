// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DraftMusic - every player picks the music of TOR's Role Draft for themselves.
 *
 * TOR starts its draft track with SoundEffectsManager.play("draft") at the top of
 * RoleDraft.CoSelectRoles and ends it with SoundEffectsManager.stop("draft"). Both go through
 * SoundManager, which keeps exactly one AudioSource per clip in allSources. This module patches
 * nothing in TOR (tiered compilation silently drops Harmony detours on managed TOR methods): while
 * RoleDraft.isRunning is set, DraftMusicDriver looks up the source that plays TOR's clip and gives
 * it the chosen clip instead. TOR's own stop still finds that source under its clip, so the chosen
 * music ends exactly where TOR's would have. TOR also writes "Music: Ultimate Superhero 3 ..." into
 * the draft title every frame; LateUpdate (after TOR's coroutine) swaps in the chosen title.
 *
 * Choices (local, config [DraftMusic] Track): "tor" (TOR's own track), "random" (a different one
 * every draft), "playlist" (a different one every draft, drawn only from the tracks the player put on
 * the playlist, config [DraftMusic] Playlist; TOR's track may be on it too), one kind of the built-in music
 * (MusicLibrary: one of its switched-on versions plays), or "file:<name>" from BepInEx\draft-music (Ogg
 * Vorbis, MP3 or WAV). The built-in music is downloaded on request (MusicLibrary), not shipped; every
 * file is decoded on a worker thread (Ogg: NVorbis, MP3: NLayer, both ILRepack-merged; WAV: own reader
 * for PCM 8/16/24/32 bit and float), only AudioClip.Create/SetData run on the main thread. The track for the next draft is decoded while the lobby is open, so the
 * swap happens in the draft's first frame; until a clip is ready TOR's own track keeps playing.
 *
 * Nobody else hears or needs anything: no option, no RPC, no host involvement.
 *
 * TRACKS OF OTHER PLUGINS (2026-10-20): AppDomain contract, BCL types only (like UTS.LobbyMenu.Add):
 *   "UTS.Music.AddTrack"     Action<string id, string title, string group, string path, string flags>
 *   "UTS.Music.RemoveTrack"  Action<string id>
 * path is an .ogg, .mp3 or .wav file on disk (decoded like an own file). The track gets the key "ext:<id>" and shows
 * in the music panel as an own choice (and for the playlist) with group as its style line. flags (comma list, may be
 * empty): slot names "draft", "menu", "lobby", "meeting", "reactor" limit the tabs it shows in (none = all tabs);
 * "random" also puts it into the random pool of those tabs; "tempo" lets the reactor tab play it faster the less
 * time the critical sabotage has left (AudioSource.pitch, see ReactorTrack).
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public static class DraftMusic {
        public const string Tor = "tor", Random = "random", Playlist = "playlist", FilePrefix = "file:", ExtPrefix = "ext:";

        internal sealed class Track {
            public string Key;       // "synthwave-a" (a version of a built-in kind), "file:My Song.ogg"
            public string Title;     // shown in the panel and in the draft's credit line
            public string Style;     // "Synthwave" (the kind); empty for files
            public string KindKey;   // "synthwave"; null for files
            public long Size;        // built-ins: bytes of the download
            public string Sha256;    // built-ins: lower-case hex of the download
            public bool Best;        // built-ins: one of the kind's best rated versions (switched on by default)
            public string FilePath;  // absolute path (own files, tracks of other plugins)
            public bool IsFile => FilePath != null;
            // tracks of other plugins (UTS.Music.AddTrack)
            public bool External;
            public string[] Slots;   // tabs it shows in, null = all
            public bool InRandom;    // also drawn by "random"
            public bool Tempo;       // the reactor tab speeds it up with the sabotage's countdown
        }

        // ====================================================================
        // Tracks of other plugins (see the header)
        // ====================================================================
        public const string AddTrackKey = "UTS.Music.AddTrack", RemoveTrackKey = "UTS.Music.RemoveTrack";
        private static readonly List<Track> external = new List<Track>();
        internal static readonly string[] SlotNames = { "draft", "menu", "lobby", "meeting", "reactor" };

        internal static void PublishContract() {
            try {
                AppDomain.CurrentDomain.SetData(AddTrackKey, new Action<string, string, string, string, string>(AddTrack));
                AppDomain.CurrentDomain.SetData(RemoveTrackKey, new Action<string>(RemoveTrack));
            } catch { }
        }

        /// <summary>Adds or replaces (same id) a track of another plugin. See the header for the flags.</summary>
        public static void AddTrack(string id, string title, string group, string path, string flags) {
            try {
                id = (id ?? "").Trim().Replace("|", "");
                if (id.Length == 0 || string.IsNullOrEmpty(path)) return;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!Formats.Contains(ext)) { Warn($"track '{id}' refused: {ext} is not .ogg, .mp3 or .wav"); return; }
                var tokens = (flags ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(f => f.Trim().ToLowerInvariant()).ToList();
                var slots = tokens.Where(t => SlotNames.Contains(t)).Distinct().ToArray();
                string key = ExtPrefix + id;
                external.RemoveAll(t => t.Key == key);
                failed.Remove(key);
                external.Add(new Track {
                    Key = key, Title = string.IsNullOrWhiteSpace(title) ? id : title.Trim(), Style = group ?? "", FilePath = path,
                    External = true, Slots = slots.Length > 0 ? slots : null, InRandom = tokens.Contains("random"), Tempo = tokens.Contains("tempo")
                });
                Changed();
                Log($"track '{key}' added by another plugin ({(slots.Length > 0 ? string.Join("/", slots) : "all tabs")}"
                    + $"{(tokens.Contains("tempo") ? ", tempo" : "")}): {Path.GetFileName(path)}");
            } catch (Exception e) { Warn($"AddTrack '{id}': {e.Message}"); }
        }

        public static void RemoveTrack(string id) {
            string key = ExtPrefix + (id ?? "").Trim();
            if (external.RemoveAll(t => t.Key == key) > 0) { Forget(key); Changed(); }
        }

        /// The tracks of other plugins for one tab ("draft", "menu", ...), in the order they were added.
        internal static List<Track> ExternalFor(string slot) =>
            external.Where(t => t.Slots == null || slot == null || t.Slots.Contains(slot)).ToList();

        /// Choice and playlist of the draft (TOR's own track may sit on the playlist).
        internal static readonly MusicSlot Slot = new MusicSlot(Tor, true, "draft");
        public static ConfigEntry<bool> DiagTest;

        public static void Bind(ConfigFile config) {
            MusicLibrary.Bind(config);
            Slot.Choice = config.Bind("DraftMusic", "Track", Tor,
                "Music during TOR's Role Draft, only for you. tor = TOR's own track, random = a different one every "
                + "draft, playlist = a different one of your playlist every draft, a kind of music like synthwave, chase "
                + "or noir (downloaded with Load music), or file:<name> for an .ogg, .mp3 or .wav file in BepInEx/draft-music. Easier: "
                + "the lobby menu (bottom left) has a Draft music panel.");
            Slot.Playlist = config.Bind("DraftMusic", "Playlist", "",
                "Tracks for Track = playlist, separated by |, e.g. tor|noir|file:My Song.ogg. Easier: the + buttons in "
                + "the Draft music panel.");
            Slot.Changed = Undecide;                     // the lobby preloads the new choice
            Slot.BindVolume(config, "DraftMusic", 100, 1f, "the draft music");
            PublishContract();
            DiagTest = config.Bind("Diagnostics", "DraftMusic Test", false,
                "Autotest only: in freeplay, opens the draft music panel, then plays a simulated draft and logs whether the chosen clip took over.");
        }

        internal static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, "draft-music");

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[DraftMusic] " + s);
        private static void Warn(string s) => UsefulTORStuffPlugin.Logger?.LogWarning("[DraftMusic] " + s);

        internal static string Selected { get => Slot.Selected; set => Slot.Selected = value; }

        private static List<Track> files = new List<Track>();
        private static float filesScannedAt = -100f;

        /// Own Ogg files in BepInEx\draft-music (rescanned at most every 2 s).
        internal static List<Track> Files(bool force = false) {
            if (!force && Time.realtimeSinceStartup - filesScannedAt < 2f) return files;
            filesScannedAt = Time.realtimeSinceStartup;
            var list = new List<Track>();
            try {
                if (Directory.Exists(Folder)) {
                    var paths = Directory.GetFiles(Folder)
                                         .Where(p => Formats.Contains(Path.GetExtension(p).ToLowerInvariant()))
                                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(MaxFiles);
                    foreach (var path in paths) {
                        string name = Path.GetFileName(path);
                        list.Add(new Track { Key = FilePrefix + name, Title = Path.GetFileNameWithoutExtension(path), Style = "", FilePath = path });
                    }
                }
            } catch (Exception e) { Warn("folder scan failed: " + e.Message); }
            files = list;
            return files;
        }

        internal const int MaxFiles = 12;
        /// File types the own-music folder accepts (lower case, with dot).
        internal static readonly string[] Formats = { ".ogg", ".mp3", ".wav" };

        private static readonly System.Random pickRng = new System.Random();

        /// A version key gives that version, a kind key one of the kind's playable versions (drawn anew on
        /// every call; null while none is on this computer), "file:" an own file.
        internal static Track Find(string key) {
            if (string.IsNullOrEmpty(key)) return null;
            var kind = MusicLibrary.KindOf(key);
            if (kind != null) return MusicLibrary.Pick(kind, pickRng);
            var v = MusicLibrary.Version(key);
            if (v != null) return v;
            if (key.StartsWith(FilePrefix)) foreach (var t in Files()) if (t.Key == key) return t;
            if (key.StartsWith(ExtPrefix)) foreach (var t in external) if (t.Key == key) return File.Exists(t.FilePath) ? t : null;
            return null;
        }

        /// Like Find, but always the same version of a kind (the first playable): for previews and tests.
        internal static Track Peek(string key) {
            var kind = MusicLibrary.KindOf(key);
            return kind != null ? MusicLibrary.Pick(kind) : Find(key);
        }

        /// The kind a key belongs to (itself for a kind key), or the key itself.
        internal static string KindKeyOf(string key) => MusicLibrary.Version(key)?.KindKey ?? key;

        /// The folder exists after this, with a short note on what belongs in it.
        internal static void EnsureFolder() {
            try {
                Directory.CreateDirectory(Folder);
                string readme = Path.Combine(Folder, "README.txt");
                // also replaces the note of older versions, which still said "Ogg Vorbis only"
                if (!File.Exists(readme) || !File.ReadAllText(readme).Contains(".mp3"))
                    File.WriteAllText(readme,
                        "Put your own draft music here: .ogg (Ogg Vorbis), .mp3 or .wav files, up to 6 minutes each.\n"
                        + "They show up in the lobby menu (bottom left) under Draft music. A file loops while the\n"
                        + "draft runs, so a track whose end flows back into its start sounds best.\n"
                        + "MP3 files usually carry a short silence at the start, so they do not loop as cleanly,\n"
                        + "and they need a sample rate of 32 kHz or more (44.1 or 48 kHz, as music files have).\n"
                        + "Ogg Opus files are not supported; convert them to Ogg Vorbis first.\n");
            } catch (Exception e) { Warn("folder: " + e.Message); }
        }

        internal static void OpenFolder() {
            EnsureFolder();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Folder, UseShellExecute = true }); }
            catch (Exception e) { Warn("open folder: " + e.Message); }
        }

        /// Which track the next draft plays: null = TOR's own.
        private static Track Resolve() => Slot.Resolve();

        // ====================================================================
        // Decoding: NVorbis on a worker thread, AudioClip on the main thread
        // ====================================================================
        private sealed class Decoded {
            public string Key, Name;
            public float[] Samples;
            public int Channels, Rate;
            public string Error;
        }

        private const int MaxSeconds = 360;   // own files: longer ones are cut, a float buffer of 6 min is ~127 MB already
        private const int CacheSize = 3;

        private static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private static readonly List<string> lru = new List<string>();
        private static readonly HashSet<string> decoding = new HashSet<string>();
        private static readonly Dictionary<string, string> failed = new Dictionary<string, string>();
        private static readonly ConcurrentQueue<Decoded> done = new ConcurrentQueue<Decoded>();

        // bumped on everything the panel shows (ready, failed, selection, preview), so it can redraw
        internal static int Version { get; private set; }
        internal static void Changed() => Version++;

        internal static bool Failed(string key) => key != null && failed.ContainsKey(key);
        internal static string FailReason(string key) => key != null && failed.TryGetValue(key, out var r) ? r : null;
        internal static bool Loading(string key) => key != null && decoding.Contains(key);

        /// The clip if decoded; otherwise starts decoding (once) and returns null.
        internal static AudioClip Get(Track t) {
            if (t == null) return null;
            if (clips.TryGetValue(t.Key, out var c) && c != null) { Touch(t.Key); return c; }
            if (failed.ContainsKey(t.Key) || decoding.Contains(t.Key)) return null;
            if (!t.IsFile && !MusicLibrary.Ready(t)) return null;   // not downloaded (any more)
            decoding.Add(t.Key);
            Changed();
            var track = t;
            ThreadPool.QueueUserWorkItem(_ => done.Enqueue(Decode(track)));
            return null;
        }

        private static Decoded Decode(Track t) {
            var d = new Decoded { Key = t.Key, Name = "UTS draft " + t.Title };
            try {
                using Stream s = File.OpenRead(t.IsFile ? t.FilePath : MusicLibrary.PathOf(t));
                string ext = t.IsFile ? Path.GetExtension(t.FilePath).ToLowerInvariant() : ".ogg";
                if (ext == ".wav") DecodeWav(s, d);
                else if (ext == ".mp3") DecodeMp3(s, d);
                else DecodeOgg(s, d);
                if (d.Error != null) { d.Samples = null; return d; }
                if (d.Samples == null || d.Samples.Length < d.Channels * d.Rate / 2) { d.Error = "too short"; d.Samples = null; return d; }
            } catch (Exception e) {
                d.Error = e.GetType().Name;
                d.Samples = null;
            }
            return d;
        }

        private static void DecodeOgg(Stream s, Decoded d) {
            using var vorbis = new NVorbis.VorbisReader(s, false);
            d.Channels = vorbis.Channels;
            d.Rate = vorbis.SampleRate;
            long frames = Math.Min(vorbis.TotalSamples, (long)MaxSeconds * d.Rate);
            if (frames <= 0 || d.Channels < 1 || d.Channels > 2) { d.Error = "unsupported"; return; }
            var buf = new float[frames * d.Channels];
            int total = 0;
            while (total < buf.Length) {
                int read = vorbis.ReadSamples(buf, total, buf.Length - total);
                if (read <= 0) break;
                total += read;
            }
            if (total < buf.Length) Array.Resize(ref buf, total - total % d.Channels);
            d.Samples = buf;
        }

        // MP3 has no reliable total length up front (VBR), so it is read in blocks up to the cap.
        private static void DecodeMp3(Stream s, Decoded d) {
            using var mp3 = new NLayer.MpegFile(s);
            d.Channels = mp3.Channels;
            d.Rate = mp3.SampleRate;
            if (d.Rate <= 0 || d.Channels < 1 || d.Channels > 2) { d.Error = "unsupported"; return; }
            // NLayer decodes MPEG-2/2.5 (16, 22.05, 24 kHz and below) only halfway (measured 2026-10-13: 4 of 8 s,
            // VBR stops after 0.5 s); refusing beats playing a broken half. Music MP3s are 44.1/48 kHz anyway.
            if (d.Rate < 32000) { d.Error = "MP3 below 32 kHz"; return; }
            long cap = (long)MaxSeconds * d.Rate * d.Channels;
            var buf = new float[Math.Min(cap, (long)d.Rate * d.Channels * 30)];
            int total = 0;
            while (total < cap) {
                if (total == buf.Length) Array.Resize(ref buf, (int)Math.Min(cap, (long)buf.Length * 2));
                int read = mp3.ReadSamples(buf, total, buf.Length - total);
                if (read <= 0) break;
                total += read;
            }
            Array.Resize(ref buf, total - total % d.Channels);
            d.Samples = buf;
        }

        // RIFF/WAVE: PCM 8/16/24/32 bit, IEEE float 32/64 bit, also as WAVE_FORMAT_EXTENSIBLE; mono or stereo.
        private static void DecodeWav(Stream s, Decoded d) {
            using var r = new BinaryReader(s);
            if (new string(r.ReadChars(4)) != "RIFF") { d.Error = "not a wav"; return; }
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE") { d.Error = "not a wav"; return; }
            int format = 0, bits = 0;
            while (s.Position + 8 <= s.Length) {
                string id = new string(r.ReadChars(4));
                long size = r.ReadUInt32();
                long next = s.Position + size + (size & 1);              // chunks are padded to even length
                if (id == "fmt ") {
                    format = r.ReadUInt16();
                    d.Channels = r.ReadUInt16();
                    d.Rate = r.ReadInt32();
                    r.ReadInt32(); r.ReadUInt16();                         // byte rate, block align
                    bits = r.ReadUInt16();
                    if (format == 0xFFFE && size >= 26) {                  // extensible: real format in the sub-format GUID
                        r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32();
                        format = r.ReadUInt16();
                    }
                } else if (id == "data") {
                    if (d.Rate <= 0 || d.Channels < 1 || d.Channels > 2) { d.Error = "unsupported"; return; }
                    bool pcm = format == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32);
                    bool flt = format == 3 && (bits == 32 || bits == 64);
                    if (!pcm && !flt) { d.Error = $"unsupported wav ({format}/{bits} bit)"; return; }
                    int bps = bits / 8;
                    long avail = Math.Min(size, s.Length - s.Position) / bps;
                    long count = Math.Min(avail, (long)MaxSeconds * d.Rate * d.Channels);
                    count -= count % d.Channels;
                    var buf = new float[count];
                    var raw = r.ReadBytes((int)(count * bps));
                    for (long i = 0; i < count; i++) {
                        int o = (int)(i * bps);
                        buf[i] = bits switch {
                            8 => (raw[o] - 128) / 128f,
                            16 => BitConverter.ToInt16(raw, o) / 32768f,
                            24 => ((raw[o] | raw[o + 1] << 8 | raw[o + 2] << 16) << 8 >> 8) / 8388608f,
                            32 when flt => BitConverter.ToSingle(raw, o),
                            32 => BitConverter.ToInt32(raw, o) / 2147483648f,
                            _ => (float)BitConverter.ToDouble(raw, o),
                        };
                    }
                    d.Samples = buf;
                    return;
                }
                if (next > s.Length) break;
                s.Position = next;
            }
            d.Error = "no audio data";
        }

        /// Main thread: turns finished decodes into clips (at most one per frame, SetData copies a few MB).
        private static void Pump() {
            if (!done.TryDequeue(out var d)) return;
            decoding.Remove(d.Key);
            if (d.Samples == null) {
                failed[d.Key] = d.Error ?? "error";
                Warn($"{d.Key}: cannot be played ({failed[d.Key]})");
                Changed();
                return;
            }
            try {
                var clip = AudioClip.Create(d.Name, d.Samples.Length / d.Channels, d.Channels, d.Rate, false);
                clip.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
                clip.SetData(d.Samples, 0);
                clips[d.Key] = clip;
                Touch(d.Key);
                Evict();
                Log($"{d.Key} ready ({d.Samples.Length / d.Channels / (float)d.Rate:F1} s, {d.Channels} ch, {d.Rate} Hz)");
            } catch (Exception e) {
                failed[d.Key] = e.GetType().Name;
                Warn($"{d.Key}: clip failed: {e.Message}");
            }
            Changed();
        }

        private static void Touch(string key) { lru.Remove(key); lru.Add(key); }

        /// A deleted version: its clip goes unless it is playing right now (then the cache drops it later).
        internal static void Forget(string key) {
            failed.Remove(key);
            if (key == PreviewKey) StopPreview();
            if (key == current?.Key || key == upcoming?.Key || key == MenuMusic.CurrentKey || key == LobbyMusic.CurrentKey
                || key == MeetingMusic.CurrentKey || ReactorTrack.Uses(key)) return;
            lru.Remove(key);
            if (clips.TryGetValue(key, out var c)) {
                clips.Remove(key);
                try { if (c != null) UnityEngine.Object.Destroy(c); } catch { }
            }
        }

        // Keeps the newest few. Never drops a clip that is playing right now (draft or preview).
        private static void Evict() {
            for (int i = 0; lru.Count > CacheSize && i < lru.Count;) {
                string key = lru[i];
                if (key == current?.Key || key == upcoming?.Key || key == PreviewKey || key == MenuMusic.CurrentKey || key == LobbyMusic.CurrentKey || key == MeetingMusic.CurrentKey || ReactorTrack.Uses(key)) { i++; continue; }
                lru.RemoveAt(i);
                if (clips.TryGetValue(key, out var c)) {
                    clips.Remove(key);
                    try { if (c != null) UnityEngine.Object.Destroy(c); } catch { }
                }
            }
        }

        /// Forget failures and rescan files (the panel calls this when it opens).
        internal static void Refresh() {
            if (Directory.Exists(Folder)) EnsureFolder();   // refreshes an outdated README
            failed.Clear();
            MusicLibrary.Rescan();
            Files(true);
            Changed();
        }

        // ====================================================================
        // The draft
        // ====================================================================
        private static FieldInfo runningField;
        private static bool runningResolved;

        internal static bool DraftRunning() {
            try {
                if (!runningResolved) {
                    runningResolved = true;
                    var type = TorType.Get("TheOtherRoles.Modules.RoleDraft");
                    runningField = type != null ? AccessTools.Field(type, "isRunning") : null;
                    if (runningField == null) Warn("RoleDraft.isRunning not found, draft music stays TOR's.");
                }
                return runningField != null && (bool)runningField.GetValue(null);
            } catch { return false; }
        }

        internal static AudioClip TorClip() {
            try { return TheOtherRoles.SoundEffectsManager.get("draft"); } catch { return null; }
        }

        private static Track upcoming;   // decided while the lobby is open, so it is decoded in time
        private static string decidedFor; // the choice upcoming was decided for (null: not decided yet)
        private static Track current;    // what the running draft plays (null: TOR's own)

        // upcoming may legitimately be null (TOR's track drawn from the playlist), so "decided" is tracked
        // separately; otherwise the lobby would draw again every frame.
        private static void Undecide() { upcoming = null; decidedFor = null; }
        private static bool wasRunning;
        private static IntroCutscene intro;
        private static float introSearchAt;
        internal static string LastSwap { get; private set; }   // diagnostics: name of the clip the draft source got

        internal static Track Upcoming => upcoming;
        internal static Track Current => current;

        // TOR clears isRunning only at the end of its coroutine, so a draft cut short (leaving the game)
        // leaves it set; the intro screen has to exist as well.
        internal static bool DiagForceDraft;   // autotest: freeplay has no draft screen

        private static bool DraftOnScreen() {
            if (DiagForceDraft) return true;
            if (!DraftRunning()) return false;
            return IntroScreen.Exists;
        }

        // SoundManager keys the source by TOR's clip and later only calls Play() on it again, so the
        // source has to get TOR's clip back, or the next draft (or "Original") would play ours.
        private static void RestoreTorSource() {
            try {
                var tor = TorClip();
                var sm = SoundManager.instance;
                if (tor == null || sm == null || !sm.allSources.TryGetValue(tor, out var src) || src == null) return;
                if (src.clip == tor) return;
                src.Stop();
                src.clip = tor;
            } catch { }
        }

        internal static void Update() {
            Pump();
            try { MenuMusic.Tick(); } catch (Exception e) { Warn("menu music: " + e.Message); }
            try { LobbyMusic.Tick(); } catch (Exception e) { Warn("lobby music: " + e.Message); }
            try { MeetingMusic.Tick(); } catch (Exception e) { Warn("meeting music: " + e.Message); }
            try { ReactorTrack.Tick(); } catch (Exception e) { Warn("reactor music: " + e.Message); }

            bool running = DraftOnScreen();
            if (!running) {
                if (wasRunning) {
                    wasRunning = false;
                    Log($"draft over ({(current != null ? current.Key : Tor)})");
                    RestoreTorSource();
                    current = null; intro = null;
                }
                // lobby: decide and decode the next draft's track ahead of time
                if (LobbyScreen.Exists) {
                    string sel = Selected;
                    if (sel != Tor && decidedFor != sel) { upcoming = Resolve(); decidedFor = sel; }
                    if (upcoming != null) Get(upcoming);
                }
                StopPreviewOutsideLobby();
                return;
            }

            if (!wasRunning) {
                wasRunning = true;
                StopPreview();
                current = decidedFor == Selected ? upcoming : Resolve();
                Undecide();
                Log($"draft started, music: {(current != null ? current.Key : Tor)}");
            }
            ApplyDraftVolume();
            if (current == null) { RestoreIfOurs(); return; }
            var clip = Get(current);
            if (clip == null) return;   // still decoding: TOR's track plays until the clip is ready

            var tor = TorClip();
            var sm = SoundManager.instance;
            if (tor == null || sm == null) return;
            AudioSource src = null;
            try { if (!sm.allSources.TryGetValue(tor, out src)) src = null; } catch { src = null; }
            if (src == null || src.clip == clip) return;
            if (!src.isPlaying && src.clip == tor) return;   // TOR did not start its track (sounds off): stay silent too
            src.clip = clip;
            src.loop = true;
            src.time = 0f;
            src.Play();
            LastSwap = clip.name;
            Log($"draft music -> {current.Title}");
        }

        // TOR starts its track at volume 1; the draft slot's level scales it, ours and TOR's alike.
        private static void ApplyDraftVolume() {
            try {
                var tor = TorClip();
                var sm = SoundManager.instance;
                if (tor == null || sm == null || !sm.allSources.TryGetValue(tor, out var src) || src == null) return;
                float v = Mathf.Clamp01(Slot.Level);              // TOR plays at 1: above 100 % is not possible here
                if (!Mathf.Approximately(src.volume, v)) src.volume = v;
            } catch { }
        }

        // "Original" chosen, but the source still carries one of ours (a draft cut short before the
        // restore ran): put TOR's clip back on the playing source, once.
        private static void RestoreIfOurs() {
            try {
                var tor = TorClip();
                var sm = SoundManager.instance;
                if (tor == null || sm == null || !sm.allSources.TryGetValue(tor, out var src) || src == null) return;
                if (src.clip == tor || !src.isPlaying) return;
                src.clip = tor;
                src.time = 0f;
                src.Play();
            } catch { }
        }

        // After TOR's coroutine wrote this frame's title: replace the credit of TOR's track.
        internal static void LateUpdate() {
            try { MenuMusic.Swap.LateTick(); LobbyMusic.Swap.LateTick(); } catch { }
            if (current == null || !wasRunning) return;
            try {
                if (intro == null) {
                    if (Time.realtimeSinceStartup < introSearchAt) return;
                    introSearchAt = Time.realtimeSinceStartup + 0.5f;
                    intro = IntroScreen.Instance;
                    if (intro == null) return;
                }
                var title = intro.TeamTitle;
                if (title == null) return;
                string s = title.text;
                int i = s.IndexOf(TorCreditStart, StringComparison.Ordinal);
                if (i < 0) return;
                int end = s.IndexOf('♫', i);
                string credit = current.IsFile && (!current.External || string.IsNullOrEmpty(current.Style))
                    ? current.Title : $"{current.Title} ({current.Style})";
                title.text = s.Substring(0, i) + credit + (end >= 0 ? " " + s.Substring(end) : "");
            } catch { intro = null; }
        }

        private const string TorCreditStart = "Ultimate Superhero 3";

        // ====================================================================
        // Preview (panel): plays a track on the music channel until stopped
        // ====================================================================
        internal static string PreviewKey { get; private set; }
        private static AudioClip previewClip;
        internal static AudioClip PreviewClip => previewClip;
        private static string previewWanted;

        internal static void TogglePreview(string key) {
            if (PreviewPlaying(key) || PreviewPending(key)) { StopPreview(); return; }
            StopPreview();
            // a kind plays its first playable version; the panel shows it under the kind's row
            if (MusicLibrary.KindOf(key) != null) {
                var v = Peek(key);
                if (v == null) return;
                key = v.Key;
            }
            previewWanted = key;
            Changed();
            TryStartPreview();
        }

        private static void TryStartPreview() {
            if (previewWanted == null) return;
            AudioClip clip;
            if (previewWanted == Tor) clip = TorClip();
            else {
                var t = Find(previewWanted);
                if (t == null || Failed(t.Key)) { previewWanted = null; Changed(); return; }
                clip = Get(t);
            }
            if (clip == null) return;   // decoding; Update retries
            try {
                var sm = SoundManager.instance;
                if (sm == null) return;
                sm.PlaySound(clip, true, 1f, sm.MusicChannel);
                previewClip = clip;
                PreviewKey = previewWanted;
            } catch (Exception e) { Warn("preview: " + e.Message); }
            previewWanted = null;
            Changed();
        }

        internal static void StopPreview() {
            previewWanted = null;
            if (previewClip != null) {
                try { SoundManager.instance?.StopSound(previewClip); } catch { }
                previewClip = null;
            }
            if (PreviewKey != null) { PreviewKey = null; Changed(); }
        }

        internal static bool PreviewPending(string key) => previewWanted != null && (previewWanted == key || KindKeyOf(previewWanted) == key);
        internal static bool PreviewPlaying(string key) => PreviewKey != null && (PreviewKey == key || KindKeyOf(PreviewKey) == key);

        /// The level a preview plays at: the slot of the panel's tab, so the slider is heard while dragging.
        internal static Func<float> PreviewLevel;

        private static void StopPreviewOutsideLobby() {
            if (previewWanted != null) TryStartPreview();
            if (previewClip != null && PreviewLevel != null) {
                try {
                    if (SoundManager.instance.allSources.TryGetValue(previewClip, out var ps) && ps != null) ps.volume = PreviewLevel();
                } catch { }
            }
            if ((PreviewKey != null || previewWanted != null) && !LobbyScreen.Exists && !DraftMusicUI.DiagActive
                && !DraftMusicUI.OpenOutsideLobby) StopPreview();
        }
    }

    /// Drives DraftMusic every frame. A component of its own, not a Harmony postfix, so it also runs
    /// in the lobby and no other mod's throwing patch can keep it from running.
    public class DraftMusicDriver : MonoBehaviour {
        public DraftMusicDriver(IntPtr ptr) : base(ptr) { }

        /// The persistent driver object; autotests run coroutines on it that must survive scene changes.
        public static DraftMusicDriver Instance { get; private set; }
        public void Awake() => Instance = this;

        public void Update() {
            try { DraftMusic.Update(); }
            catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning("[DraftMusic] " + e.Message); }
        }

        public void LateUpdate() => DraftMusic.LateUpdate();
    }
}
