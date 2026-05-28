// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

        public ModManagerUI(IntPtr ptr) : base(ptr) { }

        private GameObject _popup;
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
                    UsefulTORStuffPlugin.Logger?.LogWarning("Mod Manager is already open.");
                    return;
                }

                // Hole TwitchPopup via Reflection
                var twitchManagerType = Type.GetType("Twitch.TwitchManager, Assembly-CSharp");
                if (twitchManagerType == null)
                {
                    UsefulTORStuffPlugin.Logger?.LogError("TwitchManager type not found.");
                    return;
                }

                var instanceProp = twitchManagerType.GetProperty("Instance");
                var twitchManager = instanceProp?.GetValue(null);
                if (twitchManager == null)
                {
                    UsefulTORStuffPlugin.Logger?.LogError("TwitchManager.Instance is null.");
                    return;
                }

                var twitchPopupProp = twitchManagerType.GetProperty("TwitchPopup");
                var template = twitchPopupProp?.GetValue(twitchManager) as GameObject;
                if (template == null)
                {
                    UsefulTORStuffPlugin.Logger?.LogError("TwitchPopup template is null.");
                    return;
                }

                _popup = Instantiate(template);
                _popup.SetActive(true);

                var popupComponent = _popup.GetComponent<MonoBehaviour>();
                if (popupComponent != null)
                {
                    // Show() aufrufen
                    var showMethod = popupComponent.GetType().GetMethod("Show");
                    showMethod?.Invoke(popupComponent, null);

                    // Text setzen
                    var textAreaField = popupComponent.GetType().GetField("TextAreaTMP");
                    var textArea = textAreaField?.GetValue(popupComponent) as TMPro.TextMeshPro;
                    if (textArea != null)
                    {
                        textArea.fontSize = 14f;
                        textArea.text = GenerateModListText();
                    }

                    // Close-Button anpassen
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

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager UI shown successfully.");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to show Mod Manager UI: {ex}");
            }
        }

        private string GenerateModListText()
        {
            try
            {
                var mods = ModManagerRegistry.GetAllMods();

                UsefulTORStuffPlugin.Logger?.LogInfo($"GetAllMods returned {mods?.Count ?? 0} mods.");

                if (mods == null || mods.Count == 0)
                {
                    return "<b><size=150%>Mod Manager</size></b>\n\n" +
                           "<color=#FF0000>Keine Mods gefunden!</color>\n\n" +
                           "Debug-Info:\n" +
                           "- Stelle sicher, dass alle 3 Mods geladen sind\n" +
                           "- Prüfe BepInEx/LogOutput.log für Fehler\n" +
                           "- Mod-Registrierung erfolgt in Plugin.Load()";
                }

                string text = "<b><size=150%>Mod Manager</size></b>\n\n";
                text += $"<b>{mods.Count} Mod(s) registriert:</b>\n\n";

                foreach (var mod in mods)
                {
                    bool isEnabled = mod.Enabled?.Value ?? true;
                    string statusIcon = isEnabled ? "<color=#00FF00>✓</color>" : "<color=#FF0000>✗</color>";

                    Color nameColor = isEnabled ? mod.ButtonColor : Color.gray;
                    string nameColorHex = ColorUtility.ToHtmlStringRGB(nameColor);

                    string updateStatus = "";
                    try
                    {
                        bool hasUpdate = mod.HasUpdate?.Invoke() ?? false;
                        updateStatus = hasUpdate
                            ? " <color=#FFFF00>[Update verfügbar]</color>"
                            : " <color=#00FF00>[Aktuell]</color>";
                    }
                    catch (Exception ex)
                    {
                        UsefulTORStuffPlugin.Logger?.LogWarning($"Failed to check update status for {mod.Name}: {ex.Message}");
                        updateStatus = " <color=#AAAAAA>[Status unbekannt]</color>";
                    }

                    if (!isEnabled)
                    {
                        updateStatus = " <color=#888888>[Deaktiviert]</color>";
                    }

                    text += $"{statusIcon} <color=#{nameColorHex}><b>{mod.Name}</b></color> v{mod.Version}{updateStatus}\n";
                    text += $"   Repo: {mod.RepositoryOwner}/{mod.RepositoryName}\n";
                    text += $"   GUID: {mod.Guid}\n\n";
                }

                text += "\n<size=80%><color=#AAAAAA>Hinweis: Diese Version ist read-only.\n";
                text += "Mod-Aktivierung/Deaktivierung über BepInEx Config:\n";
                text += "BepInEx/config/<mod-guid>.cfg → [General] Enabled\n\n";
                text += "Mod-Manager aktivieren/deaktivieren:\n";
                text += "com.tormod.usefultorstuff.cfg → [ModManager] Enabled</color></size>";

                return text;
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
                if (_popup != null)
                {
                    Destroy(_popup);
                    _popup = null;
                }

                _changesApplied = 0;
                _restartWarningShown = false;

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager UI hidden.");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to hide Mod Manager UI: {ex}");
            }
        }
    }
}
