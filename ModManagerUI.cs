// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff
{
    // Popup-UI für den Mod-Manager mit vollständig interaktiven Elementen:
    // - Checkboxen zum Aktivieren/Deaktivieren von Mods
    // - Update-Buttons für verfügbare Updates
    // - Klickbare GitHub-Links
    // - Neustart-Hinweise
    public class ModManagerUI : MonoBehaviour
    {
        public static ModManagerUI Instance { get; private set; }

        public ModManagerUI(IntPtr ptr) : base(ptr) { }

        private GameObject _popup;
        private GameObject _contentContainer;
        private List<GameObject> _modEntryObjects = new List<GameObject>();
        private int _changesApplied = 0;
        private bool _restartWarningShown = false;

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
                    // Popup ist bereits offen
                    return;
                }

                CreatePopupWindow();
                RenderModList();

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager UI shown successfully.");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to show Mod Manager UI: {ex}");
            }
        }

        private void CreatePopupWindow()
        {
            // Erstelle ein großes Popup-Fenster (größer als TwitchPopup)
            _popup = new GameObject("ModManagerPopup");
            _popup.layer = 5; // UI Layer

            // Canvas für das Popup
            var canvas = _popup.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var canvasScaler = _popup.AddComponent<CanvasScaler>();
            canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasScaler.referenceResolution = new Vector2(1920, 1080);

            _popup.AddComponent<GraphicRaycaster>();

            // Halbtransparenter Hintergrund (Overlay)
            var bgPanel = new GameObject("Background");
            bgPanel.transform.SetParent(_popup.transform, false);
            var bgRect = bgPanel.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            var bgImage = bgPanel.AddComponent<Image>();
            bgImage.color = new Color(0, 0, 0, 0.8f);

            // Haupt-Panel (zentrales Fenster)
            var mainPanel = new GameObject("MainPanel");
            mainPanel.transform.SetParent(_popup.transform, false);
            var mainRect = mainPanel.AddComponent<RectTransform>();
            mainRect.anchorMin = new Vector2(0.5f, 0.5f);
            mainRect.anchorMax = new Vector2(0.5f, 0.5f);
            mainRect.sizeDelta = new Vector2(800, 600);
            mainRect.anchoredPosition = Vector2.zero;

            var mainImage = mainPanel.AddComponent<Image>();
            mainImage.color = new Color(0.1f, 0.1f, 0.15f, 1f);

            // Titel
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(mainPanel.transform, false);
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 1f);
            titleRect.anchorMax = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(760, 60);
            titleRect.anchoredPosition = new Vector2(0, -30);

            var titleText = titleObj.AddComponent<TMPro.TextMeshPro>();
            titleText.text = "<b>Mod Manager</b>";
            titleText.fontSize = 24;
            titleText.alignment = TMPro.TextAlignmentOptions.Center;
            titleText.color = Color.white;

            // Close-Button (X)
            var closeButtonTemplate = GameObject.Find("ExitGameButton");
            if (closeButtonTemplate != null)
            {
                var closeButton = Instantiate(closeButtonTemplate, mainPanel.transform);
                var closeRect = closeButton.GetComponent<RectTransform>();
                closeRect.anchorMin = new Vector2(1f, 1f);
                closeRect.anchorMax = new Vector2(1f, 1f);
                closeRect.sizeDelta = new Vector2(50, 50);
                closeRect.anchoredPosition = new Vector2(-25, -25);

                var closeText = closeButton.GetComponentInChildren<TMPro.TMP_Text>();
                if (closeText != null) closeText.text = "X";

                var closePassive = closeButton.GetComponent<PassiveButton>();
                if (closePassive != null)
                {
                    closePassive.OnClick = new Button.ButtonClickedEvent();
                    closePassive.OnClick.AddListener((Action)Hide);
                }
            }

            // Content-Container für Mod-Liste (scrollbar wäre ideal, aber vereinfacht ohne)
            _contentContainer = new GameObject("Content");
            _contentContainer.transform.SetParent(mainPanel.transform, false);
            var contentRect = _contentContainer.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.5f, 0f);
            contentRect.anchorMax = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(760, -100);
            contentRect.anchoredPosition = new Vector2(0, -80);
        }

        public void Hide()
        {
            try
            {
                // Neustart-Hinweis beim Schließen (nur wenn > 1 Änderung)
                if (_changesApplied > 1)
                {
                    ShowRestartReminder();
                }

                // Popup zerstören
                if (_popup != null)
                {
                    Destroy(_popup);
                    _popup = null;
                }

                _contentContainer = null;

                // Mod-Entry-Objekte clearen
                foreach (var entry in _modEntryObjects)
                {
                    if (entry != null) Destroy(entry);
                }
                _modEntryObjects.Clear();

                // Zustände zurücksetzen
                _changesApplied = 0;
                _restartWarningShown = false;

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager UI hidden.");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to hide Mod Manager UI: {ex}");
            }
        }

        private void RenderModList()
        {
            try
            {
                var mods = ModManagerRegistry.GetAllMods();
                if (mods == null || mods.Count == 0)
                {
                    CreateErrorLabel("Keine Mods in der Registry gefunden.");
                    return;
                }

                float yPosition = 0;
                foreach (var mod in mods)
                {
                    CreateModEntry(mod, yPosition);
                    yPosition -= 150; // Abstand zwischen Mod-Einträgen
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to render mod list: {ex}");
                CreateErrorLabel($"Fehler: {ex.Message}");
            }
        }

        private void CreateModEntry(ModInfo mod, float yPosition)
        {
            if (_contentContainer == null) return;

            // Haupt-Container für diesen Mod-Eintrag
            var entry = new GameObject($"ModEntry_{mod.Guid}");
            entry.transform.SetParent(_contentContainer.transform, false);
            var entryRect = entry.AddComponent<RectTransform>();
            entryRect.anchorMin = new Vector2(0.5f, 1f);
            entryRect.anchorMax = new Vector2(0.5f, 1f);
            entryRect.sizeDelta = new Vector2(740, 140);
            entryRect.anchoredPosition = new Vector2(0, yPosition);

            _modEntryObjects.Add(entry);

            // Hintergrund für Entry
            var entryBg = entry.AddComponent<Image>();
            entryBg.color = new Color(0.15f, 0.15f, 0.2f, 1f);

            bool isEnabled = mod.Enabled?.Value ?? true;
            bool hasUpdate = false;
            try { hasUpdate = mod.HasUpdate?.Invoke() ?? false; } catch { }

            // Mod-Name (klickbar für GitHub)
            var nameButton = CreateButton(entry.transform, new Vector2(-270, 50), new Vector2(200, 40),
                mod.Name, isEnabled ? mod.ButtonColor : Color.gray, () => {
                    string url = $"https://github.com/{mod.RepositoryOwner}/{mod.RepositoryName}";
                    Application.OpenURL(url);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"Opening GitHub: {url}");
                });

            // Version
            CreateLabel(entry.transform, new Vector2(-270, 10), new Vector2(200, 30),
                $"v{mod.Version}", Color.gray, 12);

            // Repository
            CreateLabel(entry.transform, new Vector2(-270, -20), new Vector2(400, 25),
                $"{mod.RepositoryOwner}/{mod.RepositoryName}", new Color(0.7f, 0.7f, 0.7f), 10);

            // Status-Label
            string statusText = isEnabled
                ? (hasUpdate ? "Update verfügbar" : "Aktuell")
                : "Deaktiviert";
            Color statusColor = isEnabled
                ? (hasUpdate ? Color.yellow : Color.green)
                : Color.gray;
            CreateLabel(entry.transform, new Vector2(100, 50), new Vector2(200, 30),
                statusText, statusColor, 14);

            // Enable/Disable-Toggle (Checkbox)
            var toggleButton = CreateButton(entry.transform, new Vector2(100, 10), new Vector2(180, 35),
                isEnabled ? "✓ Aktiviert" : "✗ Deaktiviert",
                isEnabled ? new Color(0, 0.8f, 0) : new Color(0.8f, 0, 0),
                () => ToggleModEnabled(mod));

            // Update-Button (nur wenn Update verfügbar und Mod aktiviert)
            if (hasUpdate && isEnabled)
            {
                CreateButton(entry.transform, new Vector2(100, -40), new Vector2(180, 35),
                    "Update starten", new Color(1f, 0.8f, 0), () => {
                        try
                        {
                            mod.TriggerUpdate?.Invoke();
                            UsefulTORStuffPlugin.Logger?.LogInfo($"Triggered update for {mod.Name}");
                        }
                        catch (Exception ex)
                        {
                            UsefulTORStuffPlugin.Logger?.LogError($"Failed to trigger update for {mod.Name}: {ex}");
                        }
                    });
            }
        }

        private void ToggleModEnabled(ModInfo mod)
        {
            if (mod.Enabled == null) return;

            try
            {
                bool newValue = !mod.Enabled.Value;
                mod.Enabled.Value = newValue;

                _changesApplied++;

                UsefulTORStuffPlugin.Logger?.LogInfo($"{mod.Name} {(newValue ? "enabled" : "disabled")} — restart required.");

                // Beim ersten Mal Neustart-Hinweis anzeigen
                if (_changesApplied == 1)
                {
                    ShowRestartWarning();
                }

                // UI neu rendern
                RefreshUI();
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to toggle {mod.Name}: {ex}");
            }
        }

        private void RefreshUI()
        {
            // Zerstöre alte Einträge
            foreach (var entry in _modEntryObjects)
            {
                if (entry != null) Destroy(entry);
            }
            _modEntryObjects.Clear();

            // Rendere neu
            RenderModList();
        }

        private GameObject CreateButton(Transform parent, Vector2 position, Vector2 size, string text, Color color, Action onClick)
        {
            var buttonTemplate = GameObject.Find("ExitGameButton");
            if (buttonTemplate == null) return null;

            var button = Instantiate(buttonTemplate, parent);
            var rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            var buttonText = button.GetComponentInChildren<TMPro.TMP_Text>();
            if (buttonText != null)
            {
                buttonText.text = text;
                buttonText.fontSize = 12;
                buttonText.color = Color.white;
            }

            var passive = button.GetComponent<PassiveButton>();
            if (passive != null)
            {
                passive.OnClick = new Button.ButtonClickedEvent();
                passive.OnClick.AddListener((Action)onClick);

                // Hover-Effekte
                passive.OnMouseOut.AddListener((Action)(() => {
                    if (buttonText != null) buttonText.color = Color.white;
                }));
                passive.OnMouseOver.AddListener((Action)(() => {
                    if (buttonText != null) buttonText.color = color;
                }));
            }

            return button;
        }

        private GameObject CreateLabel(Transform parent, Vector2 position, Vector2 size, string text, Color color, float fontSize)
        {
            var label = new GameObject("Label");
            label.transform.SetParent(parent, false);
            var rect = label.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            var tmp = label.AddComponent<TMPro.TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TMPro.TextAlignmentOptions.Left;

            return label;
        }

        private void CreateErrorLabel(string errorText)
        {
            if (_contentContainer == null) return;

            CreateLabel(_contentContainer.transform, Vector2.zero, new Vector2(700, 400),
                errorText, Color.red, 16);
        }

        private void ShowRestartWarning()
        {
            if (_restartWarningShown) return;
            this.StartCoroutine(CoShowRestartWarning());
            _restartWarningShown = true;
        }

        private void ShowRestartReminder()
        {
            this.StartCoroutine(CoShowRestartWarning());
        }

        private IEnumerator CoShowRestartWarning()
        {
            yield return new WaitForSeconds(0.3f);

            // TwitchPopup ist in Twitch namespace, aber wir greifen via Reflection zu (keine Compile-Zeit-Referenz)
            var twitchManagerType = Type.GetType("Twitch.TwitchManager, Assembly-CSharp");
            if (twitchManagerType == null) yield break;

            var instanceProp = twitchManagerType.GetProperty("Instance");
            var twitchManager = instanceProp?.GetValue(null);
            if (twitchManager == null) yield break;

            var twitchPopupProp = twitchManagerType.GetProperty("TwitchPopup");
            var template = twitchPopupProp?.GetValue(twitchManager) as GameObject;
            if (template == null) yield break;

            var warningPopup = Instantiate(template);
            warningPopup.SetActive(true);

            // TextAreaTMP setzen via Reflection
            var popupComponent = warningPopup.GetComponent<MonoBehaviour>();
            if (popupComponent != null)
            {
                var showMethod = popupComponent.GetType().GetMethod("Show");
                showMethod?.Invoke(popupComponent, null);

                var textAreaField = popupComponent.GetType().GetField("TextAreaTMP");
                var textArea = textAreaField?.GetValue(popupComponent) as TMPro.TextMeshPro;
                if (textArea != null)
                {
                    textArea.text = "<b><size=150%>Neustart erforderlich</size></b>\n\n" +
                        "Die Mod-Konfiguration wurde geändert.\n\n" +
                        "<color=#FFFF00>Bitte starte das Spiel neu,\ndamit die Änderungen wirksam werden.</color>";
                }
            }

            var closeButton = warningPopup.transform.GetChild(2).gameObject;
            if (closeButton != null)
            {
                var passiveButton = closeButton.GetComponent<PassiveButton>();
                if (passiveButton != null)
                {
                    passiveButton.OnClick.RemoveAllListeners();
                    passiveButton.OnClick.AddListener((Action)(() => {
                        Destroy(warningPopup);
                    }));
                }
            }
        }
    }
}
