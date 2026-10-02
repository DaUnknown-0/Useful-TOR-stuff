// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * RoundReplay - the last round played back on the minimap, in the lobby (User 2026-10-02: last round
 * only, lobby only).
 *
 * RECORDING (every client, locally, nothing goes over the network)
 * Every 0.25 s of gameplay (meetings do not advance the clock) the position of every living player,
 * plus whether they sit in a vent. Deaths come from TOR's GameHistory as they appear (one sentence
 * each, the kill feed's wording with roles), meetings as their start. Everything is shown only after
 * the round, when the end screen has revealed the roles anyway.
 *
 * MAP
 * At round end the ship's MapPrefab is instantiated under an inactive holder (its Awake does not run),
 * exactly the prefab the minimap is built from - Unknown's Atlas rebuilds that prefab for its own maps,
 * so the replay follows. MapBehaviour places a player at HerePoint.localPosition = world / MapScale
 * (x mirrored on dlekS), so the Background sprite's corners, taken into the HerePoint space and
 * multiplied by MapScale, give its rectangle in world metres. The sprite is copied through a
 * RenderTexture (works for compressed, unreadable textures) into a texture of our own.
 *
 * Option 1397 (General tab, on by default); guests follow the host's setting through UTSGate.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using TheOtherRoles;
using UnityEngine;
using UnityEngine.UI;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class RoundReplay {

        public static CustomOption Enabled;
        internal static BepInEx.Configuration.ConfigEntry<bool> DiagViewer;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1397, Types.General, "Round Replay In The Lobby", true, null, true);
                UTSLocalization.BindOptionTitle(Enabled, "uts.replay.option_name");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[RoundReplay] CreateOptions failed: {e}");
            }
        }

        public static bool ButtonShown { get; private set; }

        // ====================================================================
        // Recording
        // ====================================================================
        private const float Step = 0.25f;
        private const int MaxSamples = 6000;     // 25 minutes of gameplay

        private sealed class Track {
            public byte Id;
            public string Name;
            public Color Col;
            public readonly List<Vector2> P = new List<Vector2>();   // NaN = dead or gone
            public readonly List<bool> Vent = new List<bool>();
        }

        private const int EvDeath = 0, EvMeeting = 1, EvExile = 2;

        private sealed class Ev {
            public float T;
            public int Kind;
            public Vector2 Pos;
            public string Text;
        }

        private static readonly List<Track> tracks = new List<Track>();
        private static readonly List<Ev> events = new List<Ev>();
        private static readonly HashSet<byte> deathsSeen = new HashSet<byte>();
        private static ShipStatus recShip;
        private static bool recording, inMeeting, ready;
        private static float clock, sampledUpTo;
        private static float nextDeathPoll;
        private static Texture2D mapTex;
        private static Rect worldRect;
        private static bool mirrored;
        private static string mapName = "";

        private static float Duration => tracks.Count == 0 ? 0f : Mathf.Max(0, tracks.Max(t => t.P.Count) - 1) * Step;

        // Every frame, from SessionStatsUI.Update.
        public static void Tick() {
            try {
                RecordTick();
                ViewTick();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] tick failed: {e.Message}");
            }
        }

        private static void RecordTick() {
            var client = AmongUsClient.Instance;
            var ship = ShipStatus.Instance;
            // Freeplay reports IsGameStarted == false; there is no lobby afterwards anyway, so only the
            // autotest records there.
            bool started = client != null && (client.IsGameStarted || (DiagViewer != null && DiagViewer.Value));
            bool inRound = started && ship != null && IntroCutscene.Instance == null;
            if (!inRound) return;
            if (ship != recShip) Begin(ship);
            if (!recording) return;

            bool meeting = MeetingHud.Instance != null || ExileController.Instance != null;
            if (meeting && !inMeeting) events.Add(new Ev { T = clock, Kind = EvMeeting, Text = UTSLocalization.Tr("uts.replay.meeting") });
            inMeeting = meeting;
            if (!meeting) clock += Time.deltaTime;

            while (sampledUpTo <= clock && samples < MaxSamples) {
                Sample();
                sampledUpTo += Step;
            }
            if (Time.realtimeSinceStartup >= nextDeathPoll) { nextDeathPoll = Time.realtimeSinceStartup + 0.25f; PollDeaths(); }
        }

        private static void Begin(ShipStatus ship) {
            recShip = ship;
            recording = true;
            ready = false;
            inMeeting = false;
            clock = 0f; sampledUpTo = 0f; samples = 0;
            tracks.Clear(); events.Clear(); deathsSeen.Clear();
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null || p.Data == null || p.Data.Disconnected) continue;
                tracks.Add(new Track { Id = p.PlayerId, Name = p.Data.PlayerName ?? "?", Col = SessionStatsUI.PlayerColor(p) });
            }
            mapName = SafeMapName();
            CloseView();
            UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] recording started: {tracks.Count} player(s), map {mapName}.");
        }

        private static int samples;

        private static void Sample() {
            // players who show up late (freeplay dummies spawn after the ship) join with an empty past
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null || p.Data == null || p.Data.Disconnected || tracks.Any(t => t.Id == p.PlayerId)) continue;
                var nt = new Track { Id = p.PlayerId, Name = p.Data.PlayerName ?? "?", Col = SessionStatsUI.PlayerColor(p) };
                for (int i = 0; i < samples; i++) { nt.P.Add(new Vector2(float.NaN, float.NaN)); nt.Vent.Add(false); }
                tracks.Add(nt);
            }
            samples++;
            foreach (var t in tracks) {
                var p = TheOtherRoles.Helpers.playerById(t.Id);
                if (p == null || p.Data == null || p.Data.IsDead || p.Data.Disconnected) {
                    t.P.Add(new Vector2(float.NaN, float.NaN));
                    t.Vent.Add(false);
                    continue;
                }
                // name and colour can still be unset right after the spawn: keep them fresh for the first 10 s
                if (samples <= 40) { t.Name = p.Data.PlayerName ?? t.Name; t.Col = SessionStatsUI.PlayerColor(p); }
                t.P.Add(p.transform.position);
                t.Vent.Add(p.inVent);
            }
        }

        private static void PollDeaths() {
            var list = DeathTimeHistory.TorDeadPlayers();
            if (list == null) return;
            foreach (var dp in list) {
                if (dp?.player == null || !deathsSeen.Add(dp.player.PlayerId)) continue;
                bool exile = dp.deathReason == DeadPlayer.CustomDeathReason.Exile;
                Vector2 pos = dp.player.transform.position;
                var tr = tracks.FirstOrDefault(t => t.Id == dp.player.PlayerId);
                if (tr != null) for (int i = tr.P.Count - 1; i >= 0; i--) if (!float.IsNaN(tr.P[i].x)) { pos = tr.P[i]; break; }
                events.Add(new Ev { T = clock, Kind = exile ? EvExile : EvDeath, Pos = pos, Text = GhostKillFeed.Describe(dp, true) });
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
        static class GameEndPatch {
            public static void Prefix() {
                try { Finish(); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[RoundReplay] round end failed: {e}"); }
            }
        }

        private static void Finish() {
            if (!recording) return;
            PollDeaths();
            recording = false;
            ready = CaptureMap() && Duration > 1f;
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[RoundReplay] round finished: {Duration:F0}s gameplay, {events.Count} event(s), map image {(mapTex != null ? $"{mapTex.width}x{mapTex.height}" : "missing")}, world {worldRect}.");
        }

        private static string SafeMapName() {
            try {
                var atlas = AppDomain.CurrentDomain.GetData("UnknownsAtlas.ActiveMap") as string;
                if (!string.IsNullOrEmpty(atlas)) return atlas;
                return ((MapNames)GameOptionsManager.Instance.CurrentGameOptions.MapId).ToString();
            } catch { return "?"; }
        }

        // ---- the map image ----
        private static bool CaptureMap() {
            var ship = ShipStatus.Instance;
            if (ship == null || ship.MapPrefab == null) return false;
            var holder = new GameObject("UTSReplayMapProbe");
            holder.SetActive(false);
            try {
                var inst = UnityEngine.Object.Instantiate(ship.MapPrefab, holder.transform);
                // The visible floor plan: Unknown's Atlas switches the Skeld "Background" off and lays its own
                // "Atlas_MinimapFloor" into the HerePoint space instead.
                SpriteRenderer bg = null;
                foreach (var r in inst.GetComponentsInChildren<SpriteRenderer>(true))
                    if (r.sprite != null && r.enabled && r.name == "Atlas_MinimapFloor") { bg = r; break; }
                if (bg == null)
                    foreach (var r in inst.GetComponentsInChildren<SpriteRenderer>(true))
                        if (r.sprite != null && r.enabled && r.name == "Background") { bg = r; break; }
                if (bg == null || inst.HerePoint == null) { UsefulTORStuffPlugin.Logger?.LogWarning("[RoundReplay] minimap without Background/HerePoint."); return false; }
                var space = inst.HerePoint.transform.parent;
                var sb = bg.sprite.bounds;
                Vector3 a = space.InverseTransformPoint(bg.transform.TransformPoint(sb.min));
                Vector3 b = space.InverseTransformPoint(bg.transform.TransformPoint(sb.max));
                float scale = ship.MapScale;
                float sign = Mathf.Sign(ship.transform.localScale.x);
                mirrored = sign < 0f;
                float x0 = a.x * scale * sign, x1 = b.x * scale * sign;
                worldRect = Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(a.y, b.y) * scale, Mathf.Max(x0, x1), Mathf.Max(a.y, b.y) * scale);

                var spr = bg.sprite;
                var src = spr.texture;
                var tr = spr.textureRect;
                int w = Mathf.Clamp((int)tr.width, 64, 1600);
                int h = Mathf.Clamp((int)(tr.height * w / Mathf.Max(1f, tr.width)), 64, 1600);
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt, new Vector2(tr.width / src.width, tr.height / src.height),
                              new Vector2(tr.x / src.width, tr.y / src.height));
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                if (mapTex != null) UnityEngine.Object.Destroy(mapTex);
                mapTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                mapTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                // Vanilla floor plans are masks that the map shader tints (red channel = room fill, green =
                // outline/light). A plain sprite shader means the image already carries its colours.
                string shader = bg.sharedMaterial != null && bg.sharedMaterial.shader != null ? bg.sharedMaterial.shader.name : "?";
                if (!shader.StartsWith("Sprites/Default")) {
                    var px = mapTex.GetPixels32();
                    var room = new Color(0.16f, 0.32f, 0.78f);
                    for (int i = 0; i < px.Length; i++) {
                        var c = px[i];
                        var o = Color.Lerp(room, Color.white, c.g / 255f);
                        px[i] = new Color32((byte)(o.r * 255), (byte)(o.g * 255), (byte)(o.b * 255), c.a);
                    }
                    mapTex.SetPixels32(px);
                }
                mapTex.Apply(false, true);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] map image from '{bg.name}', shader '{shader}', {(shader.StartsWith("Sprites/Default") ? "kept" : "recoloured")}.");
                // The round ends before the end screen and the lobby load, and every scene load runs
                // UnloadUnusedAssets: a texture nothing in the scene references is freed there.
                // DontDestroyOnLoad does not cover assets; this flag does (User 2026-10-02: the replay
                // silently did not open after a real round).
                mapTex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return true;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] map capture failed: {e.Message}");
                return false;
            } finally {
                UnityEngine.Object.Destroy(holder);
            }
        }

        // ====================================================================
        // Lobby button and player
        // ====================================================================
        private static GameObject lobbyButton, panelRoot, mapArea, banner;
        private static RectTransform lobbyButtonRect, progressFill;
        private static TMPro.TextMeshProUGUI timeLabel, bannerText, playLabel, speedLabel;
        private static readonly Dictionary<byte, (RectTransform Dot, Image Img, TMPro.TextMeshProUGUI Name)> dots =
            new Dictionary<byte, (RectTransform, Image, TMPro.TextMeshProUGUI)>();
        private static readonly List<(Ev E, GameObject Mark)> marks = new List<(Ev, GameObject)>();
        private static float viewT, speed = 2f;
        private static bool playing;
        private static float drawW, drawH, kScale;
        private static float nextButtonPoll;
        private static float diagAt = -1f;

        private static bool ShouldShowButton() {
            try {
                if (!ready || !LobbyScreen.Exists || AmongUsClient.Instance == null) return false;
                if (AmongUsClient.Instance.AmHost) return Enabled != null && Enabled.getBool();
                return UTSGate.Bool(Enabled);
            } catch { return false; }
        }

        private static void ViewTick() {
            DiagTick();
            if (panelRoot != null) {
                bool diag = DiagViewer != null && DiagViewer.Value;
                if (!LobbyScreen.Exists && !diag) { CloseView(); }
                else Animate();
            }
            if (Time.realtimeSinceStartup < nextButtonPoll) return;
            nextButtonPoll = Time.realtimeSinceStartup + 0.5f;
            bool show = ShouldShowButton() && !SettingsOverlayView.OverlayOpen();
            ButtonShown = show;
            if (show && lobbyButton == null) BuildLobbyButton();
            if (lobbyButton == null) return;
            if (lobbyButton.activeSelf != show) lobbyButton.SetActive(show);
            int rows = (NewcomerShieldUI.ButtonShown ? 1 : 0) + (EarlyDeathShieldUI.ButtonShown ? 1 : 0) + (SessionStatsUI.ButtonShown ? 1 : 0);
            if (lobbyButtonRect != null) lobbyButtonRect.anchoredPosition = new Vector2(28, 84 + 54 * rows);
        }

        private static GameObject Canvas(string name, int order, bool blocking) {
            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var ray = go.AddComponent<GraphicRaycaster>();
            if (blocking) ray.blockingObjects = GraphicRaycaster.BlockingObjects.All;
            return go;
        }

        private static void BuildLobbyButton() {
            try {
                lobbyButton = Canvas("UTSReplayButton", 9000, false);
                var btn = SessionStatsUI.MakeButton(lobbyButton, UTSLocalization.Tr("uts.replay.button"), Vector2.zero, new Vector2(330, 46),
                                                    new Color(0.2f, 0.45f, 0.35f, 0.95f), OpenView);
                lobbyButtonRect = btn.GetComponent<RectTransform>();
                lobbyButtonRect.anchorMin = lobbyButtonRect.anchorMax = lobbyButtonRect.pivot = Vector2.zero;
                lobbyButtonRect.anchoredPosition = new Vector2(28, 84);
                var label = btn.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (label != null) label.fontSize = 18;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] lobby button failed: {e.Message}");
                lobbyButton = null;
            }
        }

        private const float PanelW = 1500, PanelH = 900, MapW = 1440, MapH = 660;

        internal static void OpenView() {
            if (panelRoot != null) { CloseView(); return; }
            if (!ready || mapTex == null) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] open refused: ready={ready}, map image {(mapTex != null ? "ok" : "missing")}.");
                return;
            }
            try {
                NewcomerShieldUI.Instance?.Close();
                EarlyDeathShieldUI.Instance?.Close();
                SessionStatsUI.Instance?.Close();

                panelRoot = Canvas("UTSReplayUI", 9500, true);
                LobbyPanelGuard.Track(panelRoot);
                var backdrop = SessionStatsUI.Box(panelRoot, Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.85f));
                var brt = backdrop.GetComponent<RectTransform>();
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.pivot = new Vector2(0.5f, 0.5f); brt.sizeDelta = Vector2.zero;
                backdrop.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)CloseView);

                var panel = SessionStatsUI.Box(panelRoot, Vector2.zero, new Vector2(PanelW, PanelH), new Color(0.1f, 0.12f, 0.16f, 0.98f));
                var prt = panel.GetComponent<RectTransform>();
                prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
                prt.anchoredPosition = Vector2.zero;

                SessionStatsUI.Label(panel, UTSLocalization.Tr("uts.replay.title"), 28, TMPro.FontStyles.Bold, new Color(0.45f, 0.8f, 1f),
                    new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -16), new Vector2(-40, 40), TMPro.TextAlignmentOptions.Center);
                SessionStatsUI.Label(panel, UTSLocalization.Tr("uts.replay.subtitle", mapName, Clock(Duration), events.Count(e => e.Kind != EvMeeting)),
                    15, TMPro.FontStyles.Normal, new Color(0.65f, 0.65f, 0.7f),
                    new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -56), new Vector2(-80, 26), TMPro.TextAlignmentOptions.Top);

                // map, aspect-fitted into the map area
                float ww = Mathf.Max(0.1f, worldRect.width), wh = Mathf.Max(0.1f, worldRect.height);
                kScale = Mathf.Min(MapW / ww, MapH / wh);
                drawW = ww * kScale; drawH = wh * kScale;
                mapArea = SessionStatsUI.Box(panel, new Vector2((PanelW - drawW) / 2f, -90 - (MapH - drawH) / 2f), new Vector2(drawW, drawH), new Color(0, 0, 0, 0));
                var raw = new GameObject("Map");
                raw.transform.SetParent(mapArea.transform, false);
                var rrt = raw.AddComponent<RectTransform>();
                rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.sizeDelta = Vector2.zero;
                var ri = raw.AddComponent<RawImage>();
                ri.texture = mapTex;
                ri.uvRect = mirrored ? new Rect(1, 0, -1, 1) : new Rect(0, 0, 1, 1);
                ri.raycastTarget = false;

                // death markers first (below the dots)
                marks.Clear();
                foreach (var e in events.Where(e => e.Kind == EvDeath)) {
                    var m = SessionStatsUI.Label(mapArea, "X", 30, TMPro.FontStyles.Bold, new Color(1f, 0.25f, 0.25f),
                        Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), ToUi(e.Pos), new Vector2(40, 40), TMPro.TextAlignmentOptions.Center);
                    m.gameObject.SetActive(false);
                    marks.Add((e, m.gameObject));
                }
                dots.Clear();
                foreach (var t in tracks) {
                    var d = SessionStatsUI.Box(mapArea, Vector2.zero, new Vector2(22, 22), t.Col);
                    var drt = d.GetComponent<RectTransform>();
                    drt.anchorMin = drt.anchorMax = Vector2.zero; drt.pivot = new Vector2(0.5f, 0.5f);
                    var img = d.GetComponent<Image>();
                    img.sprite = SessionStatsUI.Circle();
                    img.raycastTarget = false;
                    var name = SessionStatsUI.Label(d, t.Name, 13, TMPro.FontStyles.Bold, Color.white,
                        new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(160, 18), TMPro.TextAlignmentOptions.Top);
                    name.enableWordWrapping = false;
                    name.outlineWidth = 0.25f;                 // readable on light and dark floor plans
                    name.outlineColor = new Color32(0, 0, 0, 255);
                    dots[t.Id] = (drt, img, name);
                }

                // banner for the latest event
                banner = SessionStatsUI.Box(panel, new Vector2(250, -96), new Vector2(PanelW - 500, 36), new Color(0, 0, 0, 0.6f));
                bannerText = SessionStatsUI.Label(banner, "", 17, TMPro.FontStyles.Bold, Color.white, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, TMPro.TextAlignmentOptions.Center);
                banner.SetActive(false);

                // controls: play/pause, speed, timeline, time
                float cy = -PanelH + 120;
                var play = SessionStatsUI.MakeButton(panel, "", Vector2.zero, new Vector2(140, 40), new Color(0.16f, 0.42f, 0.6f, 0.95f), TogglePlay);
                Place(play, new Vector2(30, cy));
                playLabel = play.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                var sp = SessionStatsUI.MakeButton(panel, "", Vector2.zero, new Vector2(90, 40), new Color(0.3f, 0.3f, 0.38f, 0.95f), CycleSpeed);
                Place(sp, new Vector2(180, cy));
                speedLabel = sp.GetComponentInChildren<TMPro.TextMeshProUGUI>();

                var bar = SessionStatsUI.Box(panel, new Vector2(290, cy - 14), new Vector2(PanelW - 290 - 190, 12), new Color(1f, 1f, 1f, 0.12f));
                var fill = SessionStatsUI.Box(bar, Vector2.zero, new Vector2(0, 12), new Color(0.45f, 0.8f, 1f, 0.9f));
                progressFill = fill.GetComponent<RectTransform>();
                float barW = PanelW - 290 - 190, dur = Mathf.Max(1f, Duration);
                foreach (var e in events) {
                    var tick = SessionStatsUI.Box(bar, new Vector2(barW * e.T / dur - 2, 6), new Vector2(4, 24),
                        e.Kind == EvMeeting ? new Color(1f, 0.85f, 0.3f) : e.Kind == EvExile ? new Color(1f, 0.6f, 0.2f) : new Color(1f, 0.3f, 0.3f));
                    tick.GetComponent<Image>().raycastTarget = false;
                }
                // click anywhere on the bar to jump there
                var hit = SessionStatsUI.Box(bar, new Vector2(0, 14), new Vector2(barW, 40), new Color(0, 0, 0, 0));
                var hitRt = hit.GetComponent<RectTransform>();
                hit.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                    if (RectTransformUtility.ScreenPointToLocalPointInRectangle(hitRt, Input.mousePosition, null, out var local))
                        viewT = Mathf.Clamp01(local.x / barW) * Duration;
                }));
                timeLabel = SessionStatsUI.Text(panel, "", 16, TMPro.FontStyles.Bold, Color.white, new Vector2(PanelW - 180, cy - 8), new Vector2(160, 26));
                timeLabel.alignment = TMPro.TextAlignmentOptions.TopRight;

                var close = SessionStatsUI.MakeButton(panel, UTSLocalization.Tr("uts.sessionstats.close"), new Vector2(0, 20), new Vector2(240, 44),
                                                      new Color(0.3f, 0.3f, 0.38f, 0.95f), CloseView);

                viewT = 0f;
                playing = true;
                Animate();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[RoundReplay] panel failed: {e}");
                CloseView();
            }
        }

        private static void Place(GameObject go, Vector2 topLeft) {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = topLeft;
        }

        private static void TogglePlay() {
            if (!playing && viewT >= Duration - 0.01f) viewT = 0f;
            playing = !playing;
        }

        private static void CycleSpeed() => speed = speed >= 8f ? 1f : speed * 2f;

        internal static void CloseView() {
            if (panelRoot != null) { UnityEngine.Object.Destroy(panelRoot); LobbyPanelGuard.Closed(); }
            panelRoot = null; mapArea = null; banner = null;
            dots.Clear(); marks.Clear();
        }

        private static Vector2 ToUi(Vector2 world) =>
            new Vector2((world.x - worldRect.xMin) * kScale, (world.y - worldRect.yMin) * kScale);

        private static string Clock(float s) {
            int t = Mathf.Max(0, Mathf.RoundToInt(s));
            return $"{t / 60}:{t % 60:00}";
        }

        private static void Animate() {
            if (mapArea == null) return;
            float dur = Duration;
            if (playing) {
                viewT += Time.unscaledDeltaTime * speed;
                if (viewT >= dur) { viewT = dur; playing = false; }
            }
            float f = viewT / Step;
            int i = Mathf.FloorToInt(f);
            float u = f - i;
            foreach (var t in tracks) {
                if (!dots.TryGetValue(t.Id, out var d)) continue;
                if (t.P.Count == 0 || i >= t.P.Count) { d.Dot.gameObject.SetActive(false); continue; }
                var a = t.P[Mathf.Clamp(i, 0, t.P.Count - 1)];
                bool alive = !float.IsNaN(a.x);
                if (d.Dot.gameObject.activeSelf != alive) d.Dot.gameObject.SetActive(alive);
                if (!alive) continue;
                var b = i + 1 < t.P.Count ? t.P[i + 1] : a;
                var pos = float.IsNaN(b.x) ? a : Vector2.Lerp(a, b, u);
                d.Dot.anchoredPosition = ToUi(pos);
                bool vent = i < t.Vent.Count && t.Vent[i];
                var c = t.Col; c.a = vent ? 0.35f : 1f;
                d.Img.color = c;
            }
            foreach (var (e, m) in marks) {
                bool on = e.T <= viewT;
                if (m.activeSelf != on) m.SetActive(on);
            }
            // banner: the newest event within the last 3 s of replay time
            var ev = events.LastOrDefault(e => e.T <= viewT && viewT - e.T < 3f);
            if (banner != null) {
                if (banner.activeSelf != (ev != null)) banner.SetActive(ev != null);
                if (ev != null && bannerText.text != ev.Text) bannerText.text = ev.Text;
            }
            if (progressFill != null) progressFill.sizeDelta = new Vector2((PanelW - 290 - 190) * (dur > 0 ? viewT / dur : 0f), 12);
            if (timeLabel != null) timeLabel.text = $"{Clock(viewT)} / {Clock(dur)}";
            if (playLabel != null) playLabel.text = UTSLocalization.Tr(playing ? "uts.replay.pause" : "uts.replay.play");
            if (speedLabel != null) speedLabel.text = $"{speed:0}x";
        }

        // ====================================================================
        // Autotest: freeplay has no OnGameEnd, so finish after 12 s of recording and open the player
        // ====================================================================
        private static void DiagTick() {
            if (DiagViewer == null || !DiagViewer.Value) return;
            if (ShipStatus.Instance == null) { diagAt = -1f; return; }
            if (!recording && !ready) return;
            if (diagAt < 0f) { diagAt = Time.realtimeSinceStartup + 12f; return; }
            if (diagAt == 0f) return;
            if (Time.realtimeSinceStartup < diagAt) return;
            diagAt = 0f;
            // a sample death in the middle, at the first dummy's spot
            var other = tracks.FirstOrDefault(t => t.Id != PlayerControl.LocalPlayer?.PlayerId && t.P.Count > 0);
            if (other != null)
                events.Add(new Ev { T = Duration * 0.5f, Kind = EvDeath, Pos = other.P[other.P.Count / 2],
                                    Text = UTSLocalization.Tr("uts.killfeed.kill", other.Name, tracks[0].Name) });
            Finish();
            OpenView();
            viewT = Duration * 0.75f;
            playing = false;
            Animate();
            string shot = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSReplay_diag.png");
            ScreenCapture.CaptureScreenshot(shot);
            UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag: player opened ({tracks.Count} tracks, {Duration:F1}s), screenshot -> {shot}");
        }
    }
}
