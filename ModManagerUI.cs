// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff
{
    // Vereinfachte Mod-Manager-UI mit formatiertem Text (keine custom Canvas-UI).
    // Zeigt Mod-Liste in bestehendem Popup-Template.
    public class ModManagerUI : MonoBehaviour
    {
        public static ModManagerUI Instance { get; private set; }
        public static bool IsUIOpen { get; private set; }

        public ModManagerUI(IntPtr ptr) : base(ptr) { }

        private GameObject _popup;

        // P1.2: Wiederverwendbarer 1×1-Solid-Color-Sprite-Cache + einmalig erzeugtes Overlay-
        // Material. Vorher erzeugte jedes Show() und jeder Toggle-Klick frische Texture2D/Sprite/
        // Material-Assets, die nie freigegeben wurden (Destroy(_popup) gibt Assets NICHT frei,
        // da Texturen/Sprites Assets und keine Kinder sind) → GPU/CPU-Leak über die ganze Session.
        // Diese Sprites leben prozessweit (DontDestroyOnLoad) und werden überall geteilt.
        private static readonly Dictionary<Color, Sprite> _solidSprites = new Dictionary<Color, Sprite>();
        private static Material _overlayMaterial;

        private static Sprite GetSolidSprite(Color color)
        {
            if (_solidSprites.TryGetValue(color, out var cached) && cached != null) return cached;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            UnityEngine.Object.DontDestroyOnLoad(tex);
            UnityEngine.Object.DontDestroyOnLoad(sprite);
            _solidSprites[color] = sprite;
            return sprite;
        }

        // Referenzen pro Mod-Zeile, damit die Polling-Coroutine den Download-Zustand live
        // aktualisieren kann (Fortschritt, "Restart required", Fehler) ohne Neuaufbau.
        private class ModEntryRefs
        {
            public ModInfo Mod;
            public TMPro.TextMeshProUGUI StatusText;
            public GameObject UpdateButton;
            public TMPro.TextMeshProUGUI UpdateButtonText;
            // Laufzeit-Zustand (beim Start geladen?) — bleibt über das Umschalten hinweg stabil,
            // während Mod.Enabled.Value den gewünschten Zustand nach Neustart abbildet.
            public bool RuntimeEnabled;
        }

        private readonly List<ModEntryRefs> _entryRefs = new List<ModEntryRefs>();

        // F2: "Update All" läuft sequentiell (die Updater sind Single-_busy-Automaten — nicht
        // parallelisieren). Header-Button + Summary-Text.
        private bool _updateAllRunning;
        private GameObject _updateAllButton;
        private TMPro.TextMeshProUGUI _updateAllButtonText;
        private TMPro.TextMeshProUGUI _headerSummaryText;

        // F2: Release-Notes für die Anzeige aufbereiten — crude Markdown-Strip auf die ersten
        // ~10 Zeilen / ~600 Zeichen, mit "…" bei Kürzung. Neutralisiert TMP-Rich-Text, damit die
        // Notes keine Tags ins Label injizieren können. Liefert "" wenn nichts anzuzeigen ist.
        private static string StripAndTruncateNotes(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var srcLines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var outLines = new List<string>();
            foreach (var lineRaw in srcLines)
            {
                string line = lineRaw.Trim();
                line = Regex.Replace(line, @"^#{1,6}\s*", "");        // ## Heading → Heading
                line = Regex.Replace(line, @"^[\*\-\+]\s+", "• ");      // - item → • item
                line = Regex.Replace(line, @"\[([^\]]+)\]\([^\)]*\)", "$1"); // [text](url) → text
                line = line.Replace("**", "").Replace("__", "").Replace("`", "");
                outLines.Add(line);
            }
            string joined = string.Join("\n", outLines).Trim();
            if (joined.Length == 0) return "";

            bool truncated = false;
            var keep = joined.Split('\n');
            if (keep.Length > 10) { joined = string.Join("\n", keep.Take(10)); truncated = true; }
            if (joined.Length > 600) { joined = joined.Substring(0, 600).TrimEnd(); truncated = true; }

            // TMP-Tags neutralisieren: ein Zero-Width-Space hinter '<' verhindert Tag-Parsing.
            joined = joined.Replace("<", "<​");
            if (truncated) joined += " …";
            return joined;
        }

        public void Awake()
        {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        public void Show()
        {
            try
            {
                if (_popup != null)
                {
                    UsefulTORStuffPlugin.Logger?.LogWarning("Mod Manager is already open.");
                    return;
                }

                // Beim Öffnen erneut auf neue Versionen prüfen (gedrosselt auf 1×/Minute).
                // Das Ergebnis erscheint automatisch über die laufende CoRefreshStates-Schleife.
                ModManagerRegistry.MaybeCheckForUpdates();

                CreateProfessionalUI();
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to show Mod Manager UI: {ex}");
            }
        }

        private void CreateProfessionalUI()
        {
            try
            {
                // 1. Root Canvas
                _popup = new GameObject("ModManagerUI");
                DontDestroyOnLoad(_popup);

                var canvas = _popup.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9999; // Very high to be above everything

                var scaler = _popup.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                var raycaster = _popup.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                raycaster.blockingObjects = UnityEngine.UI.GraphicRaycaster.BlockingObjects.All;

                // 2. Fullscreen Overlay (blocks all interactions behind)
                CreateOverlay();

                // 3. Main Panel
                CreateMainPanel();

                _popup.SetActive(true);

                // Set flag that UI is open
                IsUIOpen = true;

                // Disable interactions with UI behind
                DisableBackgroundUI();

                // Poll laufende Downloads und aktualisiere die Anzeige der Mod-Zeilen.
                this.StartCoroutine(CoRefreshStates());

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager: Professional UI created successfully");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to create professional UI: {ex}");
                if (_popup != null)
                {
                    Destroy(_popup);
                    _popup = null;
                }
            }
        }

        private List<GameObject> _hiddenObjects = new List<GameObject>();
        private List<PassiveButton> _disabledButtons = new List<PassiveButton>();

        // P2.5: HEURISTIK, KEINE exakte Auswahl. Versteckt JEDES Objekt, dessen Name
        // "update"/"button"/"popup"/"dialog"/"confirm" enthält, um Hintergrund-UI hinter dem
        // Mod Manager zu deaktivieren; alles wird in EnableBackgroundUI() beim Schließen
        // wiederhergestellt. Achtung für künftige Maintainer: benennt das Spiel/TOR Elemente um
        // oder kollidieren neue Namen mit diesen Substrings, kann zu viel/zu wenig versteckt
        // werden. Objekte unter _popup werden hier explizit ausgenommen, damit der Manager sich
        // nie selbst deaktiviert.
        private void DisableBackgroundUI()
        {
            _hiddenObjects.Clear();
            _disabledButtons.Clear();

            // Find and hide ALL Canvas elements that are not our mod manager
            var allCanvases = GameObject.FindObjectsOfType<Canvas>();
            foreach (var canvas in allCanvases)
            {
                if (canvas.gameObject != _popup && canvas.sortingOrder < 9999)
                {
                    // Hide any canvas that might contain updater buttons or popups
                    if (canvas.gameObject.activeInHierarchy)
                    {
                        var allChildren = canvas.GetComponentsInChildren<Transform>(true);
                        foreach (var child in allChildren)
                        {
                            // Niemals etwas unter unserem eigenen Popup verstecken.
                            if (_popup != null && child.IsChildOf(_popup.transform)) continue;
                            if (child.gameObject.activeInHierarchy &&
                                (child.name.ToLower().Contains("update") ||
                                 child.name.ToLower().Contains("button") ||
                                 child.name.ToLower().Contains("popup") ||
                                 child.name.ToLower().Contains("dialog") ||
                                 child.name.ToLower().Contains("confirm")))
                            {
                                child.gameObject.SetActive(false);
                                _hiddenObjects.Add(child.gameObject);
                            }
                        }
                    }
                }
            }

            // Disable ALL PassiveButtons that are not part of the mod manager
            var allButtons = GameObject.FindObjectsOfType<PassiveButton>();
            foreach (var button in allButtons)
            {
                if (!button.transform.IsChildOf(_popup.transform) && button.enabled)
                {
                    button.enabled = false;
                    _disabledButtons.Add(button);
                }
            }

            UsefulTORStuffPlugin.Logger?.LogInfo($"Mod Manager: Disabled background UI ({_hiddenObjects.Count} objects hidden, {_disabledButtons.Count} buttons disabled)");
        }

        private void EnableBackgroundUI()
        {
            try
            {
                // Re-enable only the buttons we disabled
                foreach (var button in _disabledButtons)
                {
                    if (button != null)
                    {
                        button.enabled = true;
                    }
                }
                _disabledButtons.Clear();

                // Re-enable all hidden objects
                foreach (var go in _hiddenObjects)
                {
                    if (go != null)
                    {
                        go.SetActive(true);
                    }
                }
                _hiddenObjects.Clear();

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager: Re-enabled background UI");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Error re-enabling background UI: {ex}");
            }
        }

        private void CreateOverlay()
        {
            var overlay = new GameObject("Overlay");
            overlay.transform.SetParent(_popup.transform, false);

            var overlayRect = overlay.AddComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.sizeDelta = Vector2.zero;

            // Use CanvasRenderer for a simple colored quad
            var canvasRenderer = overlay.AddComponent<CanvasRenderer>();
            // P1.2: geteilte Textur aus dem Sprite-Cache + einmalig erzeugtes Material.
            var texture = GetSolidSprite(new Color(0, 0, 0, 0.85f)).texture;
            if (_overlayMaterial == null)
            {
                _overlayMaterial = new Material(Shader.Find("UI/Default"));
                UnityEngine.Object.DontDestroyOnLoad(_overlayMaterial);
            }
            _overlayMaterial.mainTexture = texture;

            canvasRenderer.SetMaterial(_overlayMaterial, texture);
            canvasRenderer.SetColor(Color.white);

            // Click to close
            var button = overlay.AddComponent<UnityEngine.UI.Button>();
            button.onClick.AddListener((UnityEngine.Events.UnityAction)Hide);

            UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager: Overlay created");
        }

        private void CreateMainPanel()
        {
            var panel = new GameObject("MainPanel");
            panel.transform.SetParent(_popup.transform, false);

            var panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(900, 700);

            // Panel background
            var panelBg = panel.AddComponent<UnityEngine.UI.Image>();
            panelBg.sprite = GetSolidSprite(new Color(0.1f, 0.1f, 0.15f, 0.98f));

            // Title
            CreateTitle(panel);

            // F2: "Update All" header button + summary line.
            CreateUpdateAllButton(panel);

            // Shared "show test versions" toggle (top-right). Flips the process-wide flag read by every
            // mod's vX.Y.Z(.W) version line; display-only, no effect on stable builds (no 4th component).
            CreateTestVersionToggle(panel);

            // Content
            CreateContent(panel);

            // Close button
            CreateCloseButton(panel);

            UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager: Main panel created");
        }

        private void CreateTitle(GameObject parent)
        {
            var title = new GameObject("Title");
            title.transform.SetParent(parent.transform, false);

            var titleRect = title.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.pivot = new Vector2(0.5f, 1);
            titleRect.anchoredPosition = new Vector2(0, -20);
            titleRect.sizeDelta = new Vector2(-40, 60);

            var titleText = title.AddComponent<TMPro.TextMeshProUGUI>();
            titleText.text = "MOD MANAGER";
            titleText.fontSize = 36;
            titleText.fontStyle = TMPro.FontStyles.Bold;
            titleText.alignment = TMPro.TextAlignmentOptions.Center;
            titleText.color = new Color(0.3f, 0.7f, 1f);
        }

        private GameObject _testVersionToggle;
        private TMPro.TextMeshProUGUI _testVersionToggleText;
        private GameObject _confirmOverlay;

        // Number of running mods that have a release in the requested channel (stable=true / test=false).
        private int CountChannelMods(bool stable)
        {
            int n = 0;
            try {
                foreach (var m in ModManagerRegistry.GetAllMods())
                    try { if (m.RuntimeEnabled && (m.HasChannelRelease?.Invoke(stable) ?? false)) n++; } catch { }
            } catch { }
            return n;
        }

        // Switch every eligible mod to its newest release of the given channel, sequentially (each
        // updater is a single-busy state machine), waiting for success (2) or error (3) before the next.
        // A deliberate, possibly DOWNgrading channel switch — not a version-gated update.
        private IEnumerator CoSwitchChannel(bool stable)
        {
            if (_updateAllRunning) yield break;
            _updateAllRunning = true;
            RefreshUpdateAllButton();

            List<ModInfo> mods;
            try { mods = ModManagerRegistry.GetAllMods(); } catch { mods = new List<ModInfo>(); }
            int done = 0, failed = 0;
            foreach (var mod in mods)
            {
                bool has = false;
                try { has = mod.RuntimeEnabled && (mod.HasChannelRelease?.Invoke(stable) ?? false); } catch { }
                if (!has || mod.TriggerChannelSwitch == null || mod.GetUpdateState == null) continue;

                try { mod.TriggerChannelSwitch(stable); }
                catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"Channel switch trigger failed for {mod.Name}: {ex.Message}");
                    failed++;
                    continue;
                }

                float timeout = 90f;
                int state = 0;
                while (timeout > 0f)
                {
                    try { state = mod.GetUpdateState?.Invoke() ?? 0; } catch { state = 3; }
                    if (state == 2 || state == 3) break;
                    timeout -= Time.deltaTime;
                    yield return null;
                }
                if (state == 2) done++; else failed++;
            }

            _updateAllRunning = false;
            RefreshUpdateAllButton();
            if (_headerSummaryText != null)
                _headerSummaryText.text = failed == 0
                    ? $"<color=#9EFFA0>{done} mod(s) set to {(stable ? "Stable" : "Test")} - please restart the game.</color>"
                    : $"<color=#FFD27F>{done} ok, {failed} failed - please restart the game.</color>";
        }

        // Simple modal confirmation overlay (dim background + box with message + Ja/Abbrechen).
        private void ShowConfirm(string title, string message, Action onYes)
        {
            HideConfirm();
            if (_popup == null) { onYes?.Invoke(); return; } // no UI root -> just proceed
            _confirmOverlay = new GameObject("ConfirmOverlay");
            _confirmOverlay.transform.SetParent(_popup.transform, false);
            var ort = _confirmOverlay.AddComponent<RectTransform>();
            ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one; ort.sizeDelta = Vector2.zero; ort.anchoredPosition = Vector2.zero;
            var dim = _confirmOverlay.AddComponent<UnityEngine.UI.Image>();
            dim.sprite = GetSolidSprite(new Color(0f, 0f, 0f, 0.6f));
            _confirmOverlay.AddComponent<UnityEngine.UI.Button>(); // swallow clicks behind the box

            var box = new GameObject("Box"); box.transform.SetParent(_confirmOverlay.transform, false);
            var brt = box.AddComponent<RectTransform>();
            brt.anchorMin = new Vector2(0.5f, 0.5f); brt.anchorMax = new Vector2(0.5f, 0.5f); brt.pivot = new Vector2(0.5f, 0.5f);
            brt.sizeDelta = new Vector2(540, 280); brt.anchoredPosition = Vector2.zero;
            box.AddComponent<UnityEngine.UI.Image>().sprite = GetSolidSprite(new Color(0.12f, 0.12f, 0.18f, 0.99f));

            var t = new GameObject("Title"); t.transform.SetParent(box.transform, false);
            var trt = t.AddComponent<RectTransform>(); trt.anchorMin = new Vector2(0, 1); trt.anchorMax = new Vector2(1, 1); trt.pivot = new Vector2(0.5f, 1); trt.anchoredPosition = new Vector2(0, -14); trt.sizeDelta = new Vector2(-24, 40);
            var tt = t.AddComponent<TMPro.TextMeshProUGUI>(); tt.text = title; tt.fontSize = 22; tt.fontStyle = TMPro.FontStyles.Bold; tt.alignment = TMPro.TextAlignmentOptions.Center; tt.color = new Color(0.3f, 0.7f, 1f);

            var m = new GameObject("Msg"); m.transform.SetParent(box.transform, false);
            var mrt = m.AddComponent<RectTransform>(); mrt.anchorMin = Vector2.zero; mrt.anchorMax = Vector2.one; mrt.offsetMin = new Vector2(18, 66); mrt.offsetMax = new Vector2(-18, -56);
            var mt = m.AddComponent<TMPro.TextMeshProUGUI>(); mt.text = message; mt.fontSize = 15; mt.alignment = TMPro.TextAlignmentOptions.Top; mt.color = Color.white; mt.enableWordWrapping = true;

            MakeConfirmButton(box, "Yes", new Vector2(-95, 16), new Color(0.2f, 0.6f, 0.25f, 0.95f), () => { HideConfirm(); onYes?.Invoke(); });
            MakeConfirmButton(box, "Cancel", new Vector2(95, 16), new Color(0.5f, 0.2f, 0.2f, 0.95f), () => HideConfirm());
        }

        private void MakeConfirmButton(GameObject parent, string label, Vector2 anchoredPos, Color col, Action onClick)
        {
            var b = new GameObject("Btn" + label); b.transform.SetParent(parent.transform, false);
            var rt = b.AddComponent<RectTransform>(); rt.anchorMin = new Vector2(0.5f, 0); rt.anchorMax = new Vector2(0.5f, 0); rt.pivot = new Vector2(0.5f, 0); rt.sizeDelta = new Vector2(160, 40); rt.anchoredPosition = anchoredPos;
            b.AddComponent<UnityEngine.UI.Image>().sprite = GetSolidSprite(col);
            var to = new GameObject("T"); to.transform.SetParent(b.transform, false);
            var trt = to.AddComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.sizeDelta = Vector2.zero;
            var tx = to.AddComponent<TMPro.TextMeshProUGUI>(); tx.text = label; tx.fontSize = 16; tx.fontStyle = TMPro.FontStyles.Bold; tx.alignment = TMPro.TextAlignmentOptions.Center; tx.color = Color.white;
            b.AddComponent<UnityEngine.UI.Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
        }

        private void HideConfirm()
        {
            if (_confirmOverlay != null) { UnityEngine.Object.Destroy(_confirmOverlay); _confirmOverlay = null; }
        }

        // Top-right toggle for the shared "show test versions" flag. Display-only: it controls whether
        // the 4th version component (.W on test builds) is shown in every mod's version line. Persists
        // via UsefulTORStuffPlugin.ShowTestVersionsConfig so the choice survives restarts.
        private void CreateTestVersionToggle(GameObject parent)
        {
            var button = new GameObject("TestVersionToggle");
            button.transform.SetParent(parent.transform, false);
            var btnRect = button.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-20, -18);
            btnRect.sizeDelta = new Vector2(210, 34);

            var btnBg = button.AddComponent<UnityEngine.UI.Image>();
            btnBg.sprite = GetSolidSprite(new Color(0.25f, 0.25f, 0.35f, 0.9f));

            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(button.transform, false);
            var btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;
            _testVersionToggleText = btnTextObj.AddComponent<TMPro.TextMeshProUGUI>();
            _testVersionToggleText.fontSize = 14;
            _testVersionToggleText.fontStyle = TMPro.FontStyles.Bold;
            _testVersionToggleText.alignment = TMPro.TextAlignmentOptions.Center;
            UpdateTestVersionToggleText();

            var btnComponent = button.AddComponent<UnityEngine.UI.Button>();
            btnComponent.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                if (_updateAllRunning) return; // ein Download/Wechsel läuft bereits
                bool nv = !VersionDisplay.ShowTestVersions();
                bool stableChannel = !nv;            // AUS -> Stable, AN -> Test
                string ch = stableChannel ? "STABLE" : "TEST";
                int affected = CountChannelMods(stableChannel);
                string msg = affected > 0
                    ? $"Test versions {(nv ? "ON" : "OFF")}: {affected} mod(s) will be downloaded and installed to their latest {ch} release (replacing the current build). The game must be restarted afterwards.\n\nContinue?"
                    : $"Test versions {(nv ? "ON" : "OFF")}: No matching {ch} release found - only the display is switched.\n\nContinue?";
                ShowConfirm("Switch Test Versions", msg, () => {
                    VersionDisplay.SetShowTestVersions(nv);
                    if (UsefulTORStuffPlugin.ShowTestVersionsConfig != null)
                        UsefulTORStuffPlugin.ShowTestVersionsConfig.Value = nv;
                    UpdateTestVersionToggleText();
                    if (affected > 0) this.StartCoroutine(CoSwitchChannel(stableChannel));
                });
            }));
            _testVersionToggle = button;
        }

        private void UpdateTestVersionToggleText()
        {
            if (_testVersionToggleText == null) return;
            bool on = VersionDisplay.ShowTestVersions();
            _testVersionToggleText.text = on ? "Test Versions: ON" : "Test Versions: OFF";
            _testVersionToggleText.color = on ? new Color(0.4f, 1f, 0.5f) : new Color(0.8f, 0.8f, 0.8f);
        }

        // F2: "Update All" header button (top-left) + a summary line. The button is enabled only
        // when ≥1 registered mod reports an available update; clicking it runs the per-mod downloads
        // sequentially (the updaters are single-_busy state machines).
        private void CreateUpdateAllButton(GameObject parent)
        {
            var button = new GameObject("UpdateAllButton");
            button.transform.SetParent(parent.transform, false);
            var btnRect = button.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0, 1);
            btnRect.anchorMax = new Vector2(0, 1);
            btnRect.pivot = new Vector2(0, 1);
            btnRect.anchoredPosition = new Vector2(20, -18);
            btnRect.sizeDelta = new Vector2(170, 34);

            var btnBg = button.AddComponent<UnityEngine.UI.Image>();
            btnBg.sprite = GetSolidSprite(new Color(0.2f, 0.6f, 1f, 0.9f));

            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(button.transform, false);
            var btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;
            _updateAllButtonText = btnTextObj.AddComponent<TMPro.TextMeshProUGUI>();
            _updateAllButtonText.text = "UPDATE ALL";
            _updateAllButtonText.fontSize = 15;
            _updateAllButtonText.fontStyle = TMPro.FontStyles.Bold;
            _updateAllButtonText.alignment = TMPro.TextAlignmentOptions.Center;
            _updateAllButtonText.color = Color.white;

            var btnComponent = button.AddComponent<UnityEngine.UI.Button>();
            btnComponent.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                if (_updateAllRunning) return;
                this.StartCoroutine(CoUpdateAll());
            }));
            _updateAllButton = button;

            // Summary line under the title.
            var sumObj = new GameObject("UpdateAllSummary");
            sumObj.transform.SetParent(parent.transform, false);
            var sumRect = sumObj.AddComponent<RectTransform>();
            sumRect.anchorMin = new Vector2(0, 1);
            sumRect.anchorMax = new Vector2(1, 1);
            sumRect.pivot = new Vector2(0.5f, 1);
            sumRect.anchoredPosition = new Vector2(0, -56);
            sumRect.sizeDelta = new Vector2(-40, 22);
            _headerSummaryText = sumObj.AddComponent<TMPro.TextMeshProUGUI>();
            _headerSummaryText.text = "";
            _headerSummaryText.fontSize = 15;
            _headerSummaryText.alignment = TMPro.TextAlignmentOptions.Center;
            _headerSummaryText.color = new Color(1f, 1f, 0.6f);

            RefreshUpdateAllButton();
        }

        // Enables the "Update All" button only when ≥1 mod has an update (and not mid-run).
        private void RefreshUpdateAllButton()
        {
            if (_updateAllButton == null) return;
            bool any = false;
            try { any = ModManagerRegistry.GetAllMods().Any(m => { try { return m.RuntimeEnabled && (m.HasUpdate?.Invoke() ?? false); } catch { return false; } }); }
            catch { }
            var b = _updateAllButton.GetComponent<UnityEngine.UI.Button>();
            if (b != null) b.interactable = any && !_updateAllRunning;
            if (_updateAllButtonText != null)
                _updateAllButtonText.color = (any && !_updateAllRunning) ? Color.white : new Color(0.6f, 0.6f, 0.6f);
        }

        // F2: download every updatable mod's release SEQUENTIALLY, then show one summary line. Each
        // updater is a single-_busy state machine, so we wait for one to finish (state 2/3) before
        // starting the next. Resilient: a mod whose check/download failed is counted and skipped.
        private IEnumerator CoUpdateAll()
        {
            if (_updateAllRunning) yield break;
            _updateAllRunning = true;
            RefreshUpdateAllButton();
            if (_updateAllButtonText != null) _updateAllButtonText.text = "UPDATING…";

            int updated = 0, failed = 0;
            List<ModInfo> mods;
            try { mods = ModManagerRegistry.GetAllMods(); }
            catch { mods = new List<ModInfo>(); }

            foreach (var mod in mods)
            {
                bool has = false;
                try { has = mod.RuntimeEnabled && (mod.HasUpdate?.Invoke() ?? false); } catch { }
                if (!has || mod.TriggerUpdate == null || mod.GetUpdateState == null) continue;

                // Already-succeeded mod (state 2) from a previous per-entry click: count, don't re-run.
                int pre = 0; try { pre = mod.GetUpdateState(); } catch { }
                if (pre == 2) { updated++; continue; }

                try { mod.TriggerUpdate(); }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"Update All: trigger failed for {mod.Name}: {ex.Message}");
                    failed++;
                    continue;
                }

                // Wait for this mod to reach success (2) or error (3), up to a timeout.
                float timeout = 90f;
                int state = 0;
                while (timeout > 0f)
                {
                    try { state = mod.GetUpdateState?.Invoke() ?? 0; } catch { state = 3; }
                    if (state == 2 || state == 3) break;
                    timeout -= Time.deltaTime;
                    yield return null;
                }
                if (state == 2) updated++;
                else failed++;
            }

            _updateAllRunning = false;
            if (_headerSummaryText != null)
            {
                if (updated == 0 && failed == 0)
                    _headerSummaryText.text = "Nothing to update";
                else
                    _headerSummaryText.text = failed == 0
                        ? $"{updated} updated — restart required"
                        : $"{updated} updated, {failed} failed — restart required";
            }
            if (_updateAllButtonText != null) _updateAllButtonText.text = "UPDATE ALL";
            RefreshUpdateAllButton();
        }

        // Breite des Scrollbalken-Streifens rechts (Spur + Abstand). Der Viewport wird um
        // diesen Betrag schmaler, damit Mod-Inhalte nie unter der Scrollbar liegen.
        private const float ScrollbarWidth = 14f;

        private void CreateContent(GameObject parent)
        {
            // ScrollView = Wurzel der scrollbaren Flaeche. Traegt die ScrollRect-Logik.
            var scrollView = new GameObject("ScrollView");
            scrollView.transform.SetParent(parent.transform, false);

            var scrollViewRect = scrollView.AddComponent<RectTransform>();
            scrollViewRect.anchorMin = new Vector2(0, 0);
            scrollViewRect.anchorMax = new Vector2(1, 1);
            scrollViewRect.offsetMin = new Vector2(20, 80);
            scrollViewRect.offsetMax = new Vector2(-20, -90);

            var scrollRect = scrollView.AddComponent<UnityEngine.UI.ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 35f; // Mausrad-Tempo
            scrollRect.inertia = false;

            // Viewport = sichtbarer Ausschnitt, clippt den Inhalt per RectMask2D.
            // Rechts um die Scrollbar-Breite eingerueckt, damit Buttons frei bleiben.
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollView.transform, false);
            var viewportRect = viewport.AddComponent<RectTransform>();
            viewportRect.anchorMin = new Vector2(0, 0);
            viewportRect.anchorMax = new Vector2(1, 1);
            viewportRect.pivot = new Vector2(0, 1);
            viewportRect.offsetMin = new Vector2(0, 0);
            viewportRect.offsetMax = new Vector2(-ScrollbarWidth, 0);
            viewport.AddComponent<UnityEngine.UI.RectMask2D>();

            // Content = bewegter Container mit den Mod-Zeilen.
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);

            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 0);

            // Vertikale Scrollbar rechts im freigehaltenen Streifen.
            var scrollbar = CreateScrollbar(scrollView);

            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.AutoHide;

            // Add mod entries
            _entryRefs.Clear();
            var mods = ModManagerRegistry.GetAllMods();
            float yPos = -10;

            foreach (var mod in mods)
            {
                yPos = CreateModEntry(content, mod, yPos);
            }

            // Gesamthoehe des Inhalts. Ist sie groesser als der Viewport, wird automatisch
            // gescrollt (Mausrad oder Scrollbar). So skaliert das UI mit beliebig vielen Mods.
            contentRect.sizeDelta = new Vector2(0, Mathf.Abs(yPos) + 20);

            // Nach oben scrollen (erste Mod sichtbar), sobald das Layout steht.
            scrollRect.verticalNormalizedPosition = 1f;
        }

        // Baut eine schlichte vertikale Scrollbar (Spur + Griff) und gibt die Komponente zurueck.
        private UnityEngine.UI.Scrollbar CreateScrollbar(GameObject scrollView)
        {
            var bar = new GameObject("Scrollbar");
            bar.transform.SetParent(scrollView.transform, false);

            var barRect = bar.AddComponent<RectTransform>();
            barRect.anchorMin = new Vector2(1, 0);
            barRect.anchorMax = new Vector2(1, 1);
            barRect.pivot = new Vector2(1, 1);
            barRect.sizeDelta = new Vector2(ScrollbarWidth - 2f, 0);
            barRect.anchoredPosition = Vector2.zero;

            var barBg = bar.AddComponent<UnityEngine.UI.Image>();
            barBg.color = new Color(0.08f, 0.08f, 0.12f, 0.9f);

            // Sliding-Area + Griff
            var slidingArea = new GameObject("SlidingArea");
            slidingArea.transform.SetParent(bar.transform, false);
            var slidingRect = slidingArea.AddComponent<RectTransform>();
            slidingRect.anchorMin = Vector2.zero;
            slidingRect.anchorMax = Vector2.one;
            slidingRect.sizeDelta = Vector2.zero;
            slidingRect.anchoredPosition = Vector2.zero;

            var handle = new GameObject("Handle");
            handle.transform.SetParent(slidingArea.transform, false);
            var handleRect = handle.AddComponent<RectTransform>();
            handleRect.sizeDelta = Vector2.zero;
            var handleImg = handle.AddComponent<UnityEngine.UI.Image>();
            handleImg.color = new Color(0.4f, 0.5f, 0.7f, 0.95f);

            var scrollbar = bar.AddComponent<UnityEngine.UI.Scrollbar>();
            scrollbar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImg;

            return scrollbar;
        }

        private float CreateModEntry(GameObject parent, ModInfo mod, float yPos)
        {
            var entry = new GameObject($"Mod_{mod.Guid}");
            entry.transform.SetParent(parent.transform, false);

            var entryRect = entry.AddComponent<RectTransform>();
            entryRect.anchorMin = new Vector2(0, 1);
            entryRect.anchorMax = new Vector2(1, 1);
            entryRect.pivot = new Vector2(0.5f, 1);
            entryRect.anchoredPosition = new Vector2(0, yPos);
            entryRect.sizeDelta = new Vector2(-20, 140);

            // Anzeige basiert auf dem Laufzeit-Zustand (läuft der Mod gerade?), nicht auf dem
            // Config-Wert: nach einem Toggle läuft der Mod bis zum Neustart weiter und soll auch
            // bis dahin als aktiv erscheinen (nur mit "Neustart erforderlich"-Hinweis).
            bool runtimeEnabled = mod.RuntimeEnabled;
            bool configEnabled = mod.Enabled?.Value ?? true;

            // Background
            var bg = entry.AddComponent<UnityEngine.UI.Image>();
            bg.sprite = GetSolidSprite(runtimeEnabled ? new Color(0.15f, 0.2f, 0.15f, 0.8f) : new Color(0.2f, 0.15f, 0.15f, 0.6f));

            // Mod name + version
            var nameObj = new GameObject("Name");
            nameObj.transform.SetParent(entry.transform, false);
            var nameRect = nameObj.AddComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(0.7f, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.anchoredPosition = new Vector2(15, -10);
            nameRect.sizeDelta = new Vector2(0, 30);

            var nameText = nameObj.AddComponent<TMPro.TextMeshProUGUI>();
            string statusIcon = runtimeEnabled ? "[ON]" : "[OFF]";
            nameText.text = $"{statusIcon} <b>{mod.Name}</b> <size=70%>v{mod.Version}</size>";
            nameText.fontSize = 24;
            nameText.alignment = TMPro.TextAlignmentOptions.Left;
            nameText.color = runtimeEnabled ? mod.ButtonColor : Color.gray;

            var statusObj = new GameObject("Status");
            statusObj.transform.SetParent(entry.transform, false);
            var statusRect = statusObj.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(0.6f, 1);
            statusRect.pivot = new Vector2(0, 1);
            statusRect.anchoredPosition = new Vector2(15, -45);
            statusRect.sizeDelta = new Vector2(0, 20);

            var statusText = statusObj.AddComponent<TMPro.TextMeshProUGUI>();
            statusText.fontSize = 16;

            // Referenzen für das Live-Polling sammeln.
            var refs = new ModEntryRefs { Mod = mod, StatusText = statusText, RuntimeEnabled = runtimeEnabled };

            // Enable/Disable toggle button. originalValue = Laufzeit-Zustand, damit ein Zurück-
            // Toggeln den "Neustart erforderlich"-Hinweis wieder aufhebt.
            CreateToggleButton(entry, mod, runtimeEnabled, configEnabled);

            // Update-Button immer anlegen (startet inaktiv); RefreshEntry blendet ihn je nach
            // Update-/Download-Zustand ein. So erscheinen auch spät geladene Releases live (Bug 3).
            CreateUpdateButton(entry, mod, refs);

            // Optionaler Live-Toggle (z. B. HostFix' Snitch-Fallback). Wirkt sofort, kein Neustart.
            if (mod.ExtraToggle != null)
                CreateExtraToggleButton(entry, mod);

            _entryRefs.Add(refs);

            // Status-Text und Update-Button sofort in den korrekten Zustand bringen.
            RefreshEntry(refs);

            // F2: Release-Notes der neuesten Version anzeigen, wenn ein Update verfügbar ist und der
            // Updater die Notes liefert (ältere installierte Updater haben GetReleaseNotes nicht →
            // dann einfach ausgeblendet statt Fehler). Notes kommen aus dem bereits geladenen JSON.
            float notesHeight = 0f;
            bool updateAvail = false;
            try { updateAvail = mod.HasUpdate?.Invoke() ?? false; } catch { }
            if (updateAvail && mod.GetReleaseNotes != null)
            {
                string rawNotes = null;
                try { rawNotes = mod.GetReleaseNotes(); } catch { }
                string notes = StripAndTruncateNotes(rawNotes);
                if (notes.Length > 0)
                {
                    int lineCount = notes.Split('\n').Length;
                    notesHeight = Mathf.Clamp(lineCount * 18f + 26f, 50f, 200f);

                    var notesObj = new GameObject("ReleaseNotes");
                    notesObj.transform.SetParent(entry.transform, false);
                    var notesRect = notesObj.AddComponent<RectTransform>();
                    notesRect.anchorMin = new Vector2(0, 1);
                    notesRect.anchorMax = new Vector2(1, 1);
                    notesRect.pivot = new Vector2(0, 1);
                    notesRect.anchoredPosition = new Vector2(15, -72);
                    notesRect.sizeDelta = new Vector2(-30, notesHeight);

                    var notesText = notesObj.AddComponent<TMPro.TextMeshProUGUI>();
                    notesText.text = "<b>What's new:</b>\n" + notes;
                    notesText.fontSize = 13;
                    notesText.color = new Color(0.82f, 0.82f, 0.86f);
                    notesText.alignment = TMPro.TextAlignmentOptions.TopLeft;
                    notesText.enableWordWrapping = true;
                    notesText.overflowMode = TMPro.TextOverflowModes.Truncate;
                }
            }
            // Entry-Höhe an die Notes anpassen (Basis 140); repo/guid sind bodengeankert und
            // rutschen mit der neuen Unterkante mit.
            entryRect.sizeDelta = new Vector2(-20, 140 + notesHeight);

            // GitHub link button — nur fuer Mods mit hinterlegtem Repository.
            // Lokale Mods (kein GitHub) bekommen keinen "Open GitHub"-Button.
            if (HasRepository(mod))
                CreateGitHubButton(entry, mod);

            // Repository
            var repoObj = new GameObject("Repo");
            repoObj.transform.SetParent(entry.transform, false);
            var repoRect = repoObj.AddComponent<RectTransform>();
            repoRect.anchorMin = new Vector2(0, 0);
            repoRect.anchorMax = new Vector2(1, 0);
            repoRect.pivot = new Vector2(0, 0);
            repoRect.anchoredPosition = new Vector2(15, 35);
            repoRect.sizeDelta = new Vector2(-30, 15);

            var repoText = repoObj.AddComponent<TMPro.TextMeshProUGUI>();
            repoText.text = HasRepository(mod)
                ? $"Repository: {mod.RepositoryOwner}/{mod.RepositoryName}"
                : "Lokale Mod (kein GitHub)";
            repoText.fontSize = 14;
            repoText.color = new Color(0.7f, 0.7f, 0.7f);

            // GUID
            var guidObj = new GameObject("GUID");
            guidObj.transform.SetParent(entry.transform, false);
            var guidRect = guidObj.AddComponent<RectTransform>();
            guidRect.anchorMin = new Vector2(0, 0);
            guidRect.anchorMax = new Vector2(1, 0);
            guidRect.pivot = new Vector2(0, 0);
            guidRect.anchoredPosition = new Vector2(15, 15);
            guidRect.sizeDelta = new Vector2(-30, 15);

            var guidText = guidObj.AddComponent<TMPro.TextMeshProUGUI>();
            string displayGuid = mod.Guid.Length > 50 ? mod.Guid.Substring(0, 47) + "..." : mod.Guid;
            guidText.text = $"<size=80%>ID: {displayGuid}</size>";
            guidText.fontSize = 12;
            guidText.color = new Color(0.5f, 0.5f, 0.5f);

            return yPos - (150 + notesHeight);
        }

        private void CreateToggleButton(GameObject parent, ModInfo mod, bool runtimeEnabled, bool configEnabled)
        {
            // Eine ausstehende Änderung liegt vor, wenn der gewünschte (Config-)Zustand vom
            // tatsächlich laufenden Zustand abweicht — dann ist ein Neustart nötig.
            bool pendingChange = configEnabled != runtimeEnabled;
            var button = new GameObject("ToggleButton");
            button.transform.SetParent(parent.transform, false);

            var btnRect = button.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-165, -10);
            btnRect.sizeDelta = new Vector2(140, 30);

            // Button background
            var btnBg = button.AddComponent<UnityEngine.UI.Image>();
            // Ausstehende Änderung → orangefarbener Warn-Hintergrund, sonst grün/rot je nach Config.
            Color bgColor = pendingChange ? new Color(1f, 0.6f, 0f, 0.9f)
                : (configEnabled ? new Color(0.2f, 0.7f, 0.2f, 0.9f) : new Color(0.7f, 0.2f, 0.2f, 0.9f));
            btnBg.sprite = GetSolidSprite(bgColor);

            // Button text
            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(button.transform, false);
            var btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;

            var btnText = btnTextObj.AddComponent<TMPro.TextMeshProUGUI>();
            btnText.text = pendingChange ? "RESTART REQUIRED" : (configEnabled ? "DISABLE" : "ENABLE");
            btnText.fontSize = pendingChange ? 12 : 14;
            btnText.fontStyle = TMPro.FontStyles.Bold;
            btnText.alignment = TMPro.TextAlignmentOptions.Center;
            btnText.color = pendingChange ? new Color(1f, 1f, 0.5f) : Color.white;

            // Track if changed. originalValue = Laufzeit-Zustand: ein Zurück-Toggeln auf diesen
            // Wert hebt den Neustart-Hinweis wieder auf.
            bool wasChanged = pendingChange;
            bool originalValue = runtimeEnabled;

            // Button interaction
            var btnComponent = button.AddComponent<UnityEngine.UI.Button>();
            btnComponent.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                try
                {
                    if (mod.Enabled != null)
                    {
                        // Toggle the config value
                        bool newValue = !mod.Enabled.Value;
                        mod.Enabled.Value = newValue;

                        // Check if different from original
                        wasChanged = (newValue != originalValue);

                        // Update button appearance based on new state
                        if (wasChanged)
                        {
                            // Show restart required
                            btnText.text = "RESTART REQUIRED";
                            btnText.fontSize = 12;
                            btnText.color = new Color(1f, 1f, 0.5f);

                            btnBg.sprite = GetSolidSprite(new Color(1f, 0.6f, 0f, 0.9f));
                        }
                        else
                        {
                            // Back to original state
                            btnText.text = newValue ? "DISABLE" : "ENABLE";
                            btnText.fontSize = 14;
                            btnText.color = Color.white;

                            Color normalColor = newValue ? new Color(0.2f, 0.7f, 0.2f, 0.9f) : new Color(0.7f, 0.2f, 0.2f, 0.9f);
                            btnBg.sprite = GetSolidSprite(normalColor);
                        }

                        // Always save the config
                        var configFile = mod.Enabled.ConfigFile;
                        configFile?.Save();

                        UsefulTORStuffPlugin.Logger?.LogInfo($"Toggled {mod.Name} to {(newValue ? "ENABLED" : "DISABLED")} {(wasChanged ? "- restart required" : "- reverted")}");
                    }
                }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogError($"Failed to toggle mod: {ex}");
                    btnText.text = "ERROR";
                    btnText.color = Color.red;
                }
            }));
        }

        // Zusätzlicher Live-Toggle pro Mod (z. B. HostFix' Snitch-Fallback). Anders als der
        // Enable/Disable-Toggle wirkt dieser SOFORT: der Mod liest ConfigEntry.Value zur Laufzeit,
        // daher kein "Restart required". Position: unter dem Enable/Disable-Button (links neben GitHub).
        private void CreateExtraToggleButton(GameObject parent, ModInfo mod)
        {
            string label = string.IsNullOrEmpty(mod.ExtraToggleLabel) ? "Option" : mod.ExtraToggleLabel;

            var button = new GameObject("ExtraToggleButton");
            button.transform.SetParent(parent.transform, false);

            var btnRect = button.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-165, -45);
            btnRect.sizeDelta = new Vector2(140, 30);

            var btnBg = button.AddComponent<UnityEngine.UI.Image>();

            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(button.transform, false);
            var btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;

            var btnText = btnTextObj.AddComponent<TMPro.TextMeshProUGUI>();
            btnText.fontSize = 12;
            btnText.fontStyle = TMPro.FontStyles.Bold;
            btnText.alignment = TMPro.TextAlignmentOptions.Center;
            btnText.color = Color.white;

            // Lokale Funktion: Beschriftung + Hintergrund am aktuellen Zustand ausrichten.
            void Apply(bool on)
            {
                btnText.text = $"{label}: {(on ? "ON" : "OFF")}";
                btnBg.sprite = GetSolidSprite(on ? new Color(0.2f, 0.7f, 0.2f, 0.9f) : new Color(0.7f, 0.2f, 0.2f, 0.9f));
            }

            Apply(mod.ExtraToggle.Value);

            var btnComponent = button.AddComponent<UnityEngine.UI.Button>();
            btnComponent.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                try
                {
                    bool newValue = !mod.ExtraToggle.Value;
                    mod.ExtraToggle.Value = newValue;     // wirkt sofort (Mod liest live)
                    mod.ExtraToggle.ConfigFile?.Save();   // persistiert über Neustart hinweg
                    Apply(newValue);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"{mod.Name}: {label} set to {(newValue ? "ON" : "OFF")}.");
                }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogError($"Failed to toggle {label} for {mod.Name}: {ex}");
                    btnText.text = "ERROR";
                    btnText.color = Color.red;
                }
            }));
        }

        private void CreateUpdateButton(GameObject parent, ModInfo mod, ModEntryRefs refs)
        {
            var button = new GameObject("UpdateButton");
            button.transform.SetParent(parent.transform, false);

            var btnRect = button.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-15, -10);
            btnRect.sizeDelta = new Vector2(140, 30);

            // Button background
            var btnBg = button.AddComponent<UnityEngine.UI.Image>();
            btnBg.sprite = GetSolidSprite(new Color(0.2f, 0.6f, 1f, 0.9f));

            // Button text
            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(button.transform, false);
            var btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;

            var btnText = btnTextObj.AddComponent<TMPro.TextMeshProUGUI>();
            btnText.text = "UPDATE NOW";
            btnText.fontSize = 14;
            btnText.fontStyle = TMPro.FontStyles.Bold;
            btnText.alignment = TMPro.TextAlignmentOptions.Center;
            btnText.color = Color.white;

            // Referenzen für Polling/RefreshEntry merken.
            refs.UpdateButton = button;
            refs.UpdateButtonText = btnText;

            // Button interaction. Der Download läuft im Manager-Modus (kein Among-Us-Popup);
            // die Polling-Coroutine aktualisiert Label/Status anhand von GetUpdateState().
            var btnComponent = button.AddComponent<UnityEngine.UI.Button>();
            btnComponent.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                try
                {
                    UsefulTORStuffPlugin.Logger?.LogInfo($"Triggering update for {mod.Name}...");
                    mod.TriggerUpdate?.Invoke();
                    // Sofortiges Feedback; das Polling verfeinert die Anzeige danach.
                    btnText.text = "DOWNLOADING";
                    btnText.fontSize = 12;
                    btnText.color = new Color(1f, 1f, 0.5f);
                    btnComponent.interactable = false;
                }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogError($"Failed to trigger update: {ex}");
                    btnText.text = "ERROR";
                    btnText.color = Color.red;
                }
            }));
        }

        // Berechnet Statuszeile und Sichtbarkeit des Update-Buttons einer Mod-Zeile neu.
        // Aufgerufen beim Aufbau (CreateModEntry) und laufend von CoRefreshStates, damit auch
        // spät geladene Releases und Download-Fortschritte live erscheinen.
        private void RefreshEntry(ModEntryRefs r)
        {
            if (r == null || r.Mod == null || r.StatusText == null) return;

            bool runtime = r.RuntimeEnabled;
            bool config = r.Mod.Enabled?.Value ?? true;

            // Download-Zustand (0 idle, 1 downloading, 2 success, 3 error). Nur sinnvoll für laufende Mods.
            int state = 0;
            float progress = 0f;
            if (runtime)
            {
                try { state = r.Mod.GetUpdateState?.Invoke() ?? 0; } catch { }
                try { progress = r.Mod.GetUpdateProgress?.Invoke() ?? 0f; } catch { }
            }

            // Laufender/abgeschlossener Download hat Vorrang vor allem anderen.
            switch (state)
            {
                case 1: // downloading
                    int pct = Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f);
                    int stars = Mathf.CeilToInt(Mathf.Clamp01(progress) * 10);
                    string bar = new String((char)0x25A0, stars) + new String((char)0x25A1, 10 - stars);
                    r.StatusText.text = $"[>] Downloading... {pct}%  {bar}";
                    r.StatusText.color = new Color(0.3f, 0.7f, 1f);
                    SetUpdateButton(r, true, "DOWNLOADING", 12, false);
                    return;

                case 2: // success - restart required
                    r.StatusText.text = "[OK] Updated - Restart required";
                    r.StatusText.color = new Color(1f, 1f, 0.5f);
                    SetUpdateButton(r, false, null, 0, false);
                    return;

                case 3: // error
                    r.StatusText.text = "[X] Update failed - try again";
                    r.StatusText.color = new Color(1f, 0.4f, 0.4f);
                    SetUpdateButton(r, true, "RETRY", 14, true);
                    return;
            }

            // Kein Download: ausstehende Aktivierungs-Änderung anzeigen (läuft noch bis Neustart).
            if (config != runtime)
            {
                r.StatusText.text = config ? "[!] Restart required to enable" : "[!] Will be disabled after restart";
                r.StatusText.color = new Color(1f, 1f, 0.5f);
                SetUpdateButton(r, false, null, 0, false);
                return;
            }

            // Deaktiviert (kommt für angezeigte Mods normalerweise nicht vor).
            if (!runtime)
            {
                r.StatusText.text = "[-] Disabled";
                r.StatusText.color = Color.gray;
                SetUpdateButton(r, false, null, 0, false);
                return;
            }

            // Aktiv und idle: Update verfügbar?
            bool hasUpdate = false;
            try { hasUpdate = r.Mod.HasUpdate?.Invoke() ?? false; } catch { }

            if (hasUpdate)
            {
                r.StatusText.text = "[!] Update available";
                r.StatusText.color = new Color(1f, 0.8f, 0.2f);
                SetUpdateButton(r, true, "UPDATE NOW", 14, true);
            }
            else
            {
                r.StatusText.text = "[OK] Up to date";
                r.StatusText.color = new Color(0.3f, 1f, 0.3f);
                SetUpdateButton(r, false, null, 0, false);
            }
        }

        // Hilfsfunktion: Update-Button ein-/ausblenden und Beschriftung/Interaktivität setzen.
        private static void SetUpdateButton(ModEntryRefs r, bool active, string label, int fontSize, bool interactable)
        {
            if (r.UpdateButton == null) return;
            r.UpdateButton.SetActive(active);
            if (!active) return;
            if (label != null && r.UpdateButtonText != null) { r.UpdateButtonText.text = label; r.UpdateButtonText.fontSize = fontSize; }
            var b = r.UpdateButton.GetComponent<UnityEngine.UI.Button>();
            if (b != null) b.interactable = interactable;
        }

        // Pollt Update-/Download-Zustand aller Mod-Zeilen, solange das Panel offen ist.
        private IEnumerator CoRefreshStates()
        {
            while (IsUIOpen)
            {
                foreach (var r in _entryRefs)
                {
                    if (r?.Mod == null) continue;
                    RefreshEntry(r);
                }

                // F2: keep the "Update All" button's enabled state in sync as checks complete.
                if (!_updateAllRunning) RefreshUpdateAllButton();

                yield return new WaitForSeconds(0.25f);
            }
        }

        // True, wenn der Mod ein GitHub-Repository hinterlegt hat. Lokale Mods lassen die
        // Felder leer und erhalten daher keinen GitHub-Button. Future-proof: gilt automatisch
        // fuer jede kuenftige Mod, die kein Repository angibt.
        private static bool HasRepository(ModInfo mod) =>
            mod != null
            && !string.IsNullOrWhiteSpace(mod.RepositoryOwner)
            && !string.IsNullOrWhiteSpace(mod.RepositoryName);

        private void CreateGitHubButton(GameObject parent, ModInfo mod)
        {
            var button = new GameObject("GitHubButton");
            button.transform.SetParent(parent.transform, false);

            var btnRect = button.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-15, -45);
            btnRect.sizeDelta = new Vector2(140, 30);

            // Button background
            var btnBg = button.AddComponent<UnityEngine.UI.Image>();
            btnBg.sprite = GetSolidSprite(new Color(0.3f, 0.3f, 0.35f, 0.9f));

            // Button text
            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(button.transform, false);
            var btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;

            var btnText = btnTextObj.AddComponent<TMPro.TextMeshProUGUI>();
            btnText.text = "OPEN GITHUB";
            btnText.fontSize = 14;
            btnText.fontStyle = TMPro.FontStyles.Bold;
            btnText.alignment = TMPro.TextAlignmentOptions.Center;
            btnText.color = Color.white;

            // Button interaction
            var btnComponent = button.AddComponent<UnityEngine.UI.Button>();
            btnComponent.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                try
                {
                    string url = $"https://github.com/{mod.RepositoryOwner}/{mod.RepositoryName}";
                    Application.OpenURL(url);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"Opening GitHub: {url}");
                }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogError($"Failed to open GitHub: {ex}");
                }
            }));
        }

        private void CreateCloseButton(GameObject parent)
        {
            var button = new GameObject("CloseButton");
            button.transform.SetParent(parent.transform, false);

            var btnRect = button.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0);
            btnRect.anchorMax = new Vector2(0.5f, 0);
            btnRect.pivot = new Vector2(0.5f, 0);
            btnRect.anchoredPosition = new Vector2(0, 15);
            btnRect.sizeDelta = new Vector2(200, 50);

            // Button background
            var btnBg = button.AddComponent<UnityEngine.UI.Image>();
            btnBg.sprite = GetSolidSprite(new Color(0.8f, 0.2f, 0.2f, 0.9f));

            // Button text
            var btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(button.transform, false);
            var btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;

            var btnText = btnTextObj.AddComponent<TMPro.TextMeshProUGUI>();
            btnText.text = "[X] CLOSE";
            btnText.fontSize = 20;
            btnText.fontStyle = TMPro.FontStyles.Bold;
            btnText.alignment = TMPro.TextAlignmentOptions.Center;
            btnText.color = Color.white;

            // Button interaction
            var btnComponent = button.AddComponent<UnityEngine.UI.Button>();
            btnComponent.onClick.AddListener((UnityEngine.Events.UnityAction)Hide);
        }

        public void Hide()
        {
            try
            {
                // Set flag that UI is closed (stoppt auch CoRefreshStates)
                IsUIOpen = false;
                _entryRefs.Clear();

                // Re-enable background UI first
                EnableBackgroundUI();

                if (_popup != null)
                {
                    Destroy(_popup);
                    _popup = null;
                }

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager UI hidden.");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to hide Mod Manager UI: {ex}");
            }
        }
    }
}
