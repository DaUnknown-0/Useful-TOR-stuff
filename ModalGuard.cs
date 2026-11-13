// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ModalGuard - while one of our menus is open, the game's own buttons under it do nothing (User 2026-11-13: "most GUI
 * things do not block the buttons in the background: with another GUI open, the question mark, the cog or the chat
 * still open").
 *
 * Why it happened: our panels are screen-space uGUI canvases. Their dark backdrop swallows uGUI clicks only; the
 * game's buttons are world-space PassiveButtons with colliders and the game hands clicks to them on its own, so the
 * chat, the settings cog or the map opened through any of our menus. Now for every mod:
 *
 *   ModalRegistry   one list of open menu roots, shared by all mods through the AppDomain (no mod depends on
 *                   another): every blocking VanillaUI canvas is in it, the other mods add their menus (UC's and
 *                   Role Control's world-space panels too). A root counts while it is alive, active and its canvas
 *                   enabled (a hidden canvas must never block invisibly).
 *   the guard       while one is open: PassiveButton clicks (cog, chat, map, use, report, EDIT...), the vanilla hotkeys
 *                   (KeyboardJoystick.HandleHud) and TOR's ability buttons (CustomButton.onClickEvent) are swallowed,
 *                   and the local player stands still (mouse control walked him towards a click on a menu).
 * Our own menus are uGUI or have their own hit boxes, so none of them needs a PassiveButton itself.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    /// <summary>The menu roots of all mods (AppDomain "TORMods.ModalRoots"); same code in every mod that uses it.</summary>
    public static class ModalRegistry {
        private const string Key = "TORMods.ModalRoots";
        private static List<GameObject> list;

        private static List<GameObject> L {
            get {
                if (list != null) return list;
                var d = AppDomain.CurrentDomain.GetData(Key) as List<GameObject>;
                if (d == null) { d = new List<GameObject>(); AppDomain.CurrentDomain.SetData(Key, d); }
                return list = d;
            }
        }

        /// <summary>A menu root that blocks the game while it is active (kept until it is destroyed).</summary>
        public static void Add(GameObject root) {
            if (root == null) return;
            var l = L;
            for (int i = 0; i < l.Count; i++) if (l[i] != null && l[i].Pointer == root.Pointer) return;
            l.Add(root);
            frame = -1;
        }

        // Panels that block only the game's buttons under them and leave walking and hotkeys alone (UC's role guide:
        // "the player can keep walking with the guide open"). AppDomain "TORMods.ClickBlockRoots".
        private const string ClickKey = "TORMods.ClickBlockRoots";
        private static List<GameObject> clickList;

        private static List<GameObject> C {
            get {
                if (clickList != null) return clickList;
                var d = AppDomain.CurrentDomain.GetData(ClickKey) as List<GameObject>;
                if (d == null) { d = new List<GameObject>(); AppDomain.CurrentDomain.SetData(ClickKey, d); }
                return clickList = d;
            }
        }

        /// <summary>A panel that blocks clicks on the game's buttons only (kept until it is destroyed).</summary>
        public static void AddClickOnly(GameObject root) {
            if (root == null) return;
            var l = C;
            for (int i = 0; i < l.Count; i++) if (l[i] != null && l[i].Pointer == root.Pointer) return;
            l.Add(root);
        }

        /// <summary>Is a menu or a click-only panel open (clicks on the game's buttons are swallowed)?</summary>
        public static bool ClicksBlocked() => AnyOpen() || Scan(C, null);

        private static int frame = -1;
        private static bool cached;

        /// <summary>Is a menu open (other than `except`, a mod's own panel that asks)?</summary>
        public static bool AnyOpen(GameObject except = null) {
            if (except == null && Time.frameCount == frame) return cached;
            bool open = Scan(L, except);
            if (except == null) { frame = Time.frameCount; cached = open; }
            return open;
        }

        /// <summary>Is a menu or click-only panel open other than `except` (for a mod's own panel: "someone else's is on top")?</summary>
        public static bool OtherOpen(GameObject except) => Scan(L, except) || Scan(C, except);

        private static bool Scan(List<GameObject> l, GameObject except) {
            bool open = false;
            for (int i = l.Count - 1; i >= 0; i--) {
                var go = l[i];
                if (go == null) { l.RemoveAt(i); continue; }
                if (except != null && go.Pointer == except.Pointer) continue;
                try {
                    if (!go.activeInHierarchy) continue;
                    var cv = go.GetComponent<Canvas>();
                    if (cv != null && !cv.enabled) continue;
                    open = true;
                } catch { }
            }
            return open;
        }
    }

    internal static class ModalGuard {
        // the game's buttons (Il2Cpp methods: a tiering-safe detour)
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickDown))]
        private static class ClickDown { public static bool Prefix() => !ModalRegistry.ClicksBlocked(); }
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickUp))]
        private static class ClickUp { public static bool Prefix() => !ModalRegistry.ClicksBlocked(); }
        [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveRepeatDown))]
        private static class Repeat { public static bool Prefix() => !ModalRegistry.ClicksBlocked(); }

        // vanilla hotkeys (use, report, kill, map, sabotage)
        [HarmonyPatch]
        private static class Hotkeys {
            static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(KeyboardJoystick), "HandleHud");
            static bool Prepare() => TargetMethod() != null;
            public static bool Prefix() => !ModalRegistry.AnyOpen();
        }

        // TOR's ability buttons (hotkey and click)
        [HarmonyPatch]
        private static class TorButtons {
            static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method(typeof(TheOtherRoles.Objects.CustomButton), "onClickEvent");
            static bool Prepare() => TargetMethod() != null;
            public static bool Prefix() => !ModalRegistry.AnyOpen();
        }

        private static bool froze;

        /// <summary>Per frame (ModalGuardTicker): the local player stands still while a menu is open.</summary>
        internal static void Tick() {
            try {
                var lp = PlayerControl.LocalPlayer;
                if (lp == null) { froze = false; return; }
                if (ModalRegistry.AnyOpen()) {
                    if (lp.moveable) { lp.moveable = false; froze = true; }
                } else if (froze) {
                    // only what this guard froze; a meeting or exile that started meanwhile handles moveable itself
                    froze = false;
                    if (MeetingHud.Instance == null && ExileController.Instance == null) lp.moveable = true;
                }
            } catch { }
        }
    }

    public class ModalGuardTicker : MonoBehaviour {
        public ModalGuardTicker(IntPtr ptr) : base(ptr) { }
        public void Update() => ModalGuard.Tick();
    }
}
