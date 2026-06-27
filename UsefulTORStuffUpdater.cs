// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using AmongUs.Data;
using Assets.InnerNet;
using Twitch;

namespace UsefulTORStuff {
    // Self-updater that checks the GitHub releases of this repo and offers an in-game update
    // button on the main menu. Mirrors TOR's own ModUpdater flow but uses its own GithubRelease
    // DTOs so this plugin needs no compile-time reference to TheOtherRoles.
    public class UsefulTORStuffUpdater : MonoBehaviour {
        public const string RepositoryOwner = "DaUnknown-0";
        public const string RepositoryName = "Useful-TOR-stuff";
        public const string PluginAssetName = "UsefulTORStuff.dll";

        public static UsefulTORStuffUpdater Instance { get; private set; }

        public UsefulTORStuffUpdater(IntPtr ptr) : base(ptr) { }

        private bool _busy;
        private bool _showPopUp = true;
        // Einmal-Flag: die gesammelte Update-Ankündigung (Manager-Modus) nur einmal pro Sitzung zeigen.
        private bool _showConsolidatedAnnouncement = true;
        public List<GithubRelease> Releases;

        // Download-Zustand für den Mod Manager. 0 = idle, 1 = downloading,
        // 2 = success (restart required), 3 = error. Lebt in der Instanz, damit das
        // Mod-Manager-UI ihn über Schließen/Öffnen hinweg abfragen kann.
        private int _updateState;
        private float _updateProgress;

        // True sobald der GitHub-Release-Check abgeschlossen ist (Erfolg oder Fehler). Vom
        // Mod Manager abgefragt, um die gesammelte Update-Ankündigung erst nach allen Checks zu zeigen.
        private bool _checkCompleted;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            foreach (var file in Directory.GetFiles(Paths.PluginPath, PluginAssetName + ".old")) {
                File.Delete(file);
            }
        }

        private void Start() {
            if (_busy) return;
            this.StartCoroutine(CoCheckForUpdate());
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        }

        [HideFromIl2Cpp]
        public void StartDownloadRelease(GithubRelease release, bool managerMode = false) {
            if (_busy) return;
            this.StartCoroutine(CoDownloadRelease(release, managerMode));
        }

        // Vom Mod Manager beim Öffnen ausgelöster erneuter GitHub-Release-Check (gedrosselt
        // auf 1×/Minute durch ModManagerRegistry.MaybeCheckForUpdates).
        [HideFromIl2Cpp]
        public void TriggerCheckFromManager() {
            if (_busy) return;          // läuft bereits ein Check/Download — nicht doppelt starten
            _checkCompleted = false;    // erlaubt UI/Ankündigung, den laufenden Re-Check zu erkennen
            this.StartCoroutine(CoCheckForUpdate());
        }

        // Reflection-/direkt-aufrufbare Getter für das Mod-Manager-UI.
        [HideFromIl2Cpp]
        public int GetUpdateState() => _updateState;

        [HideFromIl2Cpp]
        public float GetUpdateProgress() => _updateProgress;

        [HideFromIl2Cpp]
        public bool GetCheckCompleted() => _checkCompleted;

        [HideFromIl2Cpp]
        private IEnumerator CoCheckForUpdate() {
            _busy = true;
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases");
            // GitHub weist UA-lose Clients ab (P2.8) — eindeutigen User-Agent setzen.
            www.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            var operation = www.SendWebRequest();

            while (!operation.isDone) {
                yield return new WaitForEndOfFrame();
            }

            if (www.isNetworkError || www.isHttpError) {
                www.downloadHandler.Dispose();
                www.Dispose();
                _checkCompleted = true;
                _busy = false;
                yield break;
            }

            // GitHub liefert bei Rate-Limit (403) oder Fehlern ein JSON-OBJEKT statt eines
            // Arrays; Deserialize/Sort dürfen die Coroutine nicht killen, sonst bliebe _busy
            // für die ganze Session true und alle weiteren Checks/Downloads wären blockiert
            // (P0.2). try/catch ist hier möglich, weil dieser Block kein yield enthält.
            try {
                Releases = JsonSerializer.Deserialize<List<GithubRelease>>(www.downloadHandler.text);
                if (Releases != null) Releases.Sort(SortReleases);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"TOR - Forgotten Fixes update check: failed to parse GitHub releases ({ex.Message}). Treating as 'no update'.");
                // Releases unverändert lassen (ggf. null) — überall als "kein Update" behandelt.
            } finally {
                www.downloadHandler.Dispose();
                www.Dispose();
                _checkCompleted = true;
                _busy = false;
            }
        }

        [HideFromIl2Cpp]
        private IEnumerator CoDownloadRelease(GithubRelease release, bool managerMode) {
            _busy = true;
            _updateState = 1;
            _updateProgress = 0f;

            // Im Manager-Modus wird kein Among-Us-TwitchPopup erzeugt; der Mod Manager zeigt
            // Fortschritt/Status selbst über GetUpdateState()/GetUpdateProgress() an.
            GenericPopup popup = null;
            GameObject button = null;
            if (!managerMode) {
                popup = Instantiate(TwitchManager.Instance.TwitchPopup);
                popup.TextAreaTMP.fontSize *= 0.7f;
                popup.TextAreaTMP.enableAutoSizing = false;

                popup.Show();

                button = popup.transform.GetChild(2).gameObject;
                button.SetActive(false);
                popup.TextAreaTMP.text = "Updating TOR - Forgotten Fixes\nPlease wait...";
            }

            var asset = release.Assets.Find(FilterPluginAsset);
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(asset.DownloadUrl);
            www.downloadHandler = new DownloadHandlerBuffer();
            var operation = www.SendWebRequest();

            while (!operation.isDone) {
                _updateProgress = www.downloadProgress;
                if (!managerMode) {
                    int stars = Mathf.CeilToInt(www.downloadProgress * 10);
                    string progress = $"Updating TOR - Forgotten Fixes\nPlease wait...\nDownloading...\n{new String((char)0x25A0, stars) + new String((char)0x25A1, 10 - stars)}";
                    popup.TextAreaTMP.text = progress;
                }
                yield return new WaitForEndOfFrame();
            }

            if (www.isNetworkError || www.isHttpError) {
                _updateState = 3;
                if (!managerMode) {
                    popup.TextAreaTMP.text = "Update wasn't successful\nTry again later,\nor update manually.";
                    button.SetActive(true);
                }
                _busy = false;
                yield break;
            }
            if (!managerMode) {
                popup.TextAreaTMP.text = "Updating TOR - Forgotten Fixes\nPlease wait...\n\nDownload complete\ncopying file...";
            }

            var filePath = Path.Combine(Paths.PluginPath, asset.Name);

            if (File.Exists(filePath + ".old")) File.Delete(filePath + ".old");
            if (File.Exists(filePath)) File.Move(filePath, filePath + ".old");

            var persistTask = File.WriteAllBytesAsync(filePath, www.downloadHandler.data);
            var hasError = false;
            while (!persistTask.IsCompleted) {
                if (persistTask.Exception != null) {
                    hasError = true;
                    break;
                }

                yield return new WaitForEndOfFrame();
            }

            www.downloadHandler.Dispose();
            www.Dispose();

            if (!hasError) {
                _updateState = 2;
                if (!managerMode) {
                    popup.TextAreaTMP.text = "TOR - Forgotten Fixes\nupdated successfully\nPlease restart the game.";
                }
            } else {
                _updateState = 3;
            }
            if (!managerMode) button.SetActive(true);
            _busy = false;
        }

        [HideFromIl2Cpp]
        private static bool FilterPluginAsset(GithubAsset asset) {
            return asset.Name == PluginAssetName;
        }

        [HideFromIl2Cpp]
        private static int SortReleases(GithubRelease a, GithubRelease b) {
            if (a.IsNewer(b.Version)) return -1;
            if (b.IsNewer(a.Version)) return 1;
            return 0;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if (scene.name != "MainMenu") return;

            // Wenn Mod-Manager aktiviert ist, keine eigenen Update-Buttons anzeigen. Stattdessen
            // zeigt UsefulTORStuff (als Manager-Besitzer) einmalig eine gesammelte Ankündigung mit
            // allen Mods, die ein Update brauchen. Unabhängig von den eigenen Releases.
            if (ModManagerRegistry.IsModManagerEnabled()) {
                if (_showConsolidatedAnnouncement) {
                    _showConsolidatedAnnouncement = false;
                    this.StartCoroutine(CoShowConsolidatedUpdateAnnouncement());
                }
                return;
            }

            if (_busy || Releases == null) return;

            var latestRelease = Releases.FirstOrDefault();
            if (latestRelease == null || !latestRelease.IsNewer(global::UsefulTORStuff.UsefulTORStuffPlugin.Version) || !latestRelease.Assets.Any(FilterPluginAsset))
                return;

            var template = GameObject.Find("ExitGameButton");
            if (!template) return;

            var button = Instantiate(template, null);
            // Stacked above TOR's updater (0.124), the Chance updater (0.21) and the Host Fix
            // updater (0.30) to avoid overlap.
            button.GetComponent<AspectPosition>().anchorPoint = new Vector2(0.458f, 0.39f);

            PassiveButton passiveButton = button.GetComponent<PassiveButton>();
            passiveButton.OnClick = new Button.ButtonClickedEvent();
            passiveButton.OnClick.AddListener((Action)(() => {
                StartDownloadRelease(latestRelease);
                button.SetActive(false);
            }));

            var text = button.transform.GetComponentInChildren<TMPro.TMP_Text>();
            string t = "Update TOR - Forgotten Fixes";
            StartCoroutine(Effects.Lerp(0.1f, (Action<float>)(p => text.SetText(t))));
            passiveButton.OnMouseOut.AddListener((Action)(() => text.color = Color.green));
            passiveButton.OnMouseOver.AddListener((Action)(() => text.color = Color.white));
            text.color = Color.green;

            if (_showPopUp) {
                var announcement = $"<size=150%>A new TOR - FORGOTTEN FIXES update to {latestRelease.Tag} is available</size>\n{latestRelease.Description}";
                var mgr = FindObjectOfType<MainMenuManager>(true);
                if (mgr != null)
                    mgr.StartCoroutine(CoShowAnnouncement(announcement, shortTitle: "TOR - Forgotten Fixes Update", date: latestRelease.PublishedAt));
            }
            _showPopUp = false;
        }

        // Manager-Modus: zeigt einmalig eine gesammelte Ankündigung mit allen Mods, die ein Update
        // brauchen — statt der einzelnen per-Mod-Ankündigungen (die im Manager-Modus unterdrückt sind).
        [HideFromIl2Cpp]
        private IEnumerator CoShowConsolidatedUpdateAnnouncement() {
            var mods = ModManagerRegistry.GetAllMods();

            // Warte bis alle Mods ihren GitHub-Release-Check abgeschlossen haben (oder Timeout),
            // damit kein Update verpasst wird, dessen Check noch lief.
            for (float t = 20f; t > 0f; t -= 0.25f) {
                bool allDone = true;
                foreach (var m in mods) {
                    // Deaktivierte Mods laufen nicht und haben keinen Update-Check — überspringen,
                    // sonst würde die Schleife bis zum Timeout warten.
                    if (!m.RuntimeEnabled) continue;
                    bool done = false;
                    try { done = m.GetCheckCompleted?.Invoke() ?? true; } catch { }
                    if (!done) { allDone = false; break; }
                }
                if (allDone) break;
                yield return new WaitForSeconds(0.25f);
            }

            // Sammle alle laufenden Mods mit verfügbarem Update.
            var names = new List<string>();
            foreach (var m in mods) {
                bool has = false;
                try { has = m.RuntimeEnabled && (m.HasUpdate?.Invoke() ?? false); } catch { }
                if (has) names.Add($"• <b>{m.Name}</b> <size=80%>(v{m.Version})</size>");
            }

            if (names.Count == 0) yield break;

            string announcement =
                "<size=130%>Mod Updates Available</size>\n\n" +
                $"The following mod{(names.Count == 1 ? "" : "s")} can be updated:\n\n" +
                string.Join("\n", names) +
                "\n\nOpen the <b>Mod Manager</b> from the main menu to update.";

            yield return this.StartCoroutine(CoShowAnnouncement(
                announcement, shortTitle: "Mod Updates", title: "Mod Updates Available"));
        }

        [HideFromIl2Cpp]
        public IEnumerator CoShowAnnouncement(string announcement, bool show = true, string shortTitle = "TOR - Forgotten Fixes Update", string title = "", string date = "") {
            // Stagger behind other mods so the other update popups appear first.
            yield return new WaitForSeconds(2f);
            // Show last of all mods. The other updaters wait for a clear popup field and then
            // settle 0.2 s before instantiating, so when a shared prior popup (e.g. Chance) closes
            // they all break on the same frame and race. To guarantee Useful TOR Stuff comes AFTER
            // Host Fix, settle longer (0.6 s) and re-check: if a popup (Host Fix's) appeared during
            // the settle, loop and wait it out again.
            for (float guard = 30f; guard > 0f; guard -= 0.6f) {
                // Wait until no announcement popup is currently visible (up to 30 s).
                for (float t = 30f; t > 0f; t -= 0.25f) {
                    if (UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>() == null) break;
                    yield return new WaitForSeconds(0.25f);
                }
                // Settle longer than the other updaters (0.2 s) so they win a simultaneous clear.
                yield return new WaitForSeconds(0.6f);
                // Nothing slipped in during the settle — safe to show now.
                if (UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>() == null) break;
            }

            var mgr = FindObjectOfType<MainMenuManager>(true);
            var popUpTemplate = UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>(true);
            // Ohne Template würde Instantiate(null) sofort werfen; ohne Manager würde
            // mgr.StartCoroutine(...) weiter unten ein NullRef auslösen (P0.1).
            if (popUpTemplate == null || mgr == null) {
                yield break;
            }
            var popUp = UnityEngine.Object.Instantiate(popUpTemplate);

            popUp.gameObject.SetActive(true);

            Announcement optimizedAnnouncement = new() {
                Id = "usefulTORStuffAnnouncement",
                Language = 0,
                Number = 6972,
                Title = title == "" ? "TOR - Forgotten Fixes Announcement" : title,
                ShortTitle = shortTitle,
                SubTitle = "",
                PinState = false,
                Date = date == "" ? DateTime.Now.Date.ToString() : date,
                Text = announcement,
            };
            mgr.StartCoroutine(Effects.Lerp(0.1f, new Action<float>((p) => {
                if (p == 1) {
                    var backup = DataManager.Player.Announcements.allAnnouncements;
                    DataManager.Player.Announcements.allAnnouncements = new();
                    popUp.Init(false);
                    DataManager.Player.Announcements.SetAnnouncements(new Announcement[] { optimizedAnnouncement });
                    popUp.CreateAnnouncementList();
                    popUp.UpdateAnnouncementText(optimizedAnnouncement.Number);
                    popUp.visibleAnnouncements[0].PassiveButton.OnClick.RemoveAllListeners();
                    DataManager.Player.Announcements.allAnnouncements = backup;
                }
            })));
        }

        // Callback-Methoden für ModManagerRegistry: Prüft ob ein Update verfügbar ist.
        [HideFromIl2Cpp]
        public bool HasUpdate() {
            if (Releases == null || Releases.Count == 0) return false;
            var latestRelease = Releases.FirstOrDefault();
            return latestRelease != null
                && latestRelease.IsNewer(UsefulTORStuffPlugin.Version)
                && latestRelease.Assets.Any(FilterPluginAsset);
        }

        // F2: Roh-Release-Notes (GitHub-`body`) der neuesten Version. Aus dem bereits geladenen
        // JSON — kein zusätzlicher API-Call. Strip/Truncate übernimmt das Mod-Manager-UI.
        [HideFromIl2Cpp]
        public string GetReleaseNotes() {
            if (Releases == null || Releases.Count == 0) return "";
            return Releases.FirstOrDefault()?.Description ?? "";
        }

        // Callback-Methode für ModManagerRegistry: Startet den Update-Download.
        [HideFromIl2Cpp]
        public void TriggerUpdateFromManager() {
            if (Releases == null || Releases.Count == 0) return;
            var latestRelease = Releases.FirstOrDefault();
            // Asset-Check wie in HasUpdate(): ohne passende DLL liefe CoDownloadRelease in
            // einen NullRef bei asset.DownloadUrl (release.Assets.Find liefert dann null).
            if (latestRelease != null && latestRelease.IsNewer(UsefulTORStuffPlugin.Version)
                && latestRelease.Assets.Any(FilterPluginAsset)) {
                StartDownloadRelease(latestRelease, managerMode: true);
            }
        }

        // ---- Test/Stable channel switching (Mod Manager "show test versions" toggle) ----
        // Channel is derived from the TAG FORMAT: stable = vX.Y.Z (Version.Revision <= 0), test =
        // vX.Y.Z.W (Revision > 0). Independent of the GitHub prerelease flag, so it also works for
        // releases published before test builds were marked as prereleases.
        [HideFromIl2Cpp]
        public GithubRelease LatestInChannel(bool stable) {
            if (Releases == null) return null;
            foreach (var r in Releases) {
                if (r == null || r.Draft) continue;
                int rev;
                try { rev = r.Version.Revision; } catch { continue; }
                bool isTest = rev > 0;
                if (stable == isTest) continue;            // wrong channel
                if (r.Assets != null && r.Assets.Any(FilterPluginAsset)) return r;
            }
            return null;
        }

        // True if a release exists in the requested channel (stable=true / test=false) with our asset.
        [HideFromIl2Cpp]
        public bool HasChannelRelease(bool stable) => LatestInChannel(stable) != null;

        // Force-install the latest release of the given channel, even if it is a DOWNGRADE relative to
        // the running build (deliberate channel switch, not a version-gated update).
        [HideFromIl2Cpp]
        public void TriggerChannelSwitch(bool stable) {
            var r = LatestInChannel(stable);
            if (r != null) StartDownloadRelease(r, managerMode: true);
        }
    }

    // Minimal DTOs matching the GitHub Releases API JSON. Kept local so this plugin needs no
    // compile-time reference to TheOtherRoles.
    public class GithubRelease {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("tag_name")]
        public string Tag { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; }

        [JsonPropertyName("published_at")]
        public string PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string Description { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAsset> Assets { get; set; }

        public Version Version => Version.Parse(Tag.Replace("v", string.Empty));

        public bool IsNewer(Version version) {
            return Version > version;
        }
    }

    public class GithubAsset {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; }
    }
}
