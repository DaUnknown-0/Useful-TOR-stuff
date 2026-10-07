// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UIGallery - autotest only: photographs every UTS screen so a redesign can be checked without a
 * second player. Config "Diagnostics / UI Gallery Test" (off by default, never ships on).
 *
 * Sequence: main menu -> Mod Manager, one shot per tab -> freeplay on The Skeld -> lobby menu,
 * newcomer shield, early-death shield (host panel and viewer), session statistics, mod sync panel,
 * password gate, kill feed, then UC's help panel and Role Control's F7 panel through reflection.
 * Shots land in <game>/UTSShots/gallery_NN_<name>.png; "[UIGallery] done" in the log ends the run.
 *
 * Freeplay has no GameStartManager, so while the gallery runs LobbyScreen.Exists and the menu
 * entries' visibility are forced to "lobby, host": the panels never query a real lobby object.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public static class UIGallery {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> ColorblindOnly;
        public static ConfigEntry<int> GalleryMap;
        public static bool Active => Enabled != null && Enabled.Value;
        private static bool CbOnly => ColorblindOnly != null && ColorblindOnly.Value;

        private static bool menuFired, gameFired;
        private static int shot;

        public static void Bind(ConfigFile config) {
            Enabled = config.Bind("Diagnostics", "UI Gallery Test", false,
                "Autotest only: opens every UTS panel in turn (main menu and freeplay) and saves screenshots to UTSShots.");
            ColorblindOnly = config.Bind("Diagnostics", "UI Gallery Colorblind Only", false,
                "Autotest only: with the gallery on, skips the panels and checks the colourblind names (cams, map, admin).");
            GalleryMap = config.Bind("Diagnostics", "UI Gallery Map", 0, "Autotest only: the freeplay map (0 Skeld, 2 Polus, 4 Airship, 5 Fungle).");
        }

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[UIGallery] " + s);

        // Read back synchronously at the end of the frame: ScreenCapture.CaptureScreenshot turned out
        // to write each file only when the NEXT capture was requested, so every shot showed the
        // following step (2026-10-07).
        private static IEnumerator Shot(string tag) {
            yield return new WaitForEndOfFrame();
            try {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UTSShots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"gallery_{shot++:D2}_{tag}.png");
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(tex));
                UnityEngine.Object.Destroy(tex);
                Log($"shot {tag} -> {file}");
            } catch (Exception e) { Log($"shot {tag} failed: {e.Message}"); }
        }

        // ---- main menu: the Mod Manager ----
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class MenuPatch {
            private static void Postfix(MainMenuManager __instance) {
                if (menuFired || !Active) return;
                menuFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, MenuRun());
            }
        }

        private static IEnumerator MenuRun() {
            float t0 = Time.time;
            while (true) {
                bool done = false;
                try { done = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (done || Time.time - t0 > 45f) break;
                yield return null;
            }
            yield return new WaitForSeconds(2f);
            var mm = CbOnly ? null : ModManagerUI.Instance;
            if (mm != null) {
                mm.Show();
                yield return new WaitForSeconds(1.5f);
                yield return Shot("modmanager_installed");
                mm.DiagShowTab(1);
                yield return new WaitForSeconds(1f);
                yield return Shot("modmanager_allmods");
                // a sample pack and a pending restart, so the modpack row and the restart button show
                var sample = UTSModpacks.FromCurrent("Gallery pack");
                UTSModpacks.Add(sample);
                UTSRestart.Mark();
                mm.DiagShowTab(2);
                yield return new WaitForSeconds(1f);
                yield return Shot("modmanager_modpacks");
                mm.DiagRename(sample);
                yield return new WaitForSeconds(1f);
                yield return Shot("modmanager_rename");
                mm.DiagRename(null);
                UTSModpacks.Delete(sample);
                mm.Hide();
                yield return new WaitForSeconds(0.5f);
            } else Log("no ModManagerUI");

            // freeplay on The Skeld (same route as SubmergedSelfTest)
            try {
                var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                if (popover == null) { Log("no FreeplayPopover"); yield break; }
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap((MapNames)(GalleryMap != null ? GalleryMap.Value : 0));
                Log("freeplay started");
            } catch (Exception e) { Log("freeplay start failed: " + e.Message); }
        }

        // ---- freeplay: the lobby panels ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class GamePatch {
            private static void Postfix(HudManager __instance) {
                if (gameFired || !Active) return;
                var ac = AmongUsClient.Instance;
                if (ac == null || ac.NetworkMode != NetworkModes.FreePlay || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
                gameFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, CbOnly ? ColorblindRun() : GameRun());
            }
        }

        private static IEnumerator GameRun() {
            yield return new WaitForSeconds(6f);
            Log("game run begins");

            // the corner tile and its menu
            var menu = LobbyMenuUI.Instance;
            yield return new WaitForSeconds(1f);
            if (menu != null) { menu.Toggle(); yield return new WaitForSeconds(1f); yield return Shot("lobby_menu"); menu.Close(); }

            var nc = NewcomerShieldUI.Instance;
            if (nc != null) { nc.Toggle(); yield return new WaitForSeconds(1f); yield return Shot("newcomer_shield"); nc.Close(); }

            var ed = EarlyDeathShieldUI.Instance;
            if (ed != null) {
                ed.Toggle(); yield return new WaitForSeconds(1f); yield return Shot("earlydeath_host"); ed.Close();
                EarlyDeathShield.DiagSampleStats();
                ed.DiagOpenViewer(); yield return new WaitForSeconds(1f); yield return Shot("earlydeath_viewer"); ed.Close();
            }

            var st = SessionStatsUI.Instance;
            if (st != null) {
                SessionStats.DiagSample();
                st.Toggle(); yield return new WaitForSeconds(1.5f); yield return Shot("session_stats");
                st.DiagShowTitles(); yield return new WaitForSeconds(1f); yield return Shot("session_titles"); st.Close();
            }

            var ms = UTSModSyncUI.Instance;
            if (ms != null) { ms.Open(); yield return new WaitForSeconds(1f); yield return Shot("mod_sync"); ms.Close(); }

            var gate = LobbyPasswordGate.Instance;
            if (gate != null) { gate.ShowPanel(); yield return new WaitForSeconds(1f); yield return Shot("password_gate"); gate.HidePanel(); }

            GhostKillFeed.DiagSample();
            yield return new WaitForSeconds(1f);
            yield return Shot("kill_feed");

            // other mods' overlays, when present
            if (Invoke("UnknownsCollection.UCHelpMenu, UnknownsCollection", "DiagToggle")) {
                yield return new WaitForSeconds(1.5f); yield return Shot("uc_help");
                Invoke("UnknownsCollection.UCHelpMenu, UnknownsCollection", "DiagToggle");
            }
            if (Invoke("ForceImpostorMod.RoleControlUI, ForceImpostorMod", "DiagToggle")) {
                yield return new WaitForSeconds(1.5f); yield return Shot("role_control");
                Invoke("ForceImpostorMod.RoleControlUI, ForceImpostorMod", "DiagToggle");
            }
            // UC's colour grant: player list, hex entry, the question the target sees
            try { AppDomain.CurrentDomain.SetData("UTS.UIGallery.Active", true); } catch { }
            string[] grant = { "colorgrant_list", "colorgrant_hex", "colorgrant_prompt" };
            for (int s = 0; s < grant.Length; s++) {
                if (!Invoke("UnknownsCollection.UCColorGrantUI, UnknownsCollection", "DiagShow", s)) break;
                yield return new WaitForSeconds(1.2f); yield return Shot(grant[s]);
            }
            Invoke("UnknownsCollection.UCColorGrantUI, UnknownsCollection", "DiagShow", -1);
            yield return new WaitForSeconds(0.5f);
            Log("done");
        }

        // ---- the colourblind names: cams, map dots, the Hacker's admin ----
        private static IEnumerator ColorblindRun() {
            yield return new WaitForSeconds(6f);
            var lp = PlayerControl.LocalPlayer;
            var others = new List<PlayerControl>();
            foreach (var p in PlayerControl.AllPlayerControls) if (p != null && p != lp) others.Add(p);
            Log($"colorblind run: mode {ColorblindLabels.On()}, dummies {others.Count}");
            var ship = ShipStatus.Instance;

            // cams: the dummies in front of the cameras, then the console's minigame
            Minigame mg = null;
            try {
                var cams = ship.AllCameras;
                for (int i = 0; i < others.Count && cams.Length > 0; i++) {
                    var c = cams[i % cams.Length].transform.position;
                    others[i].NetTransform.SnapTo((Vector2)c + new Vector2((i / cams.Length) * 0.7f - 0.35f, -1.2f));
                }
                Minigame prefab = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<SystemConsole>()) {
                    if (c.MinigamePrefab == null) continue;
                    string n = c.MinigamePrefab.name;
                    if (n.Contains("Surv") || n.Contains("Cam") || n.Contains("Security")) { Log("camera console " + n); if (prefab == null) prefab = c.MinigamePrefab; }
                }
                Log($"cams: {cams.Length} cameras, prefab {(prefab != null ? prefab.name : "none")}");
                if (prefab != null) {
                    mg = UnityEngine.Object.Instantiate(prefab, Camera.main.transform, false);
                    mg.transform.localPosition = new Vector3(0f, 0f, -50f);
                    mg.Begin(null);
                }
            } catch (Exception e) { Log("cams open failed: " + e); }
            yield return new WaitForSeconds(1.5f);
            try {
                foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>())
                    Log($"camera '{cam.name}' mask 0x{cam.cullingMask:X} target {(cam.targetTexture != null ? cam.targetTexture.name : "-")} ortho {cam.orthographicSize:F2}");
                for (int i = 0; i < others.Count && i < 3; i++) {
                    var co = others[i].cosmetics;
                    var t = co.colorBlindText;
                    var mr = t.GetComponent<MeshRenderer>();
                    Log($"{others[i].name}: cb layer {t.gameObject.layer} active {t.gameObject.activeInHierarchy} enabled {t.enabled} text '{t.text}' " +
                        $"sort {mr?.sortingLayerName}/{mr?.sortingOrder} name layer {co.nameText.gameObject.layer} body layer {others[i].gameObject.layer} show {co.showColorBlindText}");
                }
            } catch (Exception e) { Log("cams diag failed: " + e); }
            yield return Shot("cb_cams");
            try { if (mg != null) mg.Close(); } catch { }
            yield return new WaitForSeconds(1f);

            // map as a ghost: TOR draws every player as a dot (MapBehaviourPatch)
            bool wasDead = lp.Data.IsDead;
            try {
                Log($"local role {lp.Data.Role?.Role}, impostor {lp.Data.Role?.IsImpostor}");
                lp.Data.IsDead = true;
                HudManager.Instance.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Normal });
            } catch (Exception e) { Log("map open failed: " + e.Message); }
            yield return new WaitForSeconds(1.5f);
            try { Log($"map dots {ColorblindLabels.HerePoints()?.Count}"); } catch { }
            yield return Shot("cb_map_ghost");
            try {
                if (MapBehaviour.Instance != null) MapBehaviour.Instance.Close();
                lp.Data.IsDead = wasDead;
            } catch { }
            yield return new WaitForSeconds(1f);

            // admin with the Hacker's colours, then "only light/dark"
            var oldHacker = TheOtherRoles.Hacker.hacker;
            bool oldOnly = TheOtherRoles.Hacker.onlyColorType;
            try {
                TheOtherRoles.Hacker.hacker = lp;
                TheOtherRoles.Hacker.hackerTimer = 60f;
                TheOtherRoles.Hacker.onlyColorType = false;
                HudManager.Instance.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.CountOverlay });
            } catch (Exception e) { Log("admin open failed: " + e.Message); }
            yield return new WaitForSeconds(1.5f);
            yield return Shot("cb_admin");
            TheOtherRoles.Hacker.onlyColorType = true;
            yield return new WaitForSeconds(1.5f);
            yield return Shot("cb_admin_lightdark");
            try {
                if (MapBehaviour.Instance != null) MapBehaviour.Instance.Close();
                TheOtherRoles.Hacker.hacker = oldHacker;
                TheOtherRoles.Hacker.hackerTimer = 0f;
                TheOtherRoles.Hacker.onlyColorType = oldOnly;
            } catch { }
            yield return new WaitForSeconds(0.5f);
            Log("done");
        }

        private static bool Invoke(string typeName, string method, params object[] args) {
            try {
                var t = Type.GetType(typeName);
                var m = t?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (m == null) { Log($"{typeName}.{method} not found"); return false; }
                m.Invoke(null, args.Length == 0 ? null : args);
                return true;
            } catch (Exception e) { Log($"{typeName}.{method} failed: {e.Message}"); return false; }
        }
    }
}
