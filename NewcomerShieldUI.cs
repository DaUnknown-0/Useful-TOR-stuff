// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * NewcomerShieldUI - the host's lobby panel for the newcomer kill shield.
 *
 * Shows every player in the lobby with the shield they would get next round, and lets the host flip
 * that by hand. The automatic rule (friend code never seen this session) covers the normal case;
 * this is for the ones it cannot know about - somebody who reinstalled, or a player the group simply
 * agrees should get a free round.
 *
 * Host only, and only in the lobby. A screen-space canvas like UTSModSyncUI, for the same reason:
 * there is no HudManager in the lobby screen, so the world-space overlay pattern does not apply.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public class NewcomerShieldUI : MonoBehaviour {
        public static NewcomerShieldUI Instance { get; private set; }

        public NewcomerShieldUI(IntPtr ptr) : base(ptr) { }

        private static readonly Dictionary<Color, Sprite> solidSprites = new Dictionary<Color, Sprite>();

        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.85f);
        private static readonly Color ColPanel = new Color(0.1f, 0.12f, 0.16f, 0.98f);
        private static readonly Color ColRow = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color ColAccent = new Color(0.45f, 0.85f, 1f);
        private static readonly Color ColShield = new Color(0.62f, 1f, 0.63f);
        private static readonly Color ColMuted = new Color(0.65f, 0.65f, 0.7f);
        private static readonly Color ColBtnGrey = new Color(0.3f, 0.3f, 0.38f, 0.95f);

        private static Sprite Solid(Color color) {
            if (solidSprites.TryGetValue(color, out var cached) && cached != null) return cached;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            DontDestroyOnLoad(tex);
            DontDestroyOnLoad(sprite);
            solidSprites[color] = sprite;
            return sprite;
        }

        private GameObject panelRoot;
        private GameObject lobbyButton;
        private TMPro.TextMeshProUGUI lobbyButtonText;
        private float nextPoll;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        // public, like every other Unity message in this plugin (see UTSModSyncUI).
        public void Update() {
            // The feature's own driver: lobby preview and round-start assignment. It lives on this
            // MonoBehaviour and NOT on a Harmony postfix precisely so no other mod's throwing patch
            // can ever keep it from running (see the NewcomerShield header). Every frame, before
            // this component's own poll throttle; Tick throttles itself.
            NewcomerShield.Tick();

            // The F1 settings overlay covers the whole screen and this button sits on top of its
            // text. Checked before the poll throttle below so it steps aside in the same frame F1 is
            // pressed instead of lingering for up to half a second.
            if (lobbyButton != null && lobbyButton.activeSelf && SettingsOverlayView.OverlayOpen()) {
                lobbyButton.SetActive(false);
                return;
            }

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;

            // LobbyScreen.Exists, never GameStartManager.Instance: that getter CONSTRUCTS a blank
            // GameStartManager when none exists (LobbyScreen in LobbyLeakGuard.cs has the whole
            // story), and this component polling it from boot onwards is how v1.3.3.15 planted the
            // phantom that degraded every session since.
            if (panelRoot != null && !LobbyScreen.Exists) Close();

            bool show = ShouldShow() && !SettingsOverlayView.OverlayOpen();
            if (show && lobbyButton == null) BuildLobbyButton();
            if (lobbyButton == null) return;
            if (lobbyButton.activeSelf != show) lobbyButton.SetActive(show);
            if (show && lobbyButtonText != null) lobbyButtonText.text = ButtonLabel();
        }

        [HideFromIl2Cpp]
        private bool ShouldShow() {
            try {
                if (!LobbyScreen.Exists) return false;                       // lobby only
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return false;
                return NewcomerShield.Enabled != null && NewcomerShield.Enabled.getBool();
            } catch { return false; }
        }

        [HideFromIl2Cpp]
        private int CountShielded() {
            int n = 0;
            try {
                foreach (var p in PlayerControl.AllPlayerControls.ToArray())
                    if (p != null && NewcomerShield.WouldShield(p)) n++;
            } catch { }
            return n;
        }

        [HideFromIl2Cpp]
        private string ButtonLabel() =>
            UTSLocalization.Tr("uts.newcomershield.lobby_button", CountShielded());

        [HideFromIl2Cpp]
        private void BuildLobbyButton() {
            try {
                lobbyButton = new GameObject("UTSNewcomerShieldButton");
                DontDestroyOnLoad(lobbyButton);

                var canvas = lobbyButton.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9000;
                var scaler = lobbyButton.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                lobbyButton.AddComponent<GraphicRaycaster>();

                var btn = new GameObject("Btn");
                btn.transform.SetParent(lobbyButton.transform, false);
                var rt = btn.AddComponent<RectTransform>();
                // Bottom left, one row above the mod sync button so the two never overlap.
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
                rt.anchoredPosition = new Vector2(28, 84);
                rt.sizeDelta = new Vector2(330, 46);
                btn.AddComponent<Image>().sprite = Solid(new Color(0.2f, 0.5f, 0.4f, 0.95f));

                var to = new GameObject("T");
                to.transform.SetParent(btn.transform, false);
                var trt = to.AddComponent<RectTransform>();
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.sizeDelta = Vector2.zero;
                lobbyButtonText = to.AddComponent<TMPro.TextMeshProUGUI>();
                lobbyButtonText.text = ButtonLabel();
                lobbyButtonText.fontSize = 18;
                lobbyButtonText.fontStyle = TMPro.FontStyles.Bold;
                lobbyButtonText.alignment = TMPro.TextAlignmentOptions.Center;
                lobbyButtonText.color = Color.white;

                btn.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)Toggle);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[NewcomerShield] lobby button failed: {ex.Message}");
                lobbyButton = null;
            }
        }

        [HideFromIl2Cpp]
        public void Toggle() {
            if (panelRoot != null) Close();
            else Open();
        }

        [HideFromIl2Cpp]
        public void Close() {
            if (panelRoot != null) { Destroy(panelRoot); panelRoot = null; }
        }

        [HideFromIl2Cpp]
        private void Open() {
            try {
                panelRoot = new GameObject("UTSNewcomerShieldUI");
                DontDestroyOnLoad(panelRoot);

                var canvas = panelRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9500;
                var scaler = panelRoot.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                panelRoot.AddComponent<GraphicRaycaster>().blockingObjects =
                    GraphicRaycaster.BlockingObjects.All;

                var backdrop = new GameObject("Backdrop");
                backdrop.transform.SetParent(panelRoot.transform, false);
                var brt = backdrop.AddComponent<RectTransform>();
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.sizeDelta = Vector2.zero;
                backdrop.AddComponent<Image>().sprite = Solid(ColBackdrop);
                backdrop.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)Close);

                var players = PlayerControl.AllPlayerControls.ToArray()
                    .Where(p => p != null && p.Data != null && !p.Data.Disconnected).ToList();
                float height = Mathf.Clamp(230 + players.Count * 52, 320, 780);

                var panel = new GameObject("Panel");
                panel.transform.SetParent(panelRoot.transform, false);
                var prt = panel.AddComponent<RectTransform>();
                prt.anchorMin = new Vector2(0.5f, 0.5f); prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.pivot = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(820, height);
                panel.AddComponent<Image>().sprite = Solid(ColPanel);

                Label(panel, UTSLocalization.Tr("uts.newcomershield.title"), 28, TMPro.FontStyles.Bold,
                      ColAccent, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, -18), new Vector2(-40, 40), TMPro.TextAlignmentOptions.Center);
                Label(panel, UTSLocalization.Tr("uts.newcomershield.subtitle"), 14,
                      TMPro.FontStyles.Normal, ColMuted,
                      new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, -58), new Vector2(-60, 40), TMPro.TextAlignmentOptions.Top);

                float y = -104;
                foreach (var p in players) {
                    BuildRow(panel, p, y);
                    y -= 52;
                }

                MakeButton(panel, UTSLocalization.Tr("uts.newcomershield.close"),
                           new Vector2(0, 20), new Vector2(240, 44), ColBtnGrey, Close);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] panel failed: {ex}");
                Close();
            }
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject parent, PlayerControl p, float y) {
            var holder = new GameObject("Row");
            holder.transform.SetParent(parent.transform, false);
            var rt = holder.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(-50, 46);
            holder.AddComponent<Image>().sprite = Solid(ColRow);

            string name = p.Data?.PlayerName ?? "?";
            Label(holder, name, 17, TMPro.FontStyles.Bold, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(14, 0), new Vector2(300, 0), TMPro.TextAlignmentOptions.Left);

            bool shielded = NewcomerShield.WouldShield(p);
            bool manual = NewcomerShield.IsManual(p);
            string state = shielded
                ? UTSLocalization.Tr(manual ? "uts.newcomershield.state_manual" : "uts.newcomershield.state_new")
                : UTSLocalization.Tr("uts.newcomershield.state_known");
            Label(holder, state, 14, TMPro.FontStyles.Normal, shielded ? ColShield : ColMuted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(322, 0), new Vector2(260, 0), TMPro.TextAlignmentOptions.Left);

            var captured = p;
            MakeButton(holder, UTSLocalization.Tr(shielded
                           ? "uts.newcomershield.btn_unprotect" : "uts.newcomershield.btn_protect"),
                       new Vector2(-14, 0), new Vector2(190, 36),
                       shielded ? new Color(0.5f, 0.25f, 0.25f, 0.95f) : new Color(0.2f, 0.55f, 0.3f, 0.95f),
                       () => { NewcomerShield.ToggleManual(captured); Rebuild(); },
                       anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                       pivot: new Vector2(1, 0.5f));
        }

        // The rows carry state, so a change redraws the whole panel rather than patching labels.
        [HideFromIl2Cpp]
        private void Rebuild() {
            Close();
            Open();
        }

        // ---- tiny UGUI helpers (same shape as UTSModSyncUI) ----
        [HideFromIl2Cpp]
        private static TMPro.TextMeshProUGUI Label(GameObject parent, string text, float size,
                TMPro.FontStyles style, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
                Vector2 pos, Vector2 sizeDelta, TMPro.TextAlignmentOptions align) {
            var go = new GameObject("L");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.fontStyle = style; t.color = color;
            t.alignment = align; t.enableWordWrapping = true;
            return t;
        }

        [HideFromIl2Cpp]
        private GameObject MakeButton(GameObject parent, string label, Vector2 pos, Vector2 size,
                Color color, Action onClick,
                Vector2? anchorMin = null, Vector2? anchorMax = null, Vector2? pivot = null) {
            var go = new GameObject("Btn");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin ?? new Vector2(0.5f, 0);
            rt.anchorMax = anchorMax ?? new Vector2(0.5f, 0);
            rt.pivot = pivot ?? new Vector2(0.5f, 0);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            go.AddComponent<Image>().sprite = Solid(color);

            var to = new GameObject("T");
            to.transform.SetParent(go.transform, false);
            var trt = to.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.sizeDelta = Vector2.zero;
            var t = to.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = label; t.fontSize = 15; t.fontStyle = TMPro.FontStyles.Bold;
            t.alignment = TMPro.TextAlignmentOptions.Center; t.color = Color.white;

            go.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
            return go;
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            public static void Postfix() { Instance?.Close(); }
        }
    }
}
