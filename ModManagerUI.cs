// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils;
using Twitch;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff
{
    // Popup-UI für den Mod-Manager.
    // Zeigt Liste aller Mods mit Update-Status, Enable/Disable-Optionen, GitHub-Links, etc.
    public class ModManagerUI : MonoBehaviour
    {
        public static ModManagerUI Instance { get; private set; }

        public ModManagerUI(IntPtr ptr) : base(ptr) { }

        private GameObject _popup;
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
                    // Popup ist bereits offen, nichts tun
                    return;
                }

                // Verwende TwitchPopup als Basis (einfacher als AnnouncementPopUp)
                var template = TwitchManager.Instance?.TwitchPopup;
                if (template == null)
                {
                    UsefulTORStuffPlugin.Logger?.LogError("TwitchPopup template not found — cannot show Mod Manager.");
                    return;
                }

                _popup = Instantiate(template.gameObject);
                _popup.SetActive(true);

                var twitchPopup = _popup.GetComponent<TwitchPopup>();
                if (twitchPopup != null)
                {
                    twitchPopup.Show();

                    // Text-Bereich anpassen
                    if (twitchPopup.TextAreaTMP != null)
                    {
                        twitchPopup.TextAreaTMP.fontSize = 14f;
                        twitchPopup.TextAreaTMP.enableAutoSizing = false;
                    }

                    // Close-Button (Child 2 ist normalerweise der Button)
                    var closeButton = _popup.transform.GetChild(2).gameObject;
                    if (closeButton != null)
                    {
                        var passiveButton = closeButton.GetComponent<PassiveButton>();
                        if (passiveButton != null)
                        {
                            passiveButton.OnClick.RemoveAllListeners();
                            passiveButton.OnClick.AddListener((Action)Hide);
                        }
                    }
                }

                // Mod-Liste rendern
                RenderModList();

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager UI shown successfully.");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to show Mod Manager UI: {ex}");
            }
        }

        public void Hide()
        {
            try
            {
                // Neustart-Hinweis beim Schließen (nur wenn > 1 Änderung oder == 0 UND mindestens eine Änderung)
                // Korrektur basierend auf User-Anforderung: "beim Verlassen erneut hinweisen, es sei denn, es ist nur eine oder keine Änderung"
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
                    SetPopupText("Keine Mods in der Registry gefunden.\n\nStelle sicher, dass ChanceMod, HostFixPlugin\nund UsefulTORStuff korrekt geladen sind.");
                    return;
                }

                // Text zusammenbauen mit Mod-Informationen
                string text = "<b><size=150%>Mod Manager</size></b>\n\n";
                text += $"<b>{mods.Count} Mod(s) installiert:</b>\n\n";

                foreach (var mod in mods)
                {
                    // Enable/Disable-Status
                    bool isEnabled = mod.Enabled?.Value ?? true;
                    string statusIcon = isEnabled ? "<color=#00FF00>✓</color>" : "<color=#FF0000>✗</color>";

                    // Name + Version (farbcodiert)
                    Color nameColor = isEnabled ? mod.ButtonColor : Color.gray;
                    string nameColorHex = ColorUtility.ToHtmlStringRGB(nameColor);

                    // Update-Status
                    string updateStatus = "";
                    try
                    {
                        bool hasUpdate = mod.HasUpdate?.Invoke() ?? false;
                        updateStatus = hasUpdate
                            ? " <color=#FFFF00>[Update verfügbar]</color>"
                            : " <color=#00FF00>[Aktuell]</color>";
                    }
                    catch
                    {
                        updateStatus = " <color=#AAAAAA>[Status unbekannt]</color>";
                    }

                    if (!isEnabled)
                    {
                        updateStatus = " <color=#888888>[Deaktiviert]</color>";
                    }

                    text += $"{statusIcon} <color=#{nameColorHex}><b>{mod.Name}</b></color> v{mod.Version}{updateStatus}\n";
                    text += $"   Repository: {mod.RepositoryOwner}/{mod.RepositoryName}\n\n";
                }

                text += "\n<size=80%><color=#AAAAAA>Hinweis: Änderungen erfordern einen Neustart.\n";
                text += "Klicke auf einen Mod-Namen im Code, um GitHub zu öffnen.\n";
                text += "Toggle-Funktionen und Update-Buttons sind in dieser\n";
                text += "Version noch nicht implementiert (nur Anzeige).</color></size>";

                SetPopupText(text);
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to render mod list: {ex}");
                SetPopupText($"Fehler beim Laden der Mod-Liste:\n{ex.Message}");
            }
        }

        private void SetPopupText(string text)
        {
            if (_popup == null) return;

            var twitchPopup = _popup.GetComponent<TwitchPopup>();
            if (twitchPopup != null && twitchPopup.TextAreaTMP != null)
            {
                twitchPopup.TextAreaTMP.text = text;
            }
        }

        // Zeigt Neustart-Hinweis (nur beim ersten Mal pro Session)
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

            var template = TwitchManager.Instance?.TwitchPopup;
            if (template == null) yield break;

            var warningPopup = Instantiate(template.gameObject);
            warningPopup.SetActive(true);

            var twitchPopup = warningPopup.GetComponent<TwitchPopup>();
            if (twitchPopup != null)
            {
                twitchPopup.Show();
                if (twitchPopup.TextAreaTMP != null)
                {
                    twitchPopup.TextAreaTMP.text = "<b><size=150%>Neustart erforderlich</size></b>\n\n" +
                        "Die Mod-Konfiguration wurde geändert.\n\n" +
                        "<color=#FFFF00>Bitte starte das Spiel neu,\ndamit die Änderungen wirksam werden.</color>";
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

        // TODO: Vollständige Implementierung mit interaktiven Elementen (Checkboxen, Update-Buttons, GitHub-Links)
        // Diese Version zeigt eine Read-Only-Liste. Für interaktive Elemente müsste man
        // custom GameObjects/UI-Elemente erstellen statt nur Text anzuzeigen.
        // Das würde den Rahmen dieser ersten Implementation sprengen, ist aber als
        // Erweiterung möglich.
    }
}
