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

    public static partial class RoundReplay {

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

        // ====================================================================
        // Recording
        // ====================================================================
        private const float Step = 0.25f;
        private const int MaxSamples = 12000;    // 50 minutes of gameplay

        private sealed class Track {
            public byte Id;
            public string Name;
            public Color Col;
            public readonly List<Vector2> P = new List<Vector2>();   // NaN = dead or gone
            public readonly List<bool> Vent = new List<bool>();
            // host only (RoundReplayHost): light radius (0 = no light: dead, gone), the tracks he
            // could see as a bit mask over the track list, and his visible look from sample S on
            public readonly List<float> R = new List<float>();
            public readonly List<ulong> Sees = new List<ulong>();
            public readonly List<(int S, Look L)> Looks = new List<(int, Look)>();
            // his own outfit (for the figures of the real map view, also after a disconnect)
            public int OColor = -1;
            public string OHat = "", OSkin = "", OVisor = "";
            public float NameDy = float.NaN;   // height of his name tag over his position (tall hats lift it)
        }

        private const int EvDeath = 0, EvMeeting = 1, EvExile = 2;

        private sealed class Ev {
            public float T;
            public int Kind;
            public Vector2 Pos;
            public string Text;
            public byte Actor = 255;
            public byte Target = 255;    // the victim of a death, the body of a cleaning
            public List<byte> Seen;      // host only: who could see the spot (null = not recorded)
            public bool Generic;         // a UC ability seen only as its module; a client report replaces it
            public string Result;        // meetings: the vote, as the replay's meeting card shows it (RoundReplayView)
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
            if (ship != recShip) {
                // Option 1397 off: record nothing at all (no samples, no raycasts, no ship copy at the
                // end). Decided once per round, like the rest of the recording state.
                if (!RecordingAllowed()) { recShip = ship; recording = false; ready = false; return; }
                Begin(ship);
            }
            if (!recording) return;

            bool meeting = MeetingHud.Instance != null || ExileController.Instance != null;
            if (meeting && !inMeeting) events.Add(MeetingEvent());
            inMeeting = meeting;
            // At the sample limit the clock stops with the samples (audit 04.10.): later events land
            // at the end of the timeline instead of beyond it, where playback never reached them.
            if (!meeting && samples < MaxSamples) clock += Time.deltaTime;
            DeviceTick();

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
            // The chosen perspective is a player id of the previous round; in a new round (or lobby)
            // it filtered the event list down to meetings or silently picked a different player.
            persp = 255;
            ReleaseLive();
            HostBegin();
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
                if (samples <= 40) {
                    t.Name = p.Data.PlayerName ?? t.Name; t.Col = SessionStatsUI.PlayerColor(p);
                    try {
                        var o = p.Data.DefaultOutfit;
                        t.OColor = o.ColorId; t.OHat = o.HatId ?? ""; t.OSkin = o.SkinId ?? ""; t.OVisor = o.VisorId ?? "";
                        if (p.cosmetics?.nameText != null) t.NameDy = p.cosmetics.nameText.transform.position.y - p.transform.position.y;
                    } catch { }
                }
                t.P.Add(p.transform.position);
                t.Vent.Add(p.inVent);
            }
            if (hostRec) {
                try { HostSample(); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] host sample failed: {e.Message}"); }
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
                byte killer = dp.killerIfExisting != null ? dp.killerIfExisting.PlayerId : (byte)255;
                events.Add(new Ev { T = clock, Kind = exile ? EvExile : EvDeath, Pos = pos, Text = GhostKillFeed.Describe(dp, true), Actor = killer, Target = dp.player.PlayerId,
                                    Seen = hostRec && !exile ? Witnesses(pos, killer).Where(id => id != dp.player.PlayerId).ToList() : null });
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
            if (hostRec) { RecordShipState(); CaptureLiveShip(); }
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
        private static GameObject panelRoot, mapArea, banner;
        private static RectTransform progressFill;
        private static TMPro.TextMeshProUGUI timeLabel, bannerText, playLabel, speedLabel;
        private static readonly Dictionary<byte, (RectTransform Dot, Image Img, TMPro.TextMeshProUGUI Name)> dots =
            new Dictionary<byte, (RectTransform, Image, TMPro.TextMeshProUGUI)>();
        private static readonly List<(Ev E, GameObject Mark)> marks = new List<(Ev, GameObject)>();
        private static float viewT, speed = 2f;
        private static bool playing;
        private static float drawW, drawH, kScale;
        private static float diagAt = -1f;

        // host perspective (RoundReplayHost records the data): the chosen player, his light, the chips
        private static byte persp = 255;
        private static TMPro.TextMeshProUGUI seesLabel;
        private static RectTransform visionRt;
        private static readonly List<(byte Id, Image Img, Color Col)> chips = new List<(byte, Image, Color)>();

        private static bool RecordingAllowed() {
            try {
                if (DiagViewer != null && DiagViewer.Value) return true;
                if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) return Enabled != null && Enabled.getBool();
                return UTSGate.Bool(Enabled);
            } catch { return true; }
        }

        internal static bool ShouldShowButton() {
            try {
                if (!ready || !LobbyScreen.Exists || AmongUsClient.Instance == null) return false;
                if (AmongUsClient.Instance.AmHost) return Enabled != null && Enabled.getBool();
                return UTSGate.Bool(Enabled);
            } catch { return false; }
        }

        private static void ViewTick() {
            DiagTick();
            WorldTick();
            if (panelRoot != null) {
                bool diag = DiagViewer != null && DiagViewer.Value;
                if (!LobbyScreen.Exists && !diag) { CloseView(); }
                else Animate();
            }
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

        // map on the left, the event list (RoundReplayView) on the right, filters above, controls below
        private const float PanelW = 1760, PanelH = 1010, MapX = 30, MapY = -128, MapW = 1240, MapH = 712;
        private const float BarX = 560, BarW = PanelW - BarX - 190;

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

                panelRoot = VanillaUI.Canvas("UTSReplayUI", 9500, true);
                LobbyPanelGuard.Track(panelRoot);
                VanillaUI.Backdrop(panelRoot, CloseView);

                var panel = VanillaUI.CenterPanel(panelRoot, new Vector2(PanelW, PanelH));
                float ty = VanillaUI.Title(panel, UTSLocalization.Tr("uts.replay.title"), 14f, 30f);
                VanillaUI.Subtitle(panel, UTSLocalization.Tr("uts.replay.subtitle", mapName, Clock(Duration), events.Count(e => e.Kind != EvMeeting)),
                                   ty - 4f, 26f);

                // map, aspect-fitted into the map area
                bool host = HostData;
                float mapH = MapH - 72;     // room for the player row and the perspective line
                float ww = Mathf.Max(0.1f, worldRect.width), wh = Mathf.Max(0.1f, worldRect.height);
                kScale = Mathf.Min(MapW / ww, mapH / wh);
                drawW = ww * kScale; drawH = wh * kScale;
                mapArea = SessionStatsUI.Box(panel, new Vector2(MapX + (MapW - drawW) / 2f, MapY - (mapH - drawH) / 2f), new Vector2(drawW, drawH), new Color(0, 0, 0, 0));
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
                // the chosen player's light, below the dots
                var vis = new GameObject("Vision");
                vis.transform.SetParent(mapArea.transform, false);
                visionRt = vis.AddComponent<RectTransform>();
                visionRt.anchorMin = visionRt.anchorMax = Vector2.zero; visionRt.pivot = new Vector2(0.5f, 0.5f);
                var visionImg = vis.AddComponent<Image>();
                visionImg.sprite = SessionStatsUI.Circle();
                visionImg.color = new Color(1f, 0.8f, 0.15f, 0.38f);     // warm, strong enough on light floor plans
                visionImg.raycastTarget = false;
                vis.SetActive(false);
                // trail, halos and the event spot, below the dots (RoundReplayView)
                BuildMapMarks();

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

                // host: the real map for the perspective (RoundReplayWorld), loaded in the background
                if (host && WorldPossible()) { WorldBuildUi(panel, mapH); WorldLoad(); }

                // banner for the latest event
                banner = SessionStatsUI.Box(panel, new Vector2(MapX + 100, MapY - 4), new Vector2(MapW - 200, host ? 50 : 36), new Color(0, 0, 0, 0.6f));
                bannerText = SessionStatsUI.Label(banner, "", 17, TMPro.FontStyles.Bold, Color.white, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-16, 0), TMPro.TextAlignmentOptions.Center);
                bannerText.enableAutoSizing = true; bannerText.fontSizeMin = 11; bannerText.fontSizeMax = 17;
                banner.SetActive(false);

                // whose eyes (one chip per player, "All" switches the perspective off). Everyone gets the
                // chips as the event list's player filter; light and line of sight are host data only.
                chips.Clear();
                seesLabel = null;
                {
                    float rowY = MapY - mapH - 8;
                    int n = tracks.Count + 1;
                    float cw = Mathf.Min(150f, (MapW - 6f * (n - 1)) / n);
                    for (int k = 0; k < n; k++) {
                        byte id = k == 0 ? (byte)255 : tracks[k - 1].Id;
                        Color col = k == 0 ? VanillaUI.Grey : tracks[k - 1].Col;
                        string label = k == 0 ? UTSLocalization.Tr("uts.replay.persp_all") : tracks[k - 1].Name;
                        var chip = SessionStatsUI.MakeButton(panel, label, Vector2.zero, new Vector2(cw, 30), Color.white, () => persp = id);
                        Place(chip, new Vector2(MapX + k * (cw + 6f), rowY));
                        var txt = chip.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                        if (txt != null) {
                            txt.enableAutoSizing = true; txt.fontSizeMin = 9; txt.fontSizeMax = 15;
                            txt.enableWordWrapping = false;
                            txt.outlineWidth = 0.25f; txt.outlineColor = new Color32(0, 0, 0, 255);
                        }
                        chips.Add((id, ButtonFill(chip), col));
                    }
                    if (host) {
                        seesLabel = SessionStatsUI.Text(panel, "", 15, TMPro.FontStyles.Normal, new Color(0.85f, 0.85f, 0.9f),
                                                        new Vector2(MapX, rowY - 36), new Vector2(MapW - 220, 24));
                        seesLabel.enableWordWrapping = false;
                        seesLabel.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                    }
                    if (worldArea != null) {
                        var vb = SessionStatsUI.MakeButton(panel, "", Vector2.zero, new Vector2(210, 26), VanillaUI.Grey, ToggleWorldView);
                        Place(vb, new Vector2(MapX + MapW - 210, rowY - 36));
                        viewLabel = vb.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                        if (viewLabel != null) viewLabel.fontSize = 13;
                    }
                }

                // controls: play/pause, previous/next event, speed, stop at kills, timeline, time
                float cy = -PanelH + 150;
                var play = SessionStatsUI.MakeButton(panel, "", Vector2.zero, new Vector2(140, 40), VanillaUI.Teal, TogglePlay);
                Place(play, new Vector2(30, cy));
                playLabel = play.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                var prev = SessionStatsUI.MakeButton(panel, "<", Vector2.zero, new Vector2(46, 40), VanillaUI.Grey, () => StepEvent(-1));
                Place(prev, new Vector2(176, cy));
                var next = SessionStatsUI.MakeButton(panel, ">", Vector2.zero, new Vector2(46, 40), VanillaUI.Grey, () => StepEvent(1));
                Place(next, new Vector2(228, cy));
                var sp = SessionStatsUI.MakeButton(panel, "", Vector2.zero, new Vector2(70, 40), VanillaUI.Grey, CycleSpeed);
                Place(sp, new Vector2(280, cy));
                speedLabel = sp.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                var ks = SessionStatsUI.MakeButton(panel, "", Vector2.zero, new Vector2(184, 40), Color.white, () => stopAtKill = !stopAtKill);
                Place(ks, new Vector2(356, cy));
                killStopImg = ButtonFill(ks);
                killStopLabel = ks.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (killStopLabel != null) { killStopLabel.enableAutoSizing = true; killStopLabel.fontSizeMin = 10; killStopLabel.fontSizeMax = 15; }

                var bar = SessionStatsUI.Box(panel, new Vector2(BarX, cy - 14), new Vector2(BarW, 12), new Color(1f, 1f, 1f, 0.12f));
                BuildPhases(bar);
                var fill = SessionStatsUI.Box(bar, Vector2.zero, new Vector2(0, 12), new Color(0.45f, 0.8f, 1f, 0.9f));
                fill.GetComponent<Image>().raycastTarget = false;
                progressFill = fill.GetComponent<RectTransform>();
                float dur = Mathf.Max(1f, Duration);
                ticks.Clear();
                foreach (var e in events) {
                    bool big = e.Kind == EvMeeting || e.Kind == EvExile || e.Kind == EvDeath;
                    var tick = SessionStatsUI.Box(bar, new Vector2(BarW * e.T / dur - (big ? 2 : 1), big ? 6 : 2), new Vector2(big ? 4 : 2, big ? 24 : 16), TickColor(e.Kind));
                    tick.GetComponent<Image>().raycastTarget = false;
                    ticks.Add((e, tick));
                }
                // click anywhere on the bar to jump there
                var hit = SessionStatsUI.Box(bar, new Vector2(0, 14), new Vector2(BarW, 40), new Color(0, 0, 0, 0));
                var hitRt = hit.GetComponent<RectTransform>();
                hit.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                    if (RectTransformUtility.ScreenPointToLocalPointInRectangle(hitRt, Input.mousePosition, null, out var local)) {
                        Seek(Mathf.Clamp01(local.x / BarW) * Duration);
                    }
                }));
                BuildPhaseLabels(panel, cy);
                timeLabel = SessionStatsUI.Text(panel, "", 16, TMPro.FontStyles.Bold, Color.white, new Vector2(PanelW - 180, cy - 8), new Vector2(160, 26));
                timeLabel.alignment = TMPro.TextAlignmentOptions.TopRight;

                // filters above the map, the event list right of it, the meeting card over the map
                BuildFilterRow(panel);
                BuildEventList(panel);
                BuildMeetingCard(panel);

                VanillaUI.CloseButton(panel, UTSLocalization.Tr("uts.sessionstats.close"), CloseView);

                viewT = 0f;
                playing = true;
                Animate();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[RoundReplay] panel failed: {e}");
                CloseView();
            }
        }

        // the tinted part of a VanillaUI button (the root itself carries no Image)
        private static Image ButtonFill(GameObject button) {
            var fill = button.transform.Find("Fill");
            return fill != null ? fill.GetComponent<Image>() : button.GetComponent<Image>();
        }

        private static void Place(GameObject go, Vector2 topLeft) {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = topLeft;
        }

        private static void TogglePlay() {
            if (CardPlayToggle()) return;
            if (!playing && viewT >= Duration - 0.01f) Seek(0f);
            playing = !playing;
        }

        private static void CycleSpeed() => speed = speed >= 8f ? 1f : speed * 2f;

        internal static void CloseView() {
            WorldDestroy();
            if (panelRoot != null) { UnityEngine.Object.Destroy(panelRoot); LobbyPanelGuard.Closed(); }
            panelRoot = null; mapArea = null; banner = null;
            dots.Clear(); marks.Clear(); chips.Clear();
            seesLabel = null; visionRt = null;
            ViewDestroy();
        }

        private static Color TickColor(int kind) => kind switch {
            EvMeeting => new Color(1f, 0.85f, 0.3f),
            EvExile => new Color(1f, 0.6f, 0.2f),
            EvDeath => new Color(1f, 0.3f, 0.3f),
            EvAbility => new Color(0.75f, 0.5f, 1f),
            EvVent => new Color(0.4f, 0.9f, 0.5f),
            EvSabotage => new Color(1f, 0.45f, 0.1f),
            EvLook => new Color(0.4f, 0.85f, 1f),
            EvTask => new Color(0.55f, 0.6f, 0.7f),
            EvDevice => new Color(0.85f, 0.95f, 0.35f),
            _ => new Color(0.7f, 0.7f, 0.7f),
        };

        private static Look? LookAt(Track t, int si) {
            for (int k = t.Looks.Count - 1; k >= 0; k--) if (t.Looks[k].S <= si) return t.Looks[k].L;
            return t.Looks.Count > 0 ? t.Looks[0].L : (Look?)null;
        }

        private static string Plain(Track t) => $"<color=#{ColorUtility.ToHtmlStringRGB(t.Col)}>{t.Name}</color>";

        private static Vector2 ToUi(Vector2 world) =>
            new Vector2((world.x - worldRect.xMin) * kScale, (world.y - worldRect.yMin) * kScale);

        private static string Clock(float s) {
            int t = Mathf.Max(0, Mathf.RoundToInt(s));
            return $"{t / 60}:{t % 60:00}";
        }

        private static void Animate() {
            if (mapArea == null) return;
            float dur = Duration;
            RefreshFilter();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) StepEvent(-1);
            if (Input.GetKeyDown(KeyCode.RightArrow)) StepEvent(1);
            if (playing && !CardHolding()) {
                float before = viewT;
                viewT += Time.unscaledDeltaTime * speed;
                if (viewT >= dur) { viewT = dur; playing = false; }
                CrossCheck(before);
            }
            float f = viewT / Step;
            int i = Mathf.FloorToInt(f);
            float u = f - i;
            var pt = persp != 255 ? tracks.FirstOrDefault(t => t.Id == persp) : null;
            if (pt != null && pt.R.Count == 0) pt = null;
            int pi = Mathf.Clamp(i, 0, Mathf.Max(0, (pt?.R.Count ?? 1) - 1));
            bool pLit = pt != null && pi < pt.R.Count && pt.R[pi] > 0f && !(pi < pt.Vent.Count && pt.Vent[pi]);
            ulong sees = pLit && pi < pt.Sees.Count ? pt.Sees[pi] : 0UL;
            bool visionShown = false;
            for (int ti = 0; ti < tracks.Count; ti++) {
                var t = tracks[ti];
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
                // the look the others saw (Morphling, Camouflage...), recorded by the host
                var look = LookAt(t, i);
                var c = t.Col;
                if (look.HasValue && look.Value.Color >= 0 && look.Value.Color < Palette.PlayerColors.Length) c = Palette.PlayerColors[look.Value.Color];
                c.a = vent || (look.HasValue && look.Value.Hidden) ? 0.35f : 1f;
                if (pt != null && t != pt && !(ti < 64 && (sees & (1UL << ti)) != 0)) c.a *= 0.2f;
                d.Img.color = c;
                d.Dot.localScale = t.Id == persp ? new Vector3(1.4f, 1.4f, 1f) : Vector3.one;
                string label = t.Name;
                if (look.HasValue && t.Looks.Count > 1) {
                    var l = look.Value;
                    var first = t.Looks[0].L;     // the settled look of the first 2 s
                    if (l.Hidden) label = UTSLocalization.Tr("uts.replay.label_hidden", t.Name);
                    else if (l.Color != first.Color) label = string.IsNullOrEmpty(l.Name) ? UTSLocalization.Tr("uts.replay.label_camo", t.Name)
                                                                                          : UTSLocalization.Tr("uts.replay.label_as", t.Name, l.Name);
                }
                if (d.Name.text != label) d.Name.text = label;
                if (t == pt && pLit && visionRt != null) {
                    float diam = 2f * pt.R[pi] * kScale;
                    visionRt.sizeDelta = new Vector2(diam, diam);
                    visionRt.anchoredPosition = d.Dot.anchoredPosition;
                    visionShown = true;
                }
            }
            if (visionRt != null && visionRt.gameObject.activeSelf != visionShown) visionRt.gameObject.SetActive(visionShown);
            bool world = WorldAnimate(pt, i, u);
            // the wheel scrolls the event list under the mouse, zooms the real map view otherwise
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f && !ListWheel(wheel) && world)
                worldZoom = Mathf.Clamp(worldZoom * (wheel > 0 ? 0.88f : 1.14f), 0.4f, 3f);
            if (viewLabel != null) {
                string vl = UTSLocalization.Tr(worldState == 1 ? "uts.replay.view_loading" : worldState == 3 ? "uts.replay.view_failed"
                                               : worldView ? "uts.replay.view_world" : "uts.replay.view_map");
                if (viewLabel.text != vl) viewLabel.text = vl;
            }
            foreach (var (id, img, col) in chips) {
                var cc = col; cc.a = id == persp ? 1f : 0.35f;
                if (img != null) img.color = cc;
            }
            if (seesLabel != null) {
                string s;
                if (pt == null) s = UTSLocalization.Tr("uts.replay.persp_hint");
                else if (pi >= pt.R.Count || pt.R[pi] <= 0f) s = UTSLocalization.Tr("uts.replay.persp_dead", Plain(pt));
                else if (pi < pt.Vent.Count && pt.Vent[pi]) s = UTSLocalization.Tr("uts.replay.persp_vent", Plain(pt));
                else {
                    var seen = new List<string>();
                    for (int k = 0; k < tracks.Count && k < 64; k++) if ((sees & (1UL << k)) != 0) seen.Add(Plain(tracks[k]));
                    s = seen.Count == 0 ? UTSLocalization.Tr("uts.replay.persp_none", Plain(pt))
                                        : UTSLocalization.Tr("uts.replay.persp_sees", Plain(pt), string.Join(", ", seen));
                }
                if (seesLabel.text != s) seesLabel.text = s;
            }
            foreach (var (e, m) in marks) {
                bool on = e.T <= viewT;
                if (m.activeSelf != on) m.SetActive(on);
            }
            // banner: the newest event the filters let through, within the last 3 s of replay time
            // (hidden while the meeting card covers the map)
            var ev = shownEvs.LastOrDefault(e => e.T <= viewT && viewT - e.T < 3f);
            if (CardShown()) ev = null;
            if (banner != null) {
                if (banner.activeSelf != (ev != null)) banner.SetActive(ev != null);
                if (ev != null) {
                    string text = ev.Text + SeenSuffix(ev, pt);
                    if (bannerText.text != text) bannerText.text = text;
                }
            }
            ViewAnimate(i, u);
            if (progressFill != null) progressFill.sizeDelta = new Vector2(BarW * (dur > 0 ? viewT / dur : 0f), 12);
            if (timeLabel != null) timeLabel.text = $"{Clock(viewT)} / {Clock(dur)}";
            if (playLabel != null) playLabel.text = UTSLocalization.Tr(playing ? "uts.replay.pause" : "uts.replay.play");
            if (speedLabel != null) speedLabel.text = $"{speed:0}x";
        }

        // who could see the event: for the chosen player yes or no, otherwise the list
        private static string SeenSuffix(Ev e, Track pt) {
            if (e.Seen == null) return "";
            if (pt != null) {
                if (e.Actor == pt.Id) return "";
                return "\n" + UTSLocalization.Tr(e.Seen.Contains(pt.Id) ? "uts.replay.seen_yes" : "uts.replay.seen_no", Plain(pt));
            }
            if (e.Seen.Count == 0) return "\n" + UTSLocalization.Tr("uts.replay.seen_nobody");
            var names = e.Seen.Select(id => tracks.FirstOrDefault(t => t.Id == id)).Where(t => t != null).Select(Plain);
            return "\n" + UTSLocalization.Tr("uts.replay.seen_by", string.Join(", ", names));
        }

        // ====================================================================
        // Autotest: freeplay has no OnGameEnd, so finish after 12 s of recording and open the player
        // ====================================================================
        private static int diagStep;

        private static string PlainText(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "");

        private static void DiagTick() {
            if (DiagViewer == null || !DiagViewer.Value) return;
            DiagShot();
            if (ShipStatus.Instance == null) { diagAt = -1f; return; }
            if (!recording && !ready) return;
            if (diagAt < 0f) { diagAt = Time.realtimeSinceStartup + 12f; diagStep = 0; diagIngameT = -1f; diagIngameT2 = -1f; diagSecond = false; return; }
            if (diagAt == 0f) return;
            // host perspective material: a lights sabotage after 3 s, TOR's Camouflage after 5 s
            float left = diagAt - Time.realtimeSinceStartup;
            // a real in-game shot at 1.5 s, compared 1:1 with the replay's real map view later
            if (diagIngameT < 0f && left < 10.5f) {
                diagIngameT = clock;
                diagIngameOrtho = Camera.main != null ? Camera.main.orthographicSize : 3f;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSReplay_ingame.png"));
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag: in-game shot at {clock:F2}s, ortho {diagIngameOrtho:F2}, local at {PlayerControl.LocalPlayer?.transform.position}.");
                DiagLightLog("light");
            }
            if (diagIngameT2 < 0f && left < 2.5f) {
                diagIngameT2 = clock;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSReplay_ingame_dark.png"));
                DiagLightLog("dark");
            }
            if (diagStep == 0 && left < 9f) {
                diagStep = 1;
                try { ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Sabotage, (byte)SystemTypes.Electrical); } catch { }
            } else if (diagStep == 1 && left < 7f) {
                diagStep = 2;
                try { RPCProcedure.camouflagerCamouflage(); } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] diag camouflage: {e.Message}"); }
            } else if (diagStep == 2 && left < 6f) {
                diagStep = 3;
                // a task through the game's own RPC, and the first usable ability button
                try {
                    var lp = PlayerControl.LocalPlayer;
                    var task = lp.myTasks.ToArray().FirstOrDefault(t => t != null && !t.IsComplete);
                    if (task != null) lp.RpcCompleteTask(task.Id);
                } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] diag task: {e.Message}"); }
                try {
                    var b = TheOtherRoles.Objects.CustomButton.buttons.FirstOrDefault(x => x != null && !string.IsNullOrEmpty(x.buttonText) && x.buttonText != "END" && x.HasButton() && x.CouldUse());
                    if (b != null) { b.Timer = -1f; b.onClickEvent(); UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag: clicked '{b.buttonText}'."); }
                } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] diag button: {e.Message}"); }
            }
            if (Time.realtimeSinceStartup < diagAt) return;
            diagAt = 0f;
            // a sample death in the middle, at the first dummy's spot
            var other = tracks.FirstOrDefault(t => t.Id != PlayerControl.LocalPlayer?.PlayerId && t.P.Count > 0);
            var victim = tracks.FirstOrDefault(t => t != other && t.Id != PlayerControl.LocalPlayer?.PlayerId && t.P.Count > 0) ?? other;
            if (victim != null)
                events.Add(new Ev { T = Duration * 0.1f, Kind = EvDeath, Pos = victim.P[victim.P.Count / 10], Target = victim.Id,
                                    Text = UTSLocalization.Tr("uts.killfeed.kill", victim.Name, tracks[0].Name) });
            Finish();
            DiagDump();
            OpenView();
            viewT = Duration * 0.2f;      // before the diag lights sabotage: the full light radius
            playing = false;
            Animate();
            // the perspective of the first dummy; the shot waits for the real map (at most 15 s)
            // (the player who sees the most others at that moment, so figures and bodies get tested)
            int dsi = Mathf.FloorToInt(viewT / Step);
            var best = tracks.Where(t => dsi < t.Sees.Count).OrderByDescending(t => BitCount(t.Sees[dsi])).FirstOrDefault() ?? other;
            if (best != null) { persp = best.Id; Animate(); }
            diagShotAt = Time.realtimeSinceStartup + 15f;
        }

        private static float diagShotAt = -1f;
        private static float diagIngameT = -1f, diagIngameT2 = -1f, diagIngameOrtho = 3f;
        private static bool diagSecond;
        private static int diagSecondFrame = int.MaxValue, diagSecondShot = -1;

        private static void DiagLightLog(string when) {
            try {
                var lp = PlayerControl.LocalPlayer;
                var ls = lp.lightSource;
                var body = lp.cosmetics.currentBodySprite.BodySprite;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag light ({when}): calc {ShipStatus.Instance.CalculateLightRadius(lp.Data):F2}, viewDistance {ls.viewDistance:F2}, " +
                    $"source at {ls.transform.position - lp.transform.position} scale {ls.transform.lossyScale}, player scale {lp.transform.localScale}, body bounds {body.bounds.size} centre {body.bounds.center - lp.transform.position}.");
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag light ({when}) failed: {e.Message}"); }
        }
        internal static float diagOrtho = -1f;     // diag: the replay camera takes the game camera's zoom

        private static int BitCount(ulong v) { int n = 0; while (v != 0) { v &= v - 1; n++; } return n; }

        private static void DiagShot() {
            if (diagShotAt < 0f || panelRoot == null) {
                if (diagSecond && panelRoot != null && Time.frameCount > diagSecondFrame) {
                    diagSecond = false;
                    viewT = diagIngameT2;
                    Animate();
                    diagSecondFrame = int.MaxValue;
                    diagSecondShot = Time.frameCount + 3;
                }
                if (diagSecondShot > 0 && Time.frameCount >= diagSecondShot) {
                    diagSecondShot = -1;
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSReplay_diag_dark.png"));
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag: dark comparison shot at {viewT:F2}s.");
                }
                return;
            }
            if (worldState == 1 && Time.realtimeSinceStartup < diagShotAt) return;
            if (worldState == 2 && diagShotAt - Time.realtimeSinceStartup > 13.5f) return;    // give the figures a frame or two
            diagShotAt = -1f;
            playing = false;
            if (diagIngameT >= 0f && PlayerControl.LocalPlayer != null) {
                viewT = diagIngameT; persp = PlayerControl.LocalPlayer.PlayerId; diagOrtho = diagIngameOrtho;
            }
            Animate();
            string shot = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSReplay_diag.png");
            ScreenCapture.CaptureScreenshot(shot);
            UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag: player opened ({tracks.Count} tracks, {Duration:F1}s), world state {worldState}, figures {figures.Count}, bodies {bodies.Count}, screenshot -> {shot}");
            if (diagIngameT2 >= 0f) { diagSecond = true; diagSecondFrame = Time.frameCount + 5; }
            UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag: host data {HostData}, events by kind: " +
                string.Join(", ", events.GroupBy(e => e.Kind).Select(g => $"{g.Key}x{g.Count()}")));
            foreach (var e in events.Where(e => e.Kind != EvMeeting).Take(20))
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag event {e.T:F1}s k{e.Kind}: {PlainText(e.Text)} | seen {(e.Seen == null ? "-" : string.Join(",", e.Seen))}");
            foreach (var t in tracks) {
                int k = t.R.Count - 1;
                if (k < 0) continue;
                int mid = k / 2;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag track {t.Name}: R {t.R[mid]:F2} -> {t.R[k]:F2}, sees {Convert.ToString((long)t.Sees[mid], 2)} -> {Convert.ToString((long)t.Sees[k], 2)}, looks {t.Looks.Count}, name dy {t.NameDy:F3}, hat '{t.OHat}'");
            }
        }
    }
}
