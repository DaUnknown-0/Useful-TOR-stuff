// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * MusicOptionsButton - a "Music" button in TOR's "Mod Options..." popup (gear menu) that opens the music panel
 * (DraftMusicUI) anywhere, so nobody has to join a lobby to pick the menu music. Same approach as Unknown's
 * Collection's own entry there: TOR's popup and button prefab are reached by reflection
 * (TheOtherRoles.Patches.ClientOptionsPatch.popUp / buttonPrefab), the button is added when the popup opens.
 * TOR's popup hides while the panel is open and comes back when it closes.
 */

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace UsefulTORStuff {

    [HarmonyPatch]
    public static class MusicOptionsButton {
        private const string ButtonName = "UTSMusicNavButton";
        private static FieldInfo popUpField, prefabField;
        private static readonly Color32 Idle = new Color32(110, 40, 90, byte.MaxValue), Hover = new Color32(150, 60, 125, byte.MaxValue);

        private static void Warn(string s) => UsefulTORStuffPlugin.Logger?.LogWarning("[MusicOptions] " + s);

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
        public static void OptionsMenuStartPostfix(OptionsMenuBehaviour __instance) {
            try {
                if (__instance.CensorChatButton == null || !Resolve()) return;
                var parent = __instance.CensorChatButton.transform.parent;
                if (parent == null) return;
                for (int i = 0; i < parent.childCount; i++) {
                    var tb = parent.GetChild(i).GetComponent<ToggleButtonBehaviour>();
                    if (tb?.Text?.text?.Contains("Mod Options") != true) continue;
                    var pb = tb.GetComponent<PassiveButton>();
                    pb?.OnClick.AddListener((Action)(() => {
                        try { if (popUpField.GetValue(null) is GameObject pop) AddButton(pop); }
                        catch (Exception e) { Warn("mod options click: " + e.Message); }
                    }));
                    break;
                }
            } catch (Exception e) { Warn("OptionsMenu.Start: " + e.Message); }
        }

        private static bool Resolve() {
            if (popUpField != null && prefabField != null) return true;
            var type = Type.GetType("TheOtherRoles.Patches.ClientOptionsPatch, TheOtherRoles");
            if (type == null) { Warn("TOR's ClientOptionsPatch not found - no Music button"); return false; }
            popUpField = AccessTools.Field(type, "popUp");
            prefabField = AccessTools.Field(type, "buttonPrefab");
            return popUpField != null && prefabField != null;
        }

        /// The popup TOR shows right now (it can be rebuilt between scenes), for autotests too.
        internal static GameObject TorPopUp() {
            try { return Resolve() ? popUpField.GetValue(null) as GameObject : null; } catch { return null; }
        }

        internal static void AddButton(GameObject pop) {
            foreach (var t in pop.GetComponentsInChildren<ToggleButtonBehaviour>(true))
                if (t.name == ButtonName) return;
            var prefab = prefabField?.GetValue(null) as ToggleButtonBehaviour;
            if (prefab == null)
                foreach (var t in pop.GetComponentsInChildren<ToggleButtonBehaviour>(true)) { prefab = t; break; }
            if (prefab == null) return;

            var nav = Object.Instantiate(prefab, pop.transform);
            nav.name = ButtonName;
            nav.gameObject.SetActive(true);
            nav.Text.text = UTSLocalization.Tr("uts.music.options_button");
            nav.Text.fontSizeMin = nav.Text.fontSizeMax = 1.8f;
            nav.Text.transform.localScale = Vector3.one;
            nav.onState = false;
            nav.Background.color = Idle;
            // TOR fills two columns from y 1.3 downwards (nine options + Unknown's Collection's entry end at
            // -1.9, the frame at about -2.8): a flat, centred bar in the gap below (checked in a screenshot)
            nav.transform.localPosition = new Vector3(0f, -2.48f, -0.5f);
            var size = new Vector2(2.4f, 0.4f);
            var collider = nav.GetComponent<BoxCollider2D>();
            if (collider != null) collider.size = size;
            foreach (var spr in nav.GetComponentsInChildren<SpriteRenderer>()) spr.size = size;

            var pb = nav.GetComponent<PassiveButton>();
            pb.OnClick = new ButtonClickedEvent();
            pb.OnMouseOut = new UnityEvent();
            pb.OnMouseOver = new UnityEvent();
            pb.OnClick.AddListener((Action)Open);
            pb.OnMouseOver.AddListener((Action)(() => nav.Background.color = Hover));
            pb.OnMouseOut.AddListener((Action)(() => nav.Background.color = Idle));
        }

        internal static void Open() {
            try {
                var ui = DraftMusicUI.Instance;
                if (ui == null) return;
                var pop = TorPopUp();
                ui.OpenFromOptions(() => { try { if (pop != null) pop.SetActive(true); } catch { } });
                if (pop != null) pop.SetActive(false);   // only once the panel is up
            } catch (Exception e) { Warn("open: " + e.Message); }
        }
    }
}
