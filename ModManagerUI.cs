// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, new Color(0, 0, 0, 0.85f));
            texture.Apply();

            var material = new Material(Shader.Find("UI/Default"));
            material.mainTexture = texture;

            canvasRenderer.SetMaterial(material, texture);
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
            var bgTexture = new Texture2D(1, 1);
            bgTexture.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.15f, 0.98f));
            bgTexture.Apply();
            panelBg.sprite = Sprite.Create(bgTexture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));

            // Title
            CreateTitle(panel);

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
            var bgTex = new Texture2D(1, 1);
            bgTex.SetPixel(0, 0, runtimeEnabled ? new Color(0.15f, 0.2f, 0.15f, 0.8f) : new Color(0.2f, 0.15f, 0.15f, 0.6f));
            bgTex.Apply();
            bg.sprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));

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

            return yPos - 150;
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
            var btnTex = new Texture2D(1, 1);
            // Ausstehende Änderung → orangefarbener Warn-Hintergrund, sonst grün/rot je nach Config.
            Color bgColor = pendingChange ? new Color(1f, 0.6f, 0f, 0.9f)
                : (configEnabled ? new Color(0.2f, 0.7f, 0.2f, 0.9f) : new Color(0.7f, 0.2f, 0.2f, 0.9f));
            btnTex.SetPixel(0, 0, bgColor);
            btnTex.Apply();
            btnBg.sprite = Sprite.Create(btnTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));

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

                            var warningTex = new Texture2D(1, 1);
                            warningTex.SetPixel(0, 0, new Color(1f, 0.6f, 0f, 0.9f));
                            warningTex.Apply();
                            btnBg.sprite = Sprite.Create(warningTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
                        }
                        else
                        {
                            // Back to original state
                            btnText.text = newValue ? "DISABLE" : "ENABLE";
                            btnText.fontSize = 14;
                            btnText.color = Color.white;

                            var normalTex = new Texture2D(1, 1);
                            Color normalColor = newValue ? new Color(0.2f, 0.7f, 0.2f, 0.9f) : new Color(0.7f, 0.2f, 0.2f, 0.9f);
                            normalTex.SetPixel(0, 0, normalColor);
                            normalTex.Apply();
                            btnBg.sprite = Sprite.Create(normalTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
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
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, on ? new Color(0.2f, 0.7f, 0.2f, 0.9f) : new Color(0.7f, 0.2f, 0.2f, 0.9f));
                tex.Apply();
                btnBg.sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
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
            var btnTex = new Texture2D(1, 1);
            btnTex.SetPixel(0, 0, new Color(0.2f, 0.6f, 1f, 0.9f));
            btnTex.Apply();
            btnBg.sprite = Sprite.Create(btnTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));

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
            var btnTex = new Texture2D(1, 1);
            btnTex.SetPixel(0, 0, new Color(0.3f, 0.3f, 0.35f, 0.9f));
            btnTex.Apply();
            btnBg.sprite = Sprite.Create(btnTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));

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
            var btnTex = new Texture2D(1, 1);
            btnTex.SetPixel(0, 0, new Color(0.8f, 0.2f, 0.2f, 0.9f));
            btnTex.Apply();
            btnBg.sprite = Sprite.Create(btnTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));

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

        private string GenerateModListText()
        {
            try
            {
                var mods = ModManagerRegistry.GetAllMods();

                UsefulTORStuffPlugin.Logger?.LogInfo($"GetAllMods returned {mods?.Count ?? 0} mods.");

                if (mods == null || mods.Count == 0)
                {
                    return "<b><size=150%>MOD MANAGER</size></b>\n\n" +
                           "<color=#FF0000>Keine Mods gefunden!</color>\n\n" +
                           "Debug-Info:\n" +
                           "- Stelle sicher, dass alle Mods geladen sind\n" +
                           "- Prüfe BepInEx/LogOutput.log für Fehler\n" +
                           "- Mod-Registrierung erfolgt in Plugin.Load() via RegisterMod()";
                }

                var sb = new StringBuilder();

                // Header
                sb.AppendLine("<b><size=150%>MOD MANAGER</size></b>");
                sb.AppendLine($"<size=120%>{mods.Count} Mod(s) geladen</size>\n");

                // Mod entries
                foreach (var mod in mods)
                {
                    bool isEnabled = mod.Enabled?.Value ?? true;

                    // Status icon
                    string statusIcon = isEnabled ? "●" : "○";
                    Color statusColor = isEnabled ? Color.green : Color.gray;

                    // Update status
                    string updateIcon = "?";
                    string updateText = "Status unbekannt";
                    Color updateColor = Color.gray;

                    try
                    {
                        if (isEnabled && (mod.HasUpdate?.Invoke() ?? false))
                        {
                            updateIcon = "⟳";
                            updateText = "Update verfügbar";
                            updateColor = new Color(1f, 0.8f, 0f); // Gold/Gelb
                        }
                        else if (isEnabled)
                        {
                            updateIcon = "✓";
                            updateText = "Aktuell";
                            updateColor = Color.green;
                        }
                        else
                        {
                            updateIcon = "—";
                            updateText = "Deaktiviert";
                        }
                    }
                    catch (Exception ex)
                    {
                        UsefulTORStuffPlugin.Logger?.LogWarning($"Mod Manager: Failed to check update for {mod.Name}: {ex.Message}");
                    }

                    // Mod name line (mit Status-Icon)
                    sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(statusColor)}>{statusIcon}</color> ");

                    Color nameColor = isEnabled ? mod.ButtonColor : Color.gray;
                    sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(nameColor)}><b>{mod.Name}</b></color> ");
                    sb.AppendLine($"<size=80%>v{mod.Version}</size>");

                    // Update status line (eingerückt)
                    sb.Append($"  <color=#{ColorUtility.ToHtmlStringRGB(updateColor)}>{updateIcon} {updateText}</color>");
                    sb.AppendLine();

                    // Repository (kleinerer Text)
                    sb.AppendLine($"  <size=80%>{mod.RepositoryOwner}/{mod.RepositoryName}</size>");

                    // GUID (noch kleiner, grau, truncated bei Bedarf)
                    string displayGuid = mod.Guid.Length > 40
                        ? mod.Guid.Substring(0, 37) + "..."
                        : mod.Guid;
                    sb.AppendLine($"  <size=70%><color=#888888>{displayGuid}</color></size>");

                    // Separator
                    sb.AppendLine("<color=#444444>────────────────────────</color>\n");
                }

                // Footer (prominent aber nicht aufdringlich)
                sb.AppendLine("<size=85%><color=#CCCCCC><b>Konfiguration:</b></color></size>");
                sb.AppendLine("<size=75%><color=#AAAAAA>");
                sb.AppendLine("Mods aktivieren/deaktivieren:");
                sb.AppendLine("  BepInEx/config/<mod-guid>.cfg → [General] Enabled");
                sb.AppendLine("");
                sb.AppendLine("Mod Manager umschalten:");
                sb.AppendLine("  com.tormod.usefultorstuff.cfg → [ModManager] Enabled");
                sb.AppendLine("</color></size>");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to generate mod list text: {ex}");
                return $"<b>Fehler beim Laden der Mod-Liste:</b>\n\n{ex.Message}\n\n{ex.StackTrace}";
            }
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
