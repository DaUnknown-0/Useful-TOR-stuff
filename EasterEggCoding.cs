// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * EasterEggCoding - a small easter egg for the mod's author (User 23.10.).
 *
 * If the author dies in the first round of a game (killed before the first meeting is over, or
 * voted out in that first meeting), his name is replaced by an animated "Coding..." for the rest of
 * the game: over his ghost and on his meeting plate. Voted out in that first meeting, the exile line
 * reads "<name> continues to code the mod." instead of the usual one.
 *
 * Who the author is: SHA-256 of his account's PUID (or, without one, of his friend code), so the id
 * itself is not in this public source. Every client with this mod recognises him on its own; nothing
 * is sent. Purely cosmetic: no role, no win, no vote is touched.
 *
 * "First round" ends when the first meeting's exile screen is gone (MeetingHud and ExileController
 * both gone after having been there). Names are written after TOR's own per-frame name pass
 * (HudManager.Update / MeetingHud.Update postfixes, Priority.Last); the exile line in a
 * TranslationController.GetString postfix (Priority.Last, after TOR's role line) plus completeString.
 *
 * 2026-10-24 (User: "make the animation a bit cooler"):
 *  - Build progress: the ghost's name runs through Coding... -> Compiling [=====-----] 47% -> Fixing bugs... ->
 *    Pushing vX.Y.Z... -> Released vX.Y.Z!, driven only by the game clock (no tasks, no time of death: nothing game-relevant).
 *  - Laptop: his ghost carries an open laptop with scrolling code (Resources/egg/egg_laptop_N.png, a drawn
 *    stand-in until then), little </> { } ; glyphs rise from it. Only where his ghost is visible anyway.
 *  - Terminal look: green monospace with a blinking cursor on the meeting plate and in the exile line.
 *  - Git gag: on his own screen only, a few seconds after the kill a small fake terminal types a commit.
 */

using System;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UsefulTORStuff {
    public static class EasterEggCoding {
        private static readonly string[] OwnerHashes = {
            "d10f0e308524211c45d93ec3300efca28e35488ea35598196c67b1ababb7fc19",   // PUID
            "cf2eab42fba217e7af4237f7a3bcd35402f2790602e07d92c6344096701480c6",   // friend code
        };

        private static ShipStatus game;          // the game the state below belongs to
        private static byte owner = 255;         // the author's PlayerId in this game (255 = not here)
        private static float nextOwnerLookup;
        private static bool inMeeting;
        private static int meetingsDone;
        private static bool coding;              // he died in the first round: "Coding..." from now on
        private static bool exileLine;           // the exile screen showing him right now gets our line
        private static string exileName = "";
        private static bool votedOut;            // coding because of the exile, not a kill (no git gag)
        private static float diedAt = -1f;       // Time.time when coding started
        private static float progress;           // shown build progress 0..1 (game clock only, see GameProgress)
        private static float gameStart = float.NaN;   // Time.time when this round began (NaN = no round)
        private static bool gagShown;

        internal static bool IsOwner(PlayerControl p) {
            try {
                if (p == null || p.Data == null) return false;
                if (diagForceLocal && p == PlayerControl.LocalPlayer) return true;
                return Matches(p.Data.Puid) || Matches(p.Data.FriendCode);
            } catch { return false; }
        }

        // ---- autotest (Diagnostics "EasterEgg Test", freeplay only): eject self, shot of the line, shot of the ghost ----
        internal static BepInEx.Configuration.ConfigEntry<bool> DiagTest;
        private static bool diagForceLocal;
        private static float diagStart = -1f;
        private static int diagStep;

        private static void DiagTick() {
            if (DiagTest == null || !DiagTest.Value || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
            if (diagStart < 0f) { diagStart = Time.time; diagStep = 0; return; }
            float t = Time.time - diagStart;
            var lp = PlayerControl.LocalPlayer;
            string shots = BepInEx.Paths.BepInExRootPath;
            if (diagStep == 0 && t > 10f) {
                diagStep = 1;
                bool real = Matches(lp.Data.Puid) || Matches(lp.Data.FriendCode);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: local player recognised by id: {real}");
                if (!real) diagForceLocal = true;
                owner = 255; nextOwnerLookup = 0f;
                var ship = ShipStatus.Instance;
                var ec = UnityEngine.Object.Instantiate(ship.ExileCutscenePrefab);
                ec.transform.SetParent(HudManager.Instance.transform, false);
                ec.transform.localPosition = new Vector3(0f, 0f, -60f);
                HudManager.Instance.SetHudActive(false);
                ec.BeginForGameplay(lp.Data, false);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: eject started, line '{ec.completeString}'");
            } else if (diagStep == 1 && t > 16f) {
                diagStep = 2;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, "EasterEgg_exile.png"));
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: exile shot, text '{ExileController.Instance?.Text?.text}'");
            } else if (diagStep == 2 && t > 26f) {
                diagStep = 3;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, "EasterEgg_ghost.png"));
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: ghost shot, dead {lp.Data.IsDead}, coding {coding}, name '{lp.cosmetics?.nameText?.text}'");
            } else if (diagStep == 3 && t > 28f) {
                // jump into the compile phase and play the git gag as after a kill
                diagStep = 4;
                gameStart = Time.time - 300f; votedOut = false; gagShown = false; diedAt = Time.time - 4f;
            } else if (diagStep == 4 && t > 31f) {
                diagStep = 5;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, "EasterEgg_gag.png"));
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: gag shot, progress {progress:F2} (round clock {Time.time - gameStart:F0} s, start {gameStart:F1}), laptop {(laptop != null)}, name '{lp.cosmetics?.nameText?.text}'");
            } else if (diagStep == 5 && t > 33f) {
                diagStep = 6;
                UsefulTORStuffPlugin.Logger?.LogInfo("[EasterEgg] diag: all done");
            }
        }

        private static bool Matches(string id) {
            if (string.IsNullOrEmpty(id)) return false;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(id));
            var sb = new StringBuilder(64);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            string h = sb.ToString();
            foreach (var o in OwnerHashes) if (o == h) return true;
            return false;
        }

        private static PlayerControl Owner() => owner == 255 ? null : TheOtherRoles.Helpers.playerById(owner);

        private const string Green = "#4ADE80";

        // 0-3 dots; the missing dots stay as invisible characters so the text keeps its width
        private static string Dots() {
            int n = (int)(Time.time * 2.5f) % 4;
            return new string('.', n) + (n < 3 ? "<alpha=#00>" + new string('.', 3 - n) + "<alpha=#FF>" : "");
        }

        private static string Cursor() => (int)(Time.time * 2f) % 2 == 0 ? "_" : "<alpha=#00>_<alpha=#FF>";

        // the next patch version of this mod ("1.5.5.1" -> "v1.5.6"): what the author is "pushing"
        private static string nextVersion;
        private static string NextVersion() {
            if (nextVersion != null) return nextVersion;
            try {
                var parts = UsefulTORStuffPlugin.PluginVersion.Split('.');
                nextVersion = $"v{parts[0]}.{parts[1]}.{int.Parse(parts[2]) + 1}";
            } catch { nextVersion = "v2.0.0"; }
            return nextVersion;
        }

        // Build progress from the game clock alone (10 minutes since the round began = released). Nothing
        // game-relevant may leak (User 24.10.): no tasks, and not the time since he died, which would date the kill.
        private static float GameProgress() => float.IsNaN(gameStart) ? 0f : Mathf.Clamp01((Time.time - gameStart) / 600f);

        // The build line. Short = meeting plate (narrow), long = over the ghost (with the bar).
        private static string CodingText(bool shortForm = false) {
            float p = progress;
            int pct = Mathf.Clamp(Mathf.FloorToInt(p * 100f), 0, 100);
            string body;
            if (p < 0.22f) body = "Coding" + Dots();
            else if (p < 0.62f) {
                int fill = Mathf.Clamp(Mathf.RoundToInt((p - 0.22f) / 0.40f * 6f), 0, 6);
                body = shortForm ? $"Compiling {pct}%"
                                 : $"Compiling <mspace=0.5em>[{new string('=', fill)}{new string('-', 6 - fill)}]</mspace> {pct}%";
            }
            else if (p < 0.92f) {
                int bugs = Mathf.Max(1, Mathf.CeilToInt((0.92f - p) * 40f));
                body = shortForm ? "Fixing bugs" + Dots() : $"Fixing bugs{Dots()} {bugs} left";
            }
            else if (p < 0.999f) body = "Pushing " + NextVersion() + Dots();
            else body = "Released " + NextVersion() + "!";
            return $"<color={Green}>{body}</color>";
        }

        private static string PlateText() => $"<color={Green}>&gt; </color>{CodingText(true)}<color={Green}>{Cursor()}</color>";

        private static void ResetFor(ShipStatus ship) {
            game = ship; owner = 255; nextOwnerLookup = 0f;
            inMeeting = false; meetingsDone = 0; coding = false; exileLine = false; exileName = "";
            votedOut = false; diedAt = -1f; progress = 0f; gagShown = false;
            gameStart = ship != null ? Time.time : float.NaN;
            DestroyLaptop();
            HideGag();
        }

        // ---- per frame: round tracking, death, name over the ghost ----

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix() {
                try { DiagTick(); } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[EasterEgg] diag: {e.Message}"); }
                try {
                    var ship = ShipStatus.Instance;
                    if (ship == null) { if (game != null) ResetFor(null); return; }
                    if (ship != game) ResetFor(ship);

                    // the author's PlayerId, looked up now and then (players can still be arriving at the start)
                    if (owner == 255 && Time.time >= nextOwnerLookup) {
                        nextOwnerLookup = Time.time + 2f;
                        foreach (var p in PlayerControl.AllPlayerControls)
                            if (IsOwner(p)) { owner = p.PlayerId; break; }
                    }
                    var me = Owner();
                    if (me == null || me.Data == null) return;

                    // death first, then the round count: a death on the exile screen still counts as round one
                    if (!coding && me.Data.IsDead && meetingsDone == 0) {
                        coding = true;
                        UsefulTORStuffPlugin.Logger?.LogInfo("[EasterEgg] the author died in round one: Coding...");
                    }
                    if (coding && diedAt < 0f) diedAt = Time.time;
                    if (coding) progress = GameProgress();
                    bool meeting = MeetingHud.Instance != null || ExileController.Instance != null;
                    if (inMeeting && !meeting) { meetingsDone++; exileLine = false; }
                    inMeeting = meeting;

                    if (coding && me.Data.IsDead && me.cosmetics != null && me.cosmetics.nameText != null)
                        me.cosmetics.nameText.text = CodingText();
                    try { LaptopTick(me, coding && me.Data.IsDead && !meeting); } catch (Exception e) { LogOnce("laptop", e); }
                    try { GagTick(me, meeting); } catch (Exception e) { LogOnce("gag", e); }
                    try { ExileCursorTick(); } catch { }
                } catch { }
            }
        }

        // ---- meeting plate ----

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class MeetingPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(MeetingHud __instance) {
                try {
                    if (!coding || owner == 255 || __instance == null || __instance.playerStates == null) return;
                    foreach (var pva in __instance.playerStates)
                        if (pva != null && pva.TargetPlayerId == owner && pva.NameText != null) pva.NameText.text = PlateText();
                } catch { }
            }
        }

        // ---- exile line ----

        [HarmonyPatch(typeof(ExileController), nameof(ExileController.BeginForGameplay))]
        static class ExileBeginPatch {
            // before vanilla builds its line, so the GetString postfix below already sees the flag
            public static void Prefix([HarmonyArgument(0)] NetworkedPlayerInfo exiled) {
                try {
                    exileLine = false;
                    if (exiled == null || meetingsDone != 0 || exiled.Object == null || !IsOwner(exiled.Object)) return;
                    exileLine = true;
                    exileName = exiled.PlayerName ?? "";
                    coding = true;    // voted out in round one: the name codes on as well
                    votedOut = true;
                    UsefulTORStuffPlugin.Logger?.LogInfo("[EasterEgg] the author was voted out in round one.");
                } catch { }
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix(ExileController __instance) {
                try {
                    if (!exileLine || __instance == null) return;
                    __instance.completeString = Line();
                    if (__instance.Text != null) __instance.Text.color = new Color(0.29f, 0.87f, 0.5f);   // terminal green
                } catch { }
            }
        }

        private static string Line() => $"{exileName} continues to code the mod.";

        // once vanilla has typed the whole line, a blinking cursor stays behind it
        private static void ExileCursorTick() {
            if (!exileLine) return;
            var ec = ExileController.Instance;
            if (ec == null || ec.Text == null || string.IsNullOrEmpty(ec.completeString)) return;
            string full = ec.completeString;
            string cur = ec.Text.text ?? "";
            if (cur.StartsWith(full, StringComparison.Ordinal)) ec.Text.text = full + Cursor();
        }

        private static readonly HashSet<string> logged = new();
        private static void LogOnce(string what, Exception e) {
            if (logged.Add(what)) UsefulTORStuffPlugin.Logger?.LogWarning($"[EasterEgg] {what}: {e.Message}");
        }

        // ---- laptop at the ghost ----

        private static GameObject laptop;
        private static SpriteRenderer laptopSr;
        private static Sprite[] laptopFrames;
        private static readonly List<(TMPro.TextMeshPro t, float born, float dx)> glyphs = new();
        private static float nextGlyph;
        private static readonly string[] GlyphTexts = { "</>", "{ }", ";", "#", "()", "=>", "//" };

        private static void LaptopTick(PlayerControl me, bool want) {
            if (!want || me == null || me.cosmetics == null) { DestroyLaptop(); return; }
            if (laptop == null) {
                laptopFrames ??= LoadLaptopFrames();
                laptop = new GameObject("UTS_EggLaptop") { layer = me.gameObject.layer };
                laptop.transform.SetParent(me.transform, false);
                laptopSr = laptop.AddComponent<SpriteRenderer>();
            }
            bool visible = me.Visible;
            if (laptop.activeSelf != visible) laptop.SetActive(visible);
            bool flip = false;
            try { flip = me.cosmetics.currentBodySprite.BodySprite.flipX; } catch { }
            float dir = flip ? -1f : 1f;
            float bob = Mathf.Sin(Time.time * 2.2f) * 0.025f;
            laptop.transform.localPosition = new Vector3(0.30f * dir, -0.18f + bob, -0.02f);
            laptopSr.flipX = flip;
            if (laptopFrames.Length > 0) laptopSr.sprite = laptopFrames[(int)(Time.time * 8f) % laptopFrames.Length];
            laptopSr.color = new Color(1f, 1f, 1f, 0.85f);

            // rising glyphs (only while the ghost is visible here)
            float now = Time.time;
            if (visible && now >= nextGlyph && glyphs.Count < 4 && me.cosmetics.nameText != null) {
                nextGlyph = now + UnityEngine.Random.Range(0.45f, 0.9f);
                var go = Object.Instantiate(me.cosmetics.nameText.gameObject, me.transform);
                go.name = "UTS_EggGlyph";
                var t = go.GetComponent<TMPro.TextMeshPro>();
                if (t != null) {
                    t.text = GlyphTexts[UnityEngine.Random.Range(0, GlyphTexts.Length)];
                    t.color = new Color(0.29f, 0.87f, 0.5f, 1f);
                    go.transform.localScale = Vector3.one * 0.55f;
                    glyphs.Add((t, now, UnityEngine.Random.Range(-0.12f, 0.12f)));
                } else Object.Destroy(go);
            }
            for (int i = glyphs.Count - 1; i >= 0; i--) {
                var (t, born, dx) = glyphs[i];
                float age = now - born;
                if (t == null || age > 1.3f) { if (t != null) Object.Destroy(t.gameObject); glyphs.RemoveAt(i); continue; }
                t.gameObject.SetActive(visible);
                t.transform.localPosition = new Vector3(0.30f * dir + dx, -0.05f + age * 0.42f, -0.03f);
                t.alpha = Mathf.Clamp01(1.3f - age) * 0.9f;
            }
        }

        private static void DestroyLaptop() {
            if (laptop != null) Object.Destroy(laptop);
            laptop = null; laptopSr = null;
            foreach (var g in glyphs) if (g.t != null) Object.Destroy(g.t.gameObject);
            glyphs.Clear();
        }

        // The delivered frames (Resources/egg/egg_laptop_N.png, shown 0.5 units wide), else a drawn stand-in.
        private static Sprite[] LoadLaptopFrames() {
            var list = new List<Sprite>();
            try {
                var asm = typeof(EasterEggCoding).Assembly;
                for (int i = 1; i <= 16; i++) {
                    using var st = asm.GetManifestResourceStream($"UsefulTORStuff.Resources.egg.egg_laptop_{i}.png");
                    if (st == null) break;
                    var data = new byte[st.Length];
                    int read = 0;
                    while (read < data.Length) { int n = st.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, data, false)) break;
                    Keep(tex);
                    var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width / 0.5f);
                    Keep(sp);
                    list.Add(sp);
                }
            } catch (Exception e) { LogOnce("laptop art", e); }
            if (list.Count == 0) for (int f = 0; f < 6; f++) list.Add(PaintLaptop(f));
            return list.ToArray();
        }

        private static void Keep(Object o) { if (o != null) o.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset; }

        // stand-in: open laptop, dark outline, green code lines that scroll by one line per frame
        private static Sprite PaintLaptop(int frame) {
            const int W = 64, H = 50;
            var ink = new Color32(20, 22, 28, 255);
            var shell = new Color32(150, 158, 170, 255);
            var screen = new Color32(10, 18, 14, 255);
            var code = new Color32(74, 222, 128, 255);
            var px = new Color32[W * H];
            var rnd = new System.Random(7);
            int[] lens = new int[24], indent = new int[24];
            for (int i = 0; i < lens.Length; i++) { lens[i] = rnd.Next(6, 30); indent[i] = rnd.Next(0, 3) * 3; }
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++) {
                    var c = new Color32(0, 0, 0, 0);
                    int inset = (10 - y) / 3;
                    bool keyboard = y < 10 && x >= 2 + inset && x <= W - 3 - inset;
                    bool lid = y >= 10 && y <= 47 && x >= 7 && x <= 56;
                    if (keyboard) c = (y == 0 || y == 9 || x == 2 + inset || x == W - 3 - inset) ? ink : shell;
                    if (lid) {
                        bool edge = y == 10 || y == 47 || x == 7 || x == 56;
                        bool bezel = y <= 12 || y >= 45 || x <= 9 || x >= 54;
                        c = edge ? ink : bezel ? shell : screen;
                        if (!edge && !bezel && (44 - y) % 3 == 0) {
                            int li = ((44 - y) / 3 + frame) % lens.Length;       // a code line every 3 px, scrolling
                            int xs = 12 + indent[li];
                            if (x >= xs && x < Math.Min(52, xs + lens[li])) c = code;
                        }
                    }
                    px[y * W + x] = c;
                }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            Keep(tex);
            var sp = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), W / 0.5f);
            Keep(sp);
            return sp;
        }

        // ---- git gag: his own screen only, a few seconds after a kill in round one ----

        private static GameObject gagRoot;
        private static TMPro.TextMeshProUGUI gagText;
        private static float gagStart;
        private static string[] gagLines = Array.Empty<string>();
        private static int gagChars;

        private static void GagTick(PlayerControl me, bool meeting) {
            if (gagRoot != null) {
                float t = Time.time - gagStart;
                if (meeting || t > 5.2f) { HideGag(); return; }
                VanillaUI.SetText(gagText, TerminalShown(Mathf.Clamp(Mathf.FloorToInt(t * 70f), 0, gagChars)));
                var cg = gagRoot.GetComponent<CanvasGroup>();
                if (cg != null) cg.alpha = t > 4.7f ? Mathf.Clamp01((5.2f - t) / 0.5f) : 1f;
                return;
            }
            if (gagShown || !coding || votedOut || diedAt < 0f || meeting) return;
            if (me != PlayerControl.LocalPlayer || Time.time - diedAt < 3.6f) return;      // after the kill cutscene
            gagShown = true;
            ShowGag();
        }

        private static void ShowGag() {
            string hash = UnityEngine.Random.Range(0x1000000, 0xFFFFFFF).ToString("x7");
            gagLines = new[] {
                "$ git commit -am \"died in round 1\"",
                $"[main {hash}] 1 file changed, 1 death(+), 0 tasks(-)",
                "$ git push",
                "Everything up-to-date. Back to coding.",
            };
            gagChars = 0;
            foreach (var l in gagLines) gagChars += l.Length + 1;
            gagRoot = VanillaUI.Canvas("UTS_EggTerminal", 480, false);
            gagRoot.AddComponent<CanvasGroup>().blocksRaycasts = false;
            var panel = VanillaUI.Panel(gagRoot, new Vector2(820f, 210f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 150f));
            gagText = VanillaUI.Text(panel, "", 24f, new Color(0.29f, 0.87f, 0.5f), new Vector2(30f, -26f), new Vector2(760f, 170f));
            gagText.enableWordWrapping = false;
            gagStart = Time.time;
            UsefulTORStuffPlugin.Logger?.LogInfo("[EasterEgg] git gag shown");
        }

        // The first n characters, line by line, monospaced, cursor at the end. Tags wrap whole line parts only,
        // so a half-typed line never shows a broken tag.
        private static string TerminalShown(int n) {
            var sb = new StringBuilder("<mspace=0.56em>");
            int left = n;
            for (int i = 0; i < gagLines.Length && left > 0; i++) {
                string line = gagLines[i];
                int take = Math.Min(line.Length, left);
                if (i > 0) sb.Append('\n');
                string part = line.Substring(0, take).Replace("<", "&lt;");
                sb.Append(line.StartsWith("$", StringComparison.Ordinal) ? part : $"<color=#C8D0DA>{part}</color>");
                left -= line.Length + 1;
                if (take < line.Length) break;
            }
            sb.Append(Cursor()).Append("</mspace>");
            return sb.ToString();
        }

        private static void HideGag() {
            if (gagRoot != null) Object.Destroy(gagRoot);
            gagRoot = null; gagText = null;
        }

        [HarmonyPatch(typeof(TranslationController), nameof(TranslationController.GetString),
            new Type[] { typeof(StringNames), typeof(Il2CppReferenceArray<Il2CppSystem.Object>) })]
        static class ExileTextPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(ref string __result, [HarmonyArgument(0)] StringNames id) {
                if (!exileLine) return;
                if (id == StringNames.ExileTextPN || id == StringNames.ExileTextSN || id == StringNames.ExileTextPP
                    || id == StringNames.ExileTextSP || id == StringNames.ExileTextNonConfirm)
                    __result = Line();
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class JoinPatch {
            public static void Postfix() => ResetFor(null);
        }
    }
}
