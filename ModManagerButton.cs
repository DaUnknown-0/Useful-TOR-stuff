// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UsefulTORStuff
{
    // Button im Hauptmenü zum Öffnen des Mod-Managers.
    // Immer sichtbar (unabhängig vom Mod-Manager-Toggle).
    public class ModManagerButton : MonoBehaviour
    {
        public ModManagerButton(IntPtr ptr) : base(ptr) { }

        private GameObject _button;

        public void Awake()
        {
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "MainMenu") return;
            CreateButton();
        }

        private void CreateButton()
        {
            try
            {
                // Zerstöre alten Button falls vorhanden (bei Scene-Reload)
                if (_button != null)
                {
                    Destroy(_button);
                    _button = null;
                }

                var template = GameObject.Find("ExitGameButton");
                if (template == null)
                {
                    UsefulTORStuffPlugin.Logger?.LogWarning("ExitGameButton template not found — Mod Manager button not created.");
                    return;
                }

                // Button instantiieren und positionieren
                _button = Instantiate(template, null);
                var buttonPosition = new Vector2(
                    UsefulTORStuffPlugin.ModManagerButtonX.Value,
                    UsefulTORStuffPlugin.ModManagerButtonY.Value
                );
                _button.GetComponent<AspectPosition>().anchorPoint = buttonPosition;

                // Text setzen
                var text = _button.transform.GetComponentInChildren<TMPro.TMP_Text>();
                string buttonText = UTSLocalization.Tr("uts.modmanagerbutton.label");
                this.StartCoroutine(Effects.Lerp(0.1f, (Action<float>)(p => {
                    if (text != null) text.SetText(buttonText);
                })));

                // OnClick-Handler: Öffnet die Mod-Manager-UI
                PassiveButton passiveButton = _button.GetComponent<PassiveButton>();
                passiveButton.OnClick = new Button.ButtonClickedEvent();
                passiveButton.OnClick.AddListener((Action)(() => {
                    if (ModManagerUI.Instance != null)
                    {
                        ModManagerUI.Instance.Show();
                    }
                    else
                    {
                        UsefulTORStuffPlugin.Logger?.LogWarning("ModManagerUI.Instance is null — cannot show Mod Manager.");
                    }
                }));

                // Hover-Farben (neutral weiß/grau)
                passiveButton.OnMouseOut.AddListener((Action)(() => {
                    if (text != null) text.color = Color.white;
                }));
                passiveButton.OnMouseOver.AddListener((Action)(() => {
                    if (text != null) text.color = Color.gray;
                }));

                if (text != null) text.color = Color.white;

                UsefulTORStuffPlugin.Logger?.LogInfo("Mod Manager button created successfully.");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to create Mod Manager button: {ex}");
            }
        }
    }
}
