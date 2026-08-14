// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSRejoin - the way back into the lobby after a mod sync.
 *
 * A downloaded DLL only takes effect after a restart (BepInEx loads plugins once, at startup), so
 * the whole feature ends with the player having to quit the game. Without this, "sync the mods"
 * really means "leave, hunt for the lobby code you never wrote down, and hope the round has not
 * started". So before the restart we persist code + region + a timestamp, and the main menu offers
 * one button to walk straight back in.
 *
 * TOR already has a related helper (LobbyScreenPatch.LobbyJoinBind: remember the last GameId,
 * rejoin on LShift), but it keeps the id in a static field that dies with the process, which is
 * exactly the moment we care about. That file is original source and is left untouched; this is a
 * separate, persisted path in our own mod.
 *
 * The entry EXPIRES after 30 minutes. A stale code is worse than none: hours later that lobby is
 * either gone or has become somebody else's round.
 */

using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class UTSRejoin {

        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

        internal static ConfigEntry<string> SavedCode;
        internal static ConfigEntry<string> SavedRegion;
        internal static ConfigEntry<string> SavedStamp;

        public static void Bind(ConfigFile config) {
            SavedCode = config.Bind("ModSync", "RejoinCode", "",
                "Lobby code remembered before a mod sync restart. Cleared once used or expired.");
            SavedRegion = config.Bind("ModSync", "RejoinRegion", "",
                "Server region of the remembered lobby.");
            SavedStamp = config.Bind("ModSync", "RejoinStamp", "",
                "UTC timestamp of the remembered lobby (round-trip format). Entries expire after 30 minutes.");
        }

        // Called after a successful sync, while we are still in the lobby and still know where we are.
        public static void RememberCurrentLobby() {
            try {
                if (SavedCode == null || AmongUsClient.Instance == null) return;
                int gameId = AmongUsClient.Instance.GameId;
                if (gameId == 0) return;

                string code = InnerNet.GameCode.IntToGameName(gameId);
                if (string.IsNullOrEmpty(code)) return;

                string region = "";
                try { region = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion?.Name ?? ""; }
                catch { }

                SavedCode.Value = code;
                SavedRegion.Value = region;
                SavedStamp.Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[ModSync] remembered lobby {code} ({region}) for the post-restart rejoin.");
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] could not remember the lobby: {ex.Message}");
            }
        }

        public static void Clear() {
            if (SavedCode == null) return;
            SavedCode.Value = "";
            SavedRegion.Value = "";
            SavedStamp.Value = "";
        }

        // A remembered lobby that is still young enough to be worth offering.
        public static bool TryGetFresh(out string code, out string region) {
            code = null; region = null;
            if (SavedCode == null || string.IsNullOrWhiteSpace(SavedCode.Value)) return false;

            DateTime stamp;
            if (!DateTime.TryParse(SavedStamp.Value, CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out stamp)) return false;
            if (DateTime.UtcNow - stamp.ToUniversalTime() > MaxAge) {
                Clear();
                return false;
            }

            code = SavedCode.Value.Trim().ToUpperInvariant();
            region = SavedRegion.Value ?? "";
            return true;
        }

        // Switch back to the region the lobby lived on, if it is not the one already selected.
        // A region we cannot find is not fatal: the join is attempted anyway and simply fails to
        // find the code, which is the same thing the player would see typing it in by hand.
        private static void EnsureRegion(string regionName) {
            if (string.IsNullOrEmpty(regionName)) return;
            try {
                var sm = DestroyableSingleton<ServerManager>.Instance;
                if (sm == null) return;
                if (sm.CurrentRegion != null
                    && string.Equals(sm.CurrentRegion.Name, regionName, StringComparison.OrdinalIgnoreCase))
                    return;

                foreach (var region in sm.AvailableRegions) {
                    if (region == null) continue;
                    if (!string.Equals(region.Name, regionName, StringComparison.OrdinalIgnoreCase)) continue;
                    sm.SetRegion(region);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[ModSync] rejoin switched region to {regionName}.");
                    return;
                }
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[ModSync] rejoin region '{regionName}' not available - joining on the current region.");
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] rejoin region switch failed: {ex.Message}");
            }
        }

        // Runs the actual join on the given MonoBehaviour. Same call TOR's own LShift rejoin uses.
        public static void JoinRemembered(MonoBehaviour host) {
            string code, region;
            if (!TryGetFresh(out code, out region) || host == null) return;

            EnsureRegion(region);
            // One shot: whether or not the join succeeds, the code has been used. Leaving it around
            // would keep offering a button for a lobby that is most likely already running.
            Clear();

            try {
                int id = InnerNet.GameCode.GameNameToInt(code);
                if (id == 0) return;
                host.StartCoroutine(AmongUsClient.Instance.CoJoinOnlineGameFromCode(id));
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ModSync] rejoin failed: {ex}");
            }
        }
    }

    // Main-menu button, built the same way every updater builds its own: clone ExitGameButton and
    // move it via AspectPosition. Column x = 0.458 is shared with the update buttons
    // (0.124 / 0.21 / 0.30 / 0.38 / 0.39 / 0.48); 0.34 is the free slot between them.
    public class UTSRejoinButton : MonoBehaviour {
        public UTSRejoinButton(IntPtr ptr) : base(ptr) { }

        public void Start() {
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if (scene.name != "MainMenu") return;
            try { Build(); }
            catch (Exception ex) { UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] rejoin button failed: {ex.Message}"); }
        }

        [HideFromIl2Cpp]
        private void Build() {
            string code, region;
            if (!UTSRejoin.TryGetFresh(out code, out region)) return;

            var template = GameObject.Find("ExitGameButton");
            if (!template) return;

            var button = Instantiate(template, null);
            button.name = "UTSRejoinButton";
            button.GetComponent<AspectPosition>().anchorPoint = new Vector2(0.458f, 0.34f);

            PassiveButton passive = button.GetComponent<PassiveButton>();
            passive.OnClick = new Button.ButtonClickedEvent();
            passive.OnClick.AddListener((Action)(() => {
                button.SetActive(false);
                UTSRejoin.JoinRemembered(this);
            }));

            var text = button.transform.GetComponentInChildren<TMPro.TMP_Text>();
            string label = UTSLocalization.Tr("uts.modsync.rejoin_button", code);
            // The label is applied through a one-frame lerp because Among Us rewrites the cloned
            // button's text on its own first frame (same trick the updaters use).
            StartCoroutine(Effects.Lerp(0.1f, (Action<float>)(p => text.SetText(label))));
            passive.OnMouseOut.AddListener((Action)(() => text.color = Color.cyan));
            passive.OnMouseOver.AddListener((Action)(() => text.color = Color.white));
            text.color = Color.cyan;
        }
    }
}
