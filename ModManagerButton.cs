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

        // Red badge on the button's top-right corner with the number of mods that have an update.
        // A screen-space canvas placed over the world-space button every frame (the button lives in
        // the menu's world, the badge is UGUI so it can use VanillaUI's circle and fonts).
        private GameObject _badgeRoot;
        private RectTransform _badgeRect;
        private TMPro.TextMeshProUGUI _badgeText;
        private int _badgeCount = -1;
        private float _nextCount;

        public void Awake()
        {
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        }

        public void Update()
        {
            if (_button == null || !_button.activeInHierarchy || ModManagerUI.IsUIOpen)
            {
                if (_badgeRoot != null && _badgeRoot.activeSelf) _badgeRoot.SetActive(false);
                return;
            }
            if (Time.realtimeSinceStartup >= _nextCount)
            {
                _nextCount = Time.realtimeSinceStartup + 1f;
                int n = 0;
                try
                {
                    foreach (var m in ModManagerRegistry.GetAllMods())
                        try { if (m.RuntimeEnabled && (m.HasUpdate?.Invoke() ?? false)) n++; } catch { }
                }
                catch { }
                if (n != _badgeCount) { _badgeCount = n; if (_badgeText != null) VanillaUI.SetText(_badgeText, n.ToString()); }
            }
            if (_badgeCount <= 0) { if (_badgeRoot != null && _badgeRoot.activeSelf) _badgeRoot.SetActive(false); return; }
            if (_badgeRoot == null) BuildBadge();
            if (_badgeRoot == null) return;
            if (!_badgeRoot.activeSelf) _badgeRoot.SetActive(true);
            try
            {
                var cam = Camera.main;
                var col = _button.GetComponent<Collider2D>();
                if (cam == null || col == null) return;
                Vector3 corner = cam.WorldToScreenPoint(col.bounds.max);
                _badgeRect.anchoredPosition = new Vector2(corner.x, corner.y) / VanillaUI.Scale(_badgeRoot);
            }
            catch { }
        }

        private void BuildBadge()
        {
            try
            {
                _badgeRoot = VanillaUI.Canvas("UTSModManagerBadge", 8900, false);
                var go = new GameObject("Badge");
                go.transform.SetParent(_badgeRoot.transform, false);
                _badgeRect = VanillaUI.Rect(go, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
                _badgeRect.sizeDelta = new Vector2(44, 44);
                var img = go.AddComponent<Image>();
                img.sprite = VanillaUI.Circle();
                img.color = VanillaUI.Red;
                img.raycastTarget = false;
                _badgeText = VanillaUI.Label(go, _badgeCount.ToString(), 26, Color.white, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, TMPro.TextAlignmentOptions.Center, TMPro.FontStyles.Bold);
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning($"Mod Manager badge failed: {ex.Message}");
                if (_badgeRoot != null) Destroy(_badgeRoot);
                _badgeRoot = null;
            }
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
