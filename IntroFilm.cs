// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * IntroFilm - the introduction film "Welcome to the Collective", played in the main menu on the first start with the
 * mods (once per player, local, never synchronised) and again from the main-menu button "Intro".
 *
 * The film is a folder of chapter files (Assets\Cinematics\intro_unknown\make_film_pack.py), by default
 * BepInEx\intro-film:
 *   film.json   chapters in order: id, key, requires (BepInEx GUID of the mod the chapter is about, empty = always),
 *               video, subtitles, seconds
 *   c<N>.mp4    1080p24 video + two AAC tracks (0 = English, 1 = German: voice and sound effects mixed)
 *   c<N>.json   subtitles, seconds from the chapter start: s_de, e_de, s_en, e_en, who, de, en
 * This file knows nothing about the chapters' content; a chapter whose mod is not loaded (or whose files are missing)
 * is left out without a trace. Further film.<name>.json files in the folder add chapters and films (merged by id).
 *
 * Download: the film is not in the DLL. A source is a small manifest.json in a public repository of DaUnknown-0 that
 * names the files (name, url, size, sha256); the files are release attachments. Forgotten Fixes has its own source,
 * another mod adds one with "public static class IntroFilmSource { public const string Manifest = ...; }" in the
 * namespace of its plugin class. On the first start everything loads in the background and the film plays once it is
 * there. "Delete film" in the Mod Manager removes the files; after that nothing loads until the player presses the
 * film button in the main menu.
 *
 * Playback: Unity's VideoPlayer (VideoModule) renders onto the far plane of an own camera that draws last and clears
 * the screen black (same camera trick as AtlasCinematic: other cameras' UI cannot show through). Subtitles in the game
 * language sit in the lower sixth. Space / Enter / right arrow: next chapter; Esc: end the film. While it plays, the
 * main menu's buttons are switched off so clicks do not land behind the film.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace UsefulTORStuff {
    public static class IntroFilm {
        private const string LogPrefix = "[IntroFilm]";

        private static ConfigEntry<bool> seen, deleted;
        private static ConfigEntry<string> folder;
        private static bool playing;
        internal static bool IsPlaying => playing;

        internal static void Bind(ConfigFile cfg) {
            seen = cfg.Bind("IntroFilm", "Seen", false, "The introduction film has been shown once (set to false to see it again on the next start).");
            folder = cfg.Bind("IntroFilm", "Folder", "", "Folder of the film (film.json + chapter files). Empty = BepInEx\\intro-film.");
            deleted = cfg.Bind("IntroFilm", "Deleted", false, "The film was deleted in the Mod Manager; it loads again only when you press the film button in the main menu.");
        }

        private static string Folder => string.IsNullOrWhiteSpace(folder?.Value)
            ? Path.Combine(BepInEx.Paths.BepInExRootPath, "intro-film") : folder.Value;

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo($"{LogPrefix} {s}");
        private static void Warn(string s) => UsefulTORStuffPlugin.Logger?.LogWarning($"{LogPrefix} {s}");

        // ---- film description ----------------------------------------------------------------------

        private sealed class Line { public float SDe, EDe, SEn, EEn; public string Who = "", De = "", En = ""; }
        private sealed class Chapter { public int Id; public bool Available; public float Seconds; public string Key = "", Video = ""; public List<Line> Lines = new(); }
        private sealed class FilmFile { public string File = ""; public List<int> Ids = new(); }

        /// <summary>film.json first, then film.*.json (chapters and films that other sources add).</summary>
        private static List<string> Descriptions() {
            var list = new List<string>();
            try {
                string dir = Folder;
                if (!Directory.Exists(dir)) return list;
                string main = Path.Combine(dir, "film.json");
                if (File.Exists(main)) list.Add(main);
                var extra = new List<string>(Directory.GetFiles(dir, "film.*.json"));
                extra.Sort(StringComparer.OrdinalIgnoreCase);
                list.AddRange(extra);
            } catch (Exception e) { Warn($"film folder unreadable: {e.Message}"); }
            return list;
        }

        /// <summary>All chapters in film order; Available = its mod is loaded here. Video is set when its own file exists.</summary>
        private static List<Chapter> LoadChapters() {
            var byId = new SortedDictionary<int, Chapter>();
            foreach (var path in Descriptions()) {
                try { ReadChapters(path, byId); }
                catch (Exception e) { Warn($"{Path.GetFileName(path)} unreadable: {e.Message}"); }
            }
            return new List<Chapter>(byId.Values);
        }

        private static void ReadChapters(string path, SortedDictionary<int, Chapter> byId) {
            string dir = Path.GetDirectoryName(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("chapters", out var chapters)) return;
            {
                foreach (var ch in chapters.EnumerateArray()) {
                    string requires = ch.TryGetProperty("requires", out var r) ? r.GetString() ?? "" : "";
                    bool available = requires.Length == 0 || BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.ContainsKey(requires);
                    string video = ch.TryGetProperty("video", out var v) ? Path.Combine(dir, v.GetString() ?? "") : "";
                    var c = new Chapter {
                        Id = ch.GetProperty("id").GetInt32(), Key = ch.GetProperty("key").GetString() ?? "", Available = available,
                        Seconds = ch.TryGetProperty("seconds", out var sec) ? (float)sec.GetDouble() : 0f,
                        Video = video.Length > 0 && File.Exists(video) ? video : "",
                    };
                    string subs = ch.TryGetProperty("subtitles", out var s) ? Path.Combine(dir, s.GetString() ?? "") : null;
                    if (subs != null && File.Exists(subs)) {
                        using var sd = JsonDocument.Parse(File.ReadAllText(subs));
                        foreach (var l in sd.RootElement.EnumerateArray())
                            c.Lines.Add(new Line {
                                SDe = (float)l.GetProperty("s_de").GetDouble(), EDe = (float)l.GetProperty("e_de").GetDouble(),
                                SEn = (float)l.GetProperty("s_en").GetDouble(), EEn = (float)l.GetProperty("e_en").GetDouble(),
                                Who = l.GetProperty("who").GetString() ?? "", De = l.GetProperty("de").GetString() ?? "", En = l.GetProperty("en").GetString() ?? "",
                            });
                    }
                    byId[c.Id] = c;
                }
            }
        }

        /// <summary>Whole-film files (film.json "films"): one continuous video for a set of chapters.</summary>
        private static List<FilmFile> LoadFilms() {
            var list = new List<FilmFile>();
            foreach (var path in Descriptions()) {
                try {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (!doc.RootElement.TryGetProperty("films", out var films)) continue;
                    foreach (var f in films.EnumerateArray()) {
                        var ff = new FilmFile { File = Path.Combine(Folder, f.GetProperty("file").GetString() ?? "") };
                        foreach (var id in f.GetProperty("chapters").EnumerateArray()) ff.Ids.Add(id.GetInt32());
                        if (File.Exists(ff.File)) list.Add(ff);
                    }
                } catch (Exception e) { Warn($"film list in {Path.GetFileName(path)} unreadable: {e.Message}"); }
            }
            return list;
        }

        private static bool German() {
            try { return AmongUs.Data.DataManager.Settings.Language.CurrentLanguage == SupportedLangs.German; } catch { return false; }
        }

        // ---- main menu -------------------------------------------------------------------------------

        private static bool checkedThisRun;

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class MainMenuPatch {
            public static void Postfix(MainMenuManager __instance) {
                try {
                    if (playing) return;
                    BuildButton(__instance);
                    // once per game start: fetch what is missing or outdated, unless the player deleted the film
                    if (!checkedThisRun && deleted != null && !deleted.Value) { checkedThisRun = true; StartDownload(); }
                    if (seen != null && !seen.Value && (Playable() || syncing)) {
                        Log("first start: introduction scheduled after main menu initialisation");
                        BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, AutoPlay(__instance));
                    }
                } catch (Exception e) { Warn($"main menu hook failed: {e.Message}"); }
            }
        }

        private static IEnumerator AutoPlay(MainMenuManager menu) {
            // TOR's first hat lookup and login finish after MainMenu.Start. Playing before those
            // jobs complete freezes the movie during their synchronous work on Unity's main thread.
            float deadline = Time.realtimeSinceStartup + 70f;
            while (menu != null && !StartupTiming.Ready && Time.realtimeSinceStartup < deadline)
                yield return null;
            // a first start without the film: it plays once the background download is through
            while (menu != null && syncing) yield return null;
            if (menu == null || playing || seen == null || seen.Value || !Playable()) yield break;
            Log("startup settled: starting automatic introduction");
            var playback = Play(true);
            while (playback.MoveNext()) yield return playback.Current;
        }

        // Same way the rejoin and update buttons are built: clone ExitGameButton, move it via AspectPosition. The column
        // x = 0.458 is full (updaters, rejoin at 0.34); the film button sits under the Mod Manager button.
        private static void BuildButton(MainMenuManager menu) {
            var template = GameObject.Find("ExitGameButton");
            if (!template) return;
            var button = Object.Instantiate(template, null);
            button.name = "UTSIntroFilmButton";
            float y = Mathf.Max(0.05f, (UsefulTORStuffPlugin.ModManagerButtonY?.Value ?? 0.21f) - 0.085f);
            button.GetComponent<AspectPosition>().anchorPoint = new Vector2(UsefulTORStuffPlugin.ModManagerButtonX?.Value ?? 0.8f, y);
            var passive = button.GetComponent<PassiveButton>();
            passive.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            var text = button.transform.GetComponentInChildren<TMP_Text>();
            passive.OnClick.AddListener((Action)(() => {
                if (playing || clickBusy) return;
                // the film here: play it; not here (deleted, or never loaded): load it now, then play
                if (Playable() && !syncing) BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(menu, Play(false));
                else BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(menu, LoadThenPlay(text));
            }));
            // Among Us rewrites a cloned button's text on its first frame; set ours one lerp later.
            menu.StartCoroutine(Effects.Lerp(0.1f, (Action<float>)(p => text.SetText(ButtonLabel()))));
        }

        private static bool clickBusy;
        private static string ButtonLabel() => German() ? "Einführungsfilm" : "Intro film";

        private static IEnumerator LoadThenPlay(TMP_Text text) {
            clickBusy = true;
            try {
                if (deleted != null && deleted.Value) deleted.Value = false;
                if (!syncing) StartDownload();
                while (syncing) {
                    if (text != null) text.SetText(German() ? $"Lädt … {Mathf.RoundToInt(Progress * 100f)} %" : $"Loading … {Mathf.RoundToInt(Progress * 100f)} %");
                    yield return null;
                }
                if (Playable()) {
                    if (text != null) text.SetText(ButtonLabel());
                    var playback = Play(false);
                    while (playback.MoveNext()) yield return playback.Current;
                    yield break;
                }
                if (text != null) text.SetText(German() ? "Laden fehlgeschlagen" : "Download failed");
                float until = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < until) yield return null;
                if (text != null) text.SetText(ButtonLabel());
            } finally { clickBusy = false; }
        }

        /// <summary>Something can be shown: a film file or chapter file for at least one chapter that plays here.</summary>
        private static bool Playable() {
            var all = LoadChapters();
            var available = all.FindAll(c => c.Available);
            if (available.Count == 0) return false;
            if (available.Exists(c => c.Video.Length > 0)) return true;
            foreach (var f in LoadFilms()) if (available.Exists(c => f.Ids.Contains(c.Id))) return true;
            return false;
        }

        // ---- download --------------------------------------------------------------------------------

        private const string OwnManifest = "https://raw.githubusercontent.com/DaUnknown-0/UTS-Music/main/film/manifest.json";
        private const string StampFile = "stamps.txt";

        private sealed class Remote { public string Name = "", Url = ""; public long Size; public string Sha = ""; }

        private static volatile bool syncing, deleteWhenStopped;
        private static long bytesTotal, bytesDone;
        private static CancellationTokenSource cancel;
        private static int changes;

        internal static bool Syncing => syncing;
        /// <summary>Counts every change of the film's state (download progress in whole percent, done, deleted) for the Mod Manager.</summary>
        internal static int Changes => changes + (syncing ? Mathf.RoundToInt(Progress * 100f) * 7919 : 0);
        internal static float Progress {
            get { long t = Interlocked.Read(ref bytesTotal); return t <= 0 ? 0f : Mathf.Clamp01(Interlocked.Read(ref bytesDone) / (float)t); }
        }

        /// <summary>Bytes of film files on this computer (what "Delete film" removes).</summary>
        internal static long LocalBytes() {
            long sum = 0;
            foreach (var f in OwnFiles()) try { sum += new FileInfo(f).Length; } catch { }
            return sum;
        }

        // Only what the film consists of; nothing else in the folder is touched.
        private static List<string> OwnFiles() {
            var list = new List<string>();
            try {
                if (!Directory.Exists(Folder)) return list;
                foreach (var f in Directory.GetFiles(Folder)) {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.EndsWith(".mp4") || n.EndsWith(".part") || n == StampFile
                        || (n.EndsWith(".json") && (n.StartsWith("film") || (n.Length > 1 && n[0] == 'c' && char.IsDigit(n[1])))))
                        list.Add(f);
                }
            } catch { }
            return list;
        }

        /// <summary>Mod Manager: remove the film. It loads again only from the main-menu button.</summary>
        internal static void Delete() {
            if (deleted != null) deleted.Value = true;
            if (syncing) { deleteWhenStopped = true; cancel?.Cancel(); }
            else RemoveFiles();
            Interlocked.Increment(ref changes);
        }

        private static void RemoveFiles() {
            int n = 0;
            foreach (var f in OwnFiles()) try { File.Delete(f); n++; } catch (Exception e) { Warn($"delete {Path.GetFileName(f)}: {e.Message}"); }
            Log($"film deleted ({n} files)");
        }

        private static void StartDownload() {
            if (syncing) return;
            syncing = true;
            deleteWhenStopped = false;
            Interlocked.Exchange(ref bytesTotal, 0);
            Interlocked.Exchange(ref bytesDone, 0);
            var sources = Sources();
            cancel = new CancellationTokenSource();
            var token = cancel.Token;
            Task.Run(async () => {
                try { await Download(sources, token); }
                catch (OperationCanceledException) { Log("download stopped"); }
                catch (Exception e) { Warn($"download failed: {e.Message}"); }
                finally {
                    if (deleteWhenStopped) RemoveFiles();
                    syncing = false;
                    Interlocked.Increment(ref changes);
                }
            });
        }

        // Own manifest plus IntroFilmSource.Manifest of every loaded plugin that has one (next to its plugin class).
        private static List<string> Sources() {
            var list = new List<string> { OwnManifest };
            try {
                foreach (var info in BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.Values) {
                    var type = info?.Instance?.GetType();
                    if (type == null) continue;
                    var src = type.Assembly.GetType((type.Namespace != null ? type.Namespace + "." : "") + "IntroFilmSource");
                    var field = src?.GetField("Manifest", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (field?.GetValue(null) is string url && Allowed(url) && !list.Contains(url)) list.Add(url);
                }
            } catch (Exception e) { Warn($"film sources: {e.Message}"); }
            return list;
        }

        private static bool Allowed(string url) =>
            url.StartsWith("https://raw.githubusercontent.com/DaUnknown-0/", StringComparison.Ordinal)
            || url.StartsWith("https://github.com/DaUnknown-0/", StringComparison.Ordinal);

        // Worker thread. Videos first and the descriptions last, so a half-loaded film is never offered.
        private static async Task Download(List<string> sources, CancellationToken token) {
            string dir = Folder;
            Directory.CreateDirectory(dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            var stamps = ReadStamps(dir);
            var wanted = new List<Remote>();
            foreach (var src in sources) {
                try {
                    using var doc = JsonDocument.Parse(await http.GetStringAsync(src, token));
                    foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray()) {
                        var r = new Remote {
                            Name = f.GetProperty("name").GetString() ?? "", Url = f.GetProperty("url").GetString() ?? "",
                            Size = f.GetProperty("size").GetInt64(), Sha = (f.GetProperty("sha256").GetString() ?? "").ToLowerInvariant(),
                        };
                        if (r.Name.Length == 0 || Path.GetFileName(r.Name) != r.Name || r.Name == StampFile || !Allowed(r.Url)) { Warn($"manifest entry {r.Name} refused"); continue; }
                        wanted.RemoveAll(w => w.Name == r.Name);
                        wanted.Add(r);
                    }
                } catch (Exception e) when (e is not OperationCanceledException) { Warn($"manifest {src}: {e.Message}"); }
            }
            var missing = new List<Remote>();
            foreach (var r in wanted) {
                string path = Path.Combine(dir, r.Name);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length == r.Size) {
                    if (stamps.TryGetValue(r.Name, out var st) && st == r.Sha) continue;
                    // a file from before the stamps (or copied by hand): check it once instead of loading it again
                    if (HashOf(path) == r.Sha) { stamps[r.Name] = r.Sha; continue; }
                }
                missing.Add(r);
            }
            WriteStamps(dir, stamps);
            if (missing.Count == 0) { Log($"film up to date ({wanted.Count} files)"); return; }
            missing.Sort((a, b) => (a.Name.EndsWith(".json") ? 1 : 0).CompareTo(b.Name.EndsWith(".json") ? 1 : 0));
            long total = 0;
            foreach (var r in missing) total += r.Size;
            Interlocked.Exchange(ref bytesTotal, total);
            Log($"downloading {missing.Count} film files ({total / 1048576.0:F1} MB)");
            foreach (var r in missing) {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(dir, r.Name), part = path + ".part";
                try {
                    using (var resp = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, token)) {
                        resp.EnsureSuccessStatusCode();
                        using var input = await resp.Content.ReadAsStreamAsync(token);
                        using var output = File.Create(part);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buf = new byte[1 << 16];
                        long got = 0;
                        int n;
                        while ((n = await input.ReadAsync(buf, 0, buf.Length, token)) > 0) {
                            await output.WriteAsync(buf, 0, n, token);
                            hash.AppendData(buf, 0, n);
                            got += n;
                            Interlocked.Add(ref bytesDone, n);
                        }
                        string sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                        if (got != r.Size || sha != r.Sha) throw new InvalidDataException($"{r.Name}: size {got} / checksum do not match");
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(part, path);
                    stamps[r.Name] = r.Sha;
                    WriteStamps(dir, stamps);
                    Interlocked.Increment(ref changes);
                } catch {
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    throw;
                }
            }
            Log("film downloaded");
        }

        private static string HashOf(string path) {
            try {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            } catch { return ""; }
        }

        private static Dictionary<string, string> ReadStamps(string dir) {
            var d = new Dictionary<string, string>();
            try {
                string p = Path.Combine(dir, StampFile);
                if (File.Exists(p))
                    foreach (var line in File.ReadAllLines(p)) {
                        int bar = line.IndexOf('|');
                        if (bar > 0) d[line.Substring(0, bar)] = line.Substring(bar + 1);
                    }
            } catch { }
            return d;
        }

        private static void WriteStamps(string dir, Dictionary<string, string> d) {
            try {
                var lines = new List<string>();
                foreach (var kv in d) if (File.Exists(Path.Combine(dir, kv.Key))) lines.Add(kv.Key + "|" + kv.Value);
                File.WriteAllLines(Path.Combine(dir, StampFile), lines);
            } catch { }
        }

        // ---- playback --------------------------------------------------------------------------------

        private static GameObject root, cameraGo;
        private static VideoPlayer player;
        private static RenderTexture videoTexture;
        private static GameObject videoSurface;
        private static Material videoMaterial;
        private static readonly List<RenderTexture> ownedTextures = new();
        private static readonly List<Object> ownedAssets = new();
        private static readonly List<Collider2D> mutedButtons = new();

        private static IEnumerator Play(bool firstStart) {
            if (playing) yield break;
            var all = LoadChapters();
            var available = all.FindAll(c => c.Available);
            if (available.Count == 0) { Warn("no playable chapter"); yield break; }
            // One continuous file that holds every chapter playable here (fewest extra chapters wins). Switching between
            // two video files stuttered in the game every time (User 29.10.); inside one file the cuts are smooth.
            // (A source may be missing: then the file that covers the most chapters playable here, the rest is left out.)
            FilmFile film = null;
            int best = 0;
            foreach (var f in LoadFilms()) {
                int covered = available.FindAll(c => f.Ids.Contains(c.Id)).Count;
                if (covered == 0) continue;
                if (film == null || covered > best || (covered == best && f.Ids.Count < film.Ids.Count)) { film = f; best = covered; }
            }
            var perChapter = available.FindAll(c => c.Video.Length > 0);
            if (film == null && perChapter.Count == 0) { Warn("no playable chapter"); yield break; }
            playing = true;
            try {
                var playback = film != null ? PlayFilm(film, all, firstStart) : PlayChapters(perChapter, firstStart);
                while (true) {
                    bool next;
                    try { next = playback.MoveNext(); }
                    catch (Exception e) { Warn($"playback failed: {e}"); break; }
                    if (!next) break;
                    yield return playback.Current;
                }
            } finally { Cleanup(); }
        }

        // One file, chapters back to back: subtitles by chapter-local time, Space seeks to the next playable chapter,
        // chapters of a mod that is not loaded are seeked over.
        private static IEnumerator PlayFilm(FilmFile film, List<Chapter> all, bool firstStart) {
            bool de = German();
            var byId = new Dictionary<int, Chapter>();
            foreach (var c in all) byId[c.Id] = c;
            var parts = new List<(Chapter ch, float start, float end)>();
            float acc = 0f;
            foreach (int id in film.Ids) {
                if (!byId.TryGetValue(id, out var c)) continue;
                parts.Add((c, acc, acc + c.Seconds));
                acc += c.Seconds;
            }
            Log($"playing {Path.GetFileName(film.File)}: {string.Join(", ", parts.ConvertAll(x => x.ch.Key + (x.ch.Available ? "" : " (skipped)")))}, {(de ? "de" : "en")}");
            Build(out var view, out var sub, out var subBg, out var note, out var noteBg);
            note.text = de ? "<b>[Leertaste]</b> nächstes Kapitel   <b>[Esc]</b> Film beenden"
                           : "<b>[Space]</b> next chapter   <b>[Esc]</b> end the film";
            MuteMenu(true);
            Fade(1f);

            var cur = player;
            Queue(cur, new Chapter { Video = film.File }, de);
            bool quit = false, started = false;
            float wait = Time.realtimeSinceStartup + 10f;
            while ((!cur.isPrepared || cur.width == 0 || cur.height == 0 || cur.length <= 0) && Time.realtimeSinceStartup < wait) {
                Layout(view, sub, subBg, note, noteBg);
                if (Input.GetKeyDown(KeyCode.Escape)) { quit = true; break; }
                yield return null;
            }
            if (!quit && (!cur.isPrepared || cur.length <= 0)) { Warn($"{Path.GetFileName(film.File)}: video did not prepare"); yield break; }
            if (!quit) {
                // start at the first playable chapter
                int first = parts.FindIndex(x => x.ch.Available);
                if (first > 0) cur.time = parts[first].start;
                cur.Play();
                if (videoMaterial != null) videoMaterial.mainTexture = cur.targetTexture;
            }
            Line current = null;
            int lastPart = -1;
            float lastFrameAt = Time.realtimeSinceStartup, worstFrameGap = 0f;
            int frameStalls = 0, frameSkips = 0;
            long lastVideoFrame = -1;
            float guard = Time.realtimeSinceStartup + (float)cur.length + 10f;
            while (!quit) {
                float nowFrame = Time.realtimeSinceStartup;
                float gap = nowFrame - lastFrameAt;
                lastFrameAt = nowFrame;
                worstFrameGap = Mathf.Max(worstFrameGap, gap);
                if (gap > 0.15f) frameStalls++;
                float t = (float)cur.time;
                if (cur.isPlaying && t > 0f) started = true;
                long vf = cur.frame;
                if (lastVideoFrame >= 0 && vf > lastVideoFrame + 1) frameSkips++;
                if (vf >= 0) lastVideoFrame = vf;

                int pi = parts.FindLastIndex(x => t >= x.start - 0.01f);
                if (pi < 0) pi = 0;
                if (pi != lastPart) {
                    lastPart = pi;
                    if (!parts[pi].ch.Available) {
                        // a chapter of a mod that is not here: on to the next one that is, or the end
                        int nx = parts.FindIndex(pi, x => x.ch.Available);
                        if (nx < 0) break;
                        cur.time = parts[nx].start;
                        lastPart = -1;
                        yield return null;
                        continue;
                    }
                    Log($"chapter {parts[pi].ch.Key} at {t:F2} s");
                }
                float local = t - parts[pi].start;
                Layout(view, sub, subBg, note, noteBg);
                Line line = null;
                foreach (var l in parts[pi].ch.Lines)
                    if (de ? (local >= l.SDe && local <= l.EDe) : (local >= l.SEn && local <= l.EEn)) { line = l; break; }
                if (line != current) {
                    current = line;
                    sub.text = line == null ? "" : (line.Who.Length > 0 ? $"<color=#E8B04A>{line.Who}:</color> " : "") + (de ? line.De : line.En);
                }
                if (Input.GetKeyDown(KeyCode.Escape)) { quit = true; break; }
                if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.RightArrow)) {
                    int nx = parts.FindIndex(Math.Min(pi + 1, parts.Count), x => x.ch.Available);
                    if (nx < 0) break;
                    cur.time = parts[nx].start;
                }
                if (started && !cur.isPlaying) break;                       // film over
                if (Time.realtimeSinceStartup > guard) break;
                yield return null;
            }
            try { cur.Stop(); } catch { }
            Log($"film: largest update gap {worstFrameGap * 1000:F0} ms, {frameStalls} stalls over 150 ms, video skipped frames {frameSkips} times");
            if (firstStart && seen != null && (started || quit)) seen.Value = true;
            Log(quit ? "film ended by the player" : "film finished");
        }

        private static IEnumerator PlayChapters(List<Chapter> chapters, bool firstStart) {
            bool de = German();
            Log($"playing {chapters.Count} chapter(s) ({string.Join(", ", chapters.ConvertAll(c => c.Key))}), {(de ? "de" : "en")}");
            Build(out var view, out var sub, out var subBg, out var note, out var noteBg);
            note.text = de ? "<b>[Leertaste]</b> nächstes Kapitel   <b>[Esc]</b> Film beenden"
                           : "<b>[Space]</b> next chapter   <b>[Esc]</b> end the film";
            MuteMenu(true);

            bool quit = false;
            int played = 0;
            // Two players take turns: the next chapter is opened and decoded while the current one runs, so the cut
            // does not wait for Media Foundation (User 29.10.: lag spike between chapter 0 and 1).
            var players = new[] { player, MakePlayer(cameraGo) };
            Queue(players[0], chapters[0], de);
            for (int i = 0; i < chapters.Count && !quit; i++) {
                var ch = chapters[i];
                var cur = players[i % 2];
                var nxt = players[(i + 1) % 2];
                bool queued = false;
                float wait = Time.realtimeSinceStartup + 10f;
                // isPrepared can turn true before Media Foundation publishes stream metadata.
                // Wait for dimensions and duration too, instead of instantly skipping the chapter.
                while ((!cur.isPrepared || cur.width == 0 || cur.height == 0 || cur.length <= 0)
                       && Time.realtimeSinceStartup < wait) {
                    Layout(view, sub, subBg, note, noteBg);
                    if (Input.GetKeyDown(KeyCode.Escape)) { quit = true; break; }
                    yield return null;
                }
                if (quit) break;
                if (!cur.isPrepared || cur.width == 0 || cur.height == 0 || cur.length <= 0) {
                    Warn($"chapter {ch.Key}: video did not prepare, skipped");
                    try { cur.Stop(); } catch { }
                    if (i + 1 < chapters.Count) Queue(nxt, chapters[i + 1], de);
                    continue;
                }
                Log($"chapter {ch.Key}: prepared, {cur.audioTrackCount} audio tracks, selected {(de ? "de" : "en")}");
                float playAt = Time.realtimeSinceStartup;
                Fade(0f);                                   // black until this chapter's first frame is really there
                cur.Play();
                if (videoMaterial != null) videoMaterial.mainTexture = cur.targetTexture;
                long lastVideoFrame = -1;
                int frameSkips = 0, worstSkip = 0;
                float firstFrameAfter = -1f;
                Line current = null;
                bool started = false;
                float diagnosticAt = Time.realtimeSinceStartup + 3f;
                float lastFrameAt = Time.realtimeSinceStartup, worstFrameGap = 0f;
                int frameStalls = 0;
                float guard = Time.realtimeSinceStartup + (float)cur.length + 5f;
                while (true) {
                    float nowFrame = Time.realtimeSinceStartup;
                    float gap = nowFrame - lastFrameAt;
                    lastFrameAt = nowFrame;
                    worstFrameGap = Mathf.Max(worstFrameGap, gap);
                    if (gap > 0.15f) frameStalls++;
                    float t = (float)cur.time;
                    if (Time.realtimeSinceStartup >= diagnosticAt) {
                        Log($"chapter {ch.Key}: time={t:F2}, frame={cur.frame}, playing={cur.isPlaying}, speed={cur.playbackSpeed}, size={cur.width}x{cur.height}, length={cur.length:F2}");
                        diagnosticAt = float.PositiveInfinity;
                    }
                    // isPlaying alone was true even for failed/empty files on this backend.
                    // Only advancing, valid video counts as having actually shown the film.
                    if (cur.isPlaying && t > 0f && !started) { started = true; played++; firstFrameAfter = Time.realtimeSinceStartup - playAt; }
                    // cuts go through black: fade in after the first frame, fade out before the end (or the skip)
                    if (started) Fade(Mathf.Min(Mathf.Clamp01(t / FadeSeconds), Mathf.Clamp01(((float)cur.length - t) / FadeSeconds)));
                    long vf = cur.frame;
                    if (lastVideoFrame >= 0 && vf > lastVideoFrame + 1) { frameSkips++; worstSkip = Math.Max(worstSkip, (int)(vf - lastVideoFrame - 1)); }
                    if (vf >= 0) lastVideoFrame = vf;
                    // open the next chapter early (a second after this one started, well before its end)
                    if (!queued && started && t > 1f && i + 1 < chapters.Count) { Queue(nxt, chapters[i + 1], de); queued = true; }
                    Layout(view, sub, subBg, note, noteBg);
                    Line line = null;
                    foreach (var l in ch.Lines)
                        if (de ? (t >= l.SDe && t <= l.EDe) : (t >= l.SEn && t <= l.EEn)) { line = l; break; }
                    if (line != current) {
                        current = line;
                        sub.text = line == null ? "" : (line.Who.Length > 0 ? $"<color=#E8B04A>{line.Who}:</color> " : "") + (de ? line.De : line.En);
                    }
                    if (Input.GetKeyDown(KeyCode.Escape)) { quit = true; break; }
                    if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.RightArrow)) break;
                    if (started && !cur.isPlaying) break;                       // chapter over
                    if (Time.realtimeSinceStartup > guard) break;
                    yield return null;
                }
                Fade(0f);
                if (!queued && !quit && i + 1 < chapters.Count) Queue(nxt, chapters[i + 1], de);   // skipped early
                try { cur.Stop(); } catch { }
                Log($"chapter {ch.Key}: largest update gap {worstFrameGap * 1000:F0} ms, {frameStalls} stalls over 150 ms; "
                    + $"first frame {firstFrameAfter * 1000:F0} ms after Play, video skipped frames {frameSkips} times (worst {worstSkip})");
                sub.text = "";
            }
            if (firstStart && seen != null && (played > 0 || quit)) seen.Value = true;
            Log(quit ? "film ended by the player" : "film finished");
        }

        private const float FadeSeconds = 0.33f;   // 8 frames at 24 fps, as the film's manifest asks for

        private static void Fade(float a) {
            if (videoMaterial != null) videoMaterial.color = new Color(a, a, a, 1f);
        }

        // Sets the chapter's file and audio track and starts opening it; Play() comes later.
        private static void Queue(VideoPlayer p, Chapter ch, bool de) {
            // Windows Media Foundation treats %20 in file URLs literally in this Unity build.
            // Pass the local filesystem path so directories containing spaces remain readable.
            p.url = Path.GetFullPath(ch.Video);
            // tracks: 0 = English, 1 = German
            ushort track = (ushort)(de ? 1 : 0);
            try {
                // The generated property setter references a nonexistent String.Format overload.
                // Keep its bounds check here and use the verified native entry point directly.
                if (VideoPlayer.controlledAudioTrackMaxCount < 2)
                    throw new InvalidOperationException("Video backend cannot control two audio tracks.");
                p.SetControlledAudioTrackCount(2);
                p.EnableAudioTrack(0, !de);
                p.EnableAudioTrack(1, de);
                p.SetDirectAudioVolume(track, Volume());
            } catch (Exception e) { Warn($"audio track setup: {e.Message}"); }
            p.Prepare();
        }

        private static VideoPlayer MakePlayer(GameObject host) {
            var p = host.AddComponent<VideoPlayer>();
            p.playOnAwake = false;
            p.isLooping = false;
            p.source = VideoSource.Url;
            p.renderMode = VideoRenderMode.RenderTexture;
            // own texture per player: the one being prepared must not draw its first frame over the running chapter
            var rt = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            ownedTextures.Add(rt);
            p.targetTexture = rt;
            p.aspectRatio = VideoAspectRatio.FitInside;
            p.audioOutputMode = VideoAudioOutputMode.Direct;
            p.skipOnDrop = true;
            return p;
        }

        private static float Volume() {
            try { return Mathf.Clamp01(AmongUs.Data.DataManager.Settings.Audio.SfxVolume); } catch { return 1f; }
        }

        private static void Build(out Camera view, out TextMeshPro sub, out GameObject subBg, out TextMeshPro note, out GameObject noteBg) {
            const int layer = 5;
            cameraGo = new GameObject("UTS_IntroFilmCamera");
            Object.DontDestroyOnLoad(cameraGo);
            cameraGo.transform.position = new Vector3(6000f, 6000f, -10f);
            view = cameraGo.AddComponent<Camera>();
            view.orthographic = true;
            view.orthographicSize = 3f;
            view.nearClipPlane = 0.1f;
            view.farClipPlane = 50f;
            view.clearFlags = CameraClearFlags.SolidColor;
            view.backgroundColor = Color.black;
            view.cullingMask = 1 << layer;
            view.depth = 1000f;

            player = MakePlayer(cameraGo);
            videoTexture = player.targetTexture;

            root = new GameObject("UTS_IntroFilm") { layer = layer };
            Object.DontDestroyOnLoad(root);
            root.transform.position = new Vector3(6000f, 6000f, 0f);
            videoSurface = GameObject.CreatePrimitive(PrimitiveType.Quad);
            videoSurface.name = "video";
            videoSurface.layer = layer;
            videoSurface.transform.SetParent(root.transform, false);
            videoSurface.transform.localPosition = new Vector3(0f, 0f, 1f);
            Object.Destroy(videoSurface.GetComponent<Collider>());
            videoMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = videoTexture };
            videoSurface.GetComponent<MeshRenderer>().sharedMaterial = videoMaterial;
            ownedAssets.Add(videoMaterial);
            subBg = Quad("subtitle_bg", new Color(0f, 0f, 0f, 0.55f), 2, layer);
            noteBg = Quad("note_bg", new Color(0f, 0f, 0f, 0.45f), 2, layer);
            sub = Text("subtitle", 3, layer);
            note = Text("note", 3, layer);
            note.alignment = TextAlignmentOptions.BottomRight;
        }

        // 16:9 picture fitted into the camera; subtitles in its lower sixth, the key hint at the top right
        private static void Layout(Camera view, TextMeshPro sub, GameObject subBg, TextMeshPro note, GameObject noteBg) {
            float ch = view.orthographicSize * 2f, cw = ch * view.aspect;
            float picW = Mathf.Min(cw, ch * 16f / 9f), picH = picW * 9f / 16f;
            if (videoSurface != null) videoSurface.transform.localScale = new Vector3(picW, picH, 1f);
            sub.rectTransform.sizeDelta = new Vector2(picW * 0.86f, picH / 6f);
            sub.transform.localPosition = new Vector3(0f, -picH / 2f + picH / 12f, -1f);
            sub.fontSize = picH * 0.42f;
            note.rectTransform.sizeDelta = new Vector2(picW * 0.6f, picH * 0.06f);
            note.transform.localPosition = new Vector3(picW / 2f - picW * 0.31f, picH / 2f - picH * 0.04f, -1f);
            note.fontSize = picH * 0.22f;
            FitBackground(subBg, sub, picW * 0.86f, picH * 0.012f);
            FitBackground(noteBg, note, picW * 0.6f, picH * 0.008f);
        }

        private static void FitBackground(GameObject bg, TextMeshPro t, float maxW, float pad) {
            try {
                if (string.IsNullOrEmpty(t.text)) { if (bg.activeSelf) bg.SetActive(false); return; }
                var size = t.GetPreferredValues(t.text, maxW, 0f);
                float w = Mathf.Min(size.x, maxW) + pad * 4f, h = size.y + pad * 2f;
                var p = t.transform.localPosition;
                bool right = t.alignment == TextAlignmentOptions.BottomRight;
                float x = right ? p.x + t.rectTransform.sizeDelta.x / 2f - w / 2f + pad * 2f : p.x;
                float y = right ? p.y - t.rectTransform.sizeDelta.y / 2f + h / 2f - pad : p.y;
                bg.transform.localPosition = new Vector3(x, y, p.z + 0.5f);
                bg.transform.localScale = new Vector3(w, h, 1f);
                if (!bg.activeSelf) bg.SetActive(true);
            } catch { }
        }

        private static GameObject Quad(string name, Color c, int order, int layer) {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            var tex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            ownedAssets.Add(sr.sprite);
            ownedAssets.Add(tex);
            sr.color = c;
            sr.sortingOrder = order;
            return go;
        }

        private static TextMeshPro Text(string name, int order, int layer) {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(root.transform, false);
            var t = go.AddComponent<TextMeshPro>();
            try {
                var src = Object.FindObjectOfType<TextMeshPro>();
                if (src != null) { t.font = src.font; t.fontSharedMaterial = src.fontSharedMaterial; }
            } catch { }
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = true;
            t.color = Color.white;
            t.text = "";
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = order;
            return t;
        }

        // The main menu's buttons react to clicks through the film otherwise (they raycast with the menu camera).
        private static void MuteMenu(bool mute) {
            try {
                if (mute) {
                    mutedButtons.Clear();
                    foreach (var b in Object.FindObjectsOfType<PassiveButton>()) {
                        if (b == null) continue;
                        foreach (var col in b.GetComponents<Collider2D>())
                            if (col != null && col.enabled) { col.enabled = false; mutedButtons.Add(col); }
                    }
                } else {
                    foreach (var col in mutedButtons) if (col != null) col.enabled = true;
                    mutedButtons.Clear();
                }
            } catch (Exception e) { Warn($"menu buttons: {e.Message}"); }
        }

        private static void Cleanup() {
            MuteMenu(false);
            try { if (cameraGo != null) foreach (var p in cameraGo.GetComponents<VideoPlayer>()) p.Stop(); } catch { }
            try { if (root != null) Object.Destroy(root); } catch { }
            try { if (cameraGo != null) Object.Destroy(cameraGo); } catch { }
            foreach (var rt in ownedTextures) try { if (rt != null) { rt.Release(); Object.Destroy(rt); } } catch { }
            ownedTextures.Clear();
            videoTexture = null; videoSurface = null; videoMaterial = null;
            foreach (var asset in ownedAssets) try { if (asset != null) Object.Destroy(asset); } catch { }
            ownedAssets.Clear();
            root = null; cameraGo = null; player = null;
            playing = false;
        }
    }
}
