// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * EarlyDeathShieldUI - the host's lobby panel for the pink early-death shield, and the driver of
 * both the shield and the death-time clock.
 *
 * Every lobby player with their recorded rounds, their average survived share, and whether they get
 * the shield next round. The host can force the shield on or off per player; "Auto" hands the
 * decision back to the numbers. Same screen-space canvas shape as NewcomerShieldUI; its lobby
 * button sits one row above the newcomer's when both are shown.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public class EarlyDeathShieldUI : MonoBehaviour {
        public static EarlyDeathShieldUI Instance { get; private set; }

        public EarlyDeathShieldUI(IntPtr ptr) : base(ptr) { }

        private static readonly Dictionary<Color, Sprite> solidSprites = new Dictionary<Color, Sprite>();

        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.85f);
        private static readonly Color ColPanel = new Color(0.1f, 0.12f, 0.16f, 0.98f);
        private static readonly Color ColRow = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color ColAccent = new Color(1f, 0.45f, 0.85f);
        private static readonly Color ColShield = new Color(1f, 0.55f, 0.88f);
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
        private RectTransform lobbyButtonRect;
        private TMPro.TextMeshProUGUI lobbyButtonText;
        private float nextPoll;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        public void Update() {
            // The drivers, every frame and before any early return (both throttle themselves where
            // needed; the clock must not, it measures meeting boundaries).
            DeathTimeHistory.Tick();
            EarlyDeathShield.Tick();

            if (lobbyButton != null && lobbyButton.activeSelf && SettingsOverlayView.OverlayOpen()) {
                lobbyButton.SetActive(false);
                return;
            }

            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.5f;

            if (panelRoot != null && !LobbyScreen.Exists) Close();

            bool show = ShouldShow() && !SettingsOverlayView.OverlayOpen();
            if (show && lobbyButton == null) BuildLobbyButton();
            if (lobbyButton == null) return;
            if (lobbyButton.activeSelf != show) lobbyButton.SetActive(show);
            if (!show) return;
            if (lobbyButtonText != null) lobbyButtonText.text = ButtonLabel();
            // One row above the newcomer button while that one is shown, in its place otherwise.
            if (lobbyButtonRect != null)
                lobbyButtonRect.anchoredPosition = new Vector2(28, NewcomerShieldUI.ButtonShown ? 138 : 84);
        }

        [HideFromIl2Cpp]
        private bool ShouldShow() {
            try {
                if (!LobbyScreen.Exists) return false;
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return false;
                return EarlyDeathShield.Enabled != null && EarlyDeathShield.Enabled.getBool();
            } catch { return false; }
        }

        [HideFromIl2Cpp]
        private string ButtonLabel() =>
            UTSLocalization.Tr("uts.earlydeath.lobby_button", EarlyDeathShield.Evaluate().ShieldCount);

        [HideFromIl2Cpp]
        private void BuildLobbyButton() {
            try {
                lobbyButton = new GameObject("UTSEarlyDeathShieldButton");
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
                lobbyButtonRect = btn.AddComponent<RectTransform>();
                lobbyButtonRect.anchorMin = Vector2.zero; lobbyButtonRect.anchorMax = Vector2.zero;
                lobbyButtonRect.pivot = Vector2.zero;
                lobbyButtonRect.anchoredPosition = new Vector2(28, 84);
                lobbyButtonRect.sizeDelta = new Vector2(330, 46);
                btn.AddComponent<Image>().sprite = Solid(new Color(0.55f, 0.2f, 0.45f, 0.95f));

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
                UsefulTORStuffPlugin.Logger?.LogWarning($"[EarlyDeathShield] lobby button failed: {ex.Message}");
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
                // Newcomer and early-death panel share the screen centre: only one at a time.
                NewcomerShieldUI.Instance?.Close();

                panelRoot = new GameObject("UTSEarlyDeathShieldUI");
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

                var ev = EarlyDeathShield.Evaluate();
                float height = Mathf.Clamp(260 + ev.Rows.Count * 52, 350, 820);

                var panel = new GameObject("Panel");
                panel.transform.SetParent(panelRoot.transform, false);
                var prt = panel.AddComponent<RectTransform>();
                prt.anchorMin = new Vector2(0.5f, 0.5f); prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.pivot = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(900, height);
                panel.AddComponent<Image>().sprite = Solid(ColPanel);

                Label(panel, UTSLocalization.Tr("uts.earlydeath.title"), 28, TMPro.FontStyles.Bold,
                      ColAccent, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, -18), new Vector2(-40, 40), TMPro.TextAlignmentOptions.Center);
                Label(panel, UTSLocalization.Tr("uts.earlydeath.subtitle"), 14,
                      TMPro.FontStyles.Normal, ColMuted,
                      new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, -58), new Vector2(-60, 40), TMPro.TextAlignmentOptions.Top);

                string average = ev.HasAverage
                    ? UTSLocalization.Tr("uts.earlydeath.average",
                          Mathf.RoundToInt(ev.LobbyMean * 100f), ev.ThresholdPercent,
                          Mathf.RoundToInt(ev.LobbyMean * ev.ThresholdPercent))
                    : UTSLocalization.Tr("uts.earlydeath.no_average", 3, ev.MinRounds);
                Label(panel, average, 15, TMPro.FontStyles.Bold, Color.white,
                      new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                      new Vector2(0, -100), new Vector2(-60, 26), TMPro.TextAlignmentOptions.Top);

                float y = -134;
                foreach (var v in ev.Rows) {
                    BuildRow(panel, v, ev, y);
                    y -= 52;
                }

                MakeButton(panel, UTSLocalization.Tr("uts.earlydeath.close"),
                           new Vector2(0, 20), new Vector2(240, 44), ColBtnGrey, Close);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] panel failed: {ex}");
                Close();
            }
        }

        [HideFromIl2Cpp]
        private void BuildRow(GameObject parent, EarlyDeathShield.Verdict v, EarlyDeathShield.Evaluation ev, float y) {
            var holder = new GameObject("Row");
            holder.transform.SetParent(parent.transform, false);
            var rt = holder.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(-50, 46);
            holder.AddComponent<Image>().sprite = Solid(ColRow);

            Label(holder, v.Name, 17, TMPro.FontStyles.Bold, Color.white,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(14, 0), new Vector2(220, 0), TMPro.TextAlignmentOptions.Left);

            string stats = v.Qualified
                ? UTSLocalization.Tr("uts.earlydeath.stats", v.Stat.Rounds, Mathf.RoundToInt(v.Stat.Mean * 100f))
                : UTSLocalization.Tr("uts.earlydeath.stats_few", v.Stat.Rounds, ev.MinRounds);
            Label(holder, stats, 14, TMPro.FontStyles.Normal, v.Qualified ? Color.white : ColMuted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(240, 0), new Vector2(230, 0), TMPro.TextAlignmentOptions.Left);

            string state = v.Override == DeathTimeHistory.OverrideOn ? "uts.earlydeath.state_forced"
                         : v.Override == DeathTimeHistory.OverrideOff ? "uts.earlydeath.state_excluded"
                         : v.Shield ? "uts.earlydeath.state_auto" : "uts.earlydeath.state_none";
            Label(holder, UTSLocalization.Tr(state), 14, TMPro.FontStyles.Normal, v.Shield ? ColShield : ColMuted,
                  new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f),
                  new Vector2(476, 0), new Vector2(140, 0), TMPro.TextAlignmentOptions.Left);

            var captured = v;
            MakeButton(holder, UTSLocalization.Tr(v.Shield
                           ? "uts.earlydeath.btn_unprotect" : "uts.earlydeath.btn_protect"),
                       new Vector2(-14, 0), new Vector2(130, 36),
                       v.Shield ? new Color(0.5f, 0.25f, 0.25f, 0.95f) : new Color(0.55f, 0.2f, 0.45f, 0.95f),
                       () => { EarlyDeathShield.ToggleOverride(captured); Rebuild(); },
                       anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                       pivot: new Vector2(1, 0.5f));

            if (v.Override != DeathTimeHistory.OverrideAuto)
                MakeButton(holder, UTSLocalization.Tr("uts.earlydeath.btn_auto"),
                           new Vector2(-154, 0), new Vector2(76, 36), ColBtnGrey,
                           () => { EarlyDeathShield.ClearOverride(captured); Rebuild(); },
                           anchorMin: new Vector2(1, 0.5f), anchorMax: new Vector2(1, 0.5f),
                           pivot: new Vector2(1, 0.5f));
        }

        [HideFromIl2Cpp]
        private void Rebuild() {
            EarlyDeathShield.RefreshPreview();
            Close();
            Open();
        }

        // ---- tiny UGUI helpers (same shape as NewcomerShieldUI) ----
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
