// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ModManagerUI - the Mod Manager panel in the main menu, drawn with VanillaUI (lobby-pane look).
 *
 * Three tabs:
 *   Installed  every mod that registered itself (ModManagerRegistry): on/off for the next start,
 *              update check and download through the mod's own updater, release notes, GitHub link
 *   All mods   the whole catalog (UTSModCatalog): what is installed, switched off or missing, with a
 *              download of the newest release for anything missing (UTSModDownloader)
 *   Modpacks   named selections (UTSModpacks): save the current set-up, apply a pack, share it as a
 *              code through the clipboard, import a code, delete
 * The header keeps the "Update all" button and the shared test-versions switch (which also switches
 * every mod's update channel after a confirmation).
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff
{
    public class ModManagerUI : MonoBehaviour
    {
        public static ModManagerUI Instance { get; private set; }
        public static bool IsUIOpen { get; private set; }

        public ModManagerUI(IntPtr ptr) : base(ptr) { }

        private GameObject _popup;
        private enum Tab { Installed, Catalog, Modpacks }
        private Tab _tab = Tab.Installed;

        // References per installed-mod card so the polling coroutine can update the download state
        // live (progress, "restart required", errors) without a rebuild.
        private class ModEntryRefs
        {
            public ModInfo Mod;
            public TMPro.TextMeshProUGUI StatusText;
            public GameObject UpdateButton;
            public TMPro.TextMeshProUGUI UpdateButtonText;
            public bool RuntimeEnabled;
            public bool NotesShown;
        }
        private readonly List<ModEntryRefs> _entryRefs = new List<ModEntryRefs>();

        // References per catalog row (download state of the newest-release jobs).
        private class CatalogRefs
        {
            public CatalogEntry Entry;
            public TMPro.TextMeshProUGUI StatusText;
            public GameObject DownloadButton;
        }
        private readonly List<CatalogRefs> _catalogRefs = new List<CatalogRefs>();

        private bool _updateAllRunning;
        private GameObject _updateAllButton;
        private TMPro.TextMeshProUGUI _updateAllButtonText;
        private TMPro.TextMeshProUGUI _headerSummaryText;
        private TMPro.TextMeshProUGUI _testVersionToggleText;
        private GameObject _testVersionToggle;
        private GameObject _confirmOverlay;
        private bool _rebuiltForNotes;
        private string _summary = "";     // survives a rebuild (tab switch)

        private const float PanelW = 1240f, PanelH = 880f, Inset = 30f;

        // Release notes for display: crude Markdown strip to the first ~10 lines / ~600 characters,
        // with an ellipsis when cut. TMP rich text is neutralised so notes cannot inject tags.
        private static string StripAndTruncateNotes(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var srcLines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var outLines = new List<string>();
            foreach (var lineRaw in srcLines)
            {
                string line = lineRaw.Trim();
                line = Regex.Replace(line, @"^#{1,6}\s*", "");
                line = Regex.Replace(line, @"^[\*\-\+]\s+", "• ");
                line = Regex.Replace(line, @"\[([^\]]+)\]\([^\)]*\)", "$1");
                line = line.Replace("**", "").Replace("__", "").Replace("`", "");
                outLines.Add(line);
            }
            string joined = string.Join("\n", outLines).Trim();
            if (joined.Length == 0) return "";

            bool truncated = false;
            var keep = joined.Split('\n');
            if (keep.Length > 10) { joined = string.Join("\n", keep.Take(10)); truncated = true; }
            if (joined.Length > 600) { joined = joined.Substring(0, 600).TrimEnd(); truncated = true; }
            joined = joined.Replace("<", "<​");
            if (truncated) joined += " ...";
            return joined;
        }

        public void Awake()
        {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        public void Show()
        {
            try
            {
                if (_popup != null) { UsefulTORStuffPlugin.Logger?.LogWarning("Mod Manager is already open."); return; }
                // Re-check for new versions on opening (throttled to once a minute); the result shows
                // up through the running CoRefreshStates loop.
                ModManagerRegistry.MaybeCheckForUpdates();
                Build();
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to show Mod Manager UI: {ex}");
            }
        }

        private void Build()
        {
            try
            {
                _popup = VanillaUI.Canvas("ModManagerUI", 9999, true);   // above everything
                VanillaUI.Backdrop(_popup, Hide);
                var panel = VanillaUI.CenterPanel(_popup, new Vector2(PanelW, PanelH));

                float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.modmanagerui.title"));
                BuildHeader(panel, y - 10f);
                BuildContent(panel, y - 112f);
                VanillaUI.CloseButton(panel, UTSLocalization.Tr("uts.modmanagerui.close_button"), Hide);

                IsUIOpen = true;
                DisableBackgroundUI();
                this.StartCoroutine(CoRefreshStates());
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to create Mod Manager UI: {ex}");
                // AUDIT L-15: a throw after DisableBackgroundUI() used to leave the menu behind this
                // popup switched off. Undo on the way out and drop IsUIOpen so a retry is possible.
                try { EnableBackgroundUI(); } catch { }
                IsUIOpen = false;
                if (_popup != null) { Destroy(_popup); _popup = null; }
            }
        }

        /// <summary>Autotest (UIGallery): switch to a tab by index while the panel is open.</summary>
        public void DiagShowTab(int index)
        {
            _tab = (Tab)Mathf.Clamp(index, 0, 2);
            if (_popup != null) Rebuild();
        }

        private void Rebuild()
        {
            _entryRefs.Clear(); _catalogRefs.Clear();
            EnableBackgroundUI();
            if (_popup != null) { Destroy(_popup); _popup = null; }
            IsUIOpen = false;
            Build();
        }

        // ====================================================================
        // Background: hide the menu's own buttons while the panel is open
        // ====================================================================
        private readonly List<GameObject> _hiddenObjects = new List<GameObject>();
        private readonly List<PassiveButton> _disabledButtons = new List<PassiveButton>();

        // P2.5: a HEURISTIC, not an exact selection: hides every object whose name contains
        // update/button/popup/dialog/confirm, so nothing behind the Mod Manager reacts; everything is
        // restored in EnableBackgroundUI(). Objects under _popup are excluded.
        private void DisableBackgroundUI()
        {
            _hiddenObjects.Clear();
            _disabledButtons.Clear();
            var allCanvases = GameObject.FindObjectsOfType<Canvas>();
            foreach (var canvas in allCanvases)
            {
                if (canvas.gameObject == _popup || canvas.sortingOrder >= 9999 || !canvas.gameObject.activeInHierarchy) continue;
                foreach (var child in canvas.GetComponentsInChildren<Transform>(true))
                {
                    if (_popup != null && child.IsChildOf(_popup.transform)) continue;
                    string n = child.name.ToLower();
                    if (child.gameObject.activeInHierarchy &&
                        (n.Contains("update") || n.Contains("button") || n.Contains("popup") || n.Contains("dialog") || n.Contains("confirm")))
                    {
                        child.gameObject.SetActive(false);
                        _hiddenObjects.Add(child.gameObject);
                    }
                }
            }
            foreach (var button in GameObject.FindObjectsOfType<PassiveButton>())
            {
                if (!button.transform.IsChildOf(_popup.transform) && button.enabled)
                {
                    button.enabled = false;
                    _disabledButtons.Add(button);
                }
            }
        }

        private void EnableBackgroundUI()
        {
            try
            {
                foreach (var button in _disabledButtons) if (button != null) button.enabled = true;
                _disabledButtons.Clear();
                foreach (var go in _hiddenObjects) if (go != null) go.SetActive(true);
                _hiddenObjects.Clear();
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Error re-enabling background UI: {ex}");
            }
        }

        // ====================================================================
        // Header: tabs left, Update all + test versions right, summary line below
        // ====================================================================
        private void BuildHeader(GameObject panel, float y)
        {
            float x = VanillaUI.FrameW + Inset;
            TabButton(panel, Tab.Installed, UTSLocalization.Tr("uts.modmanagerui.tab_installed"), x, y); x += 196;
            TabButton(panel, Tab.Catalog, UTSLocalization.Tr("uts.modmanagerui.tab_catalog"), x, y); x += 196;
            TabButton(panel, Tab.Modpacks, UTSLocalization.Tr("uts.modmanagerui.tab_modpacks"), x, y);

            // test versions (right) and update all (left of it)
            _testVersionToggle = VanillaUI.Button(panel, "", new Vector2(-VanillaUI.FrameW - Inset, y), new Vector2(230, 40), VanillaUI.Grey,
                OnTestVersionToggle, out _testVersionToggleText, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            UpdateTestVersionToggleText();
            _updateAllButton = VanillaUI.Button(panel, UTSLocalization.Tr("uts.modmanagerui.update_all_button"),
                new Vector2(-VanillaUI.FrameW - Inset - 242, y), new Vector2(190, 40), VanillaUI.Blue,
                () => { if (!_updateAllRunning) this.StartCoroutine(CoUpdateAll()); }, out _updateAllButtonText,
                new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            RefreshUpdateAllButton();

            _headerSummaryText = VanillaUI.Label(panel, _summary, 15, VanillaUI.Warn, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                new Vector2(0, y - 50), new Vector2(-2 * (VanillaUI.FrameW + Inset), 24), TMPro.TextAlignmentOptions.Center);
        }

        private void TabButton(GameObject panel, Tab tab, string label, float x, float y)
        {
            bool sel = _tab == tab;
            VanillaUI.Button(panel, label, new Vector2(x, y), new Vector2(186, 40), sel ? VanillaUI.TealBright : VanillaUI.Teal,
                () => { if (_tab != tab) { _tab = tab; Rebuild(); } }, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
        }

        private void SetSummary(string text, Color? color = null)
        {
            _summary = text ?? "";
            if (_headerSummaryText == null) return;
            _headerSummaryText.color = color ?? VanillaUI.Warn;
            VanillaUI.SetText(_headerSummaryText, _summary);
        }

        // ====================================================================
        // Test-versions switch and channel switch
        // ====================================================================
        private int CountChannelMods(bool stable)
        {
            int n = 0;
            try {
                foreach (var m in ModManagerRegistry.GetAllMods())
                    try { if (m.RuntimeEnabled && (m.HasChannelRelease?.Invoke(stable) ?? false)) n++; } catch { }
            } catch { }
            return n;
        }

        private void OnTestVersionToggle()
        {
            if (_updateAllRunning) return;
            bool nv = !VersionDisplay.ShowTestVersions();
            bool stableChannel = !nv;            // OFF -> stable, ON -> test
            string ch = stableChannel ? "STABLE" : "TEST";
            int affected = CountChannelMods(stableChannel);
            string onOffLabel = nv ? "ON" : "OFF";
            string msg = affected > 0
                ? UTSLocalization.Tr("uts.modmanagerui.confirm_switch_msg_affected", onOffLabel, affected, ch)
                : UTSLocalization.Tr("uts.modmanagerui.confirm_switch_msg_none", onOffLabel, ch);
            ShowConfirm(UTSLocalization.Tr("uts.modmanagerui.confirm_switch_title"), msg, () => {
                VersionDisplay.SetShowTestVersions(nv);
                if (UsefulTORStuffPlugin.ShowTestVersionsConfig != null)
                    UsefulTORStuffPlugin.ShowTestVersionsConfig.Value = nv;
                UpdateTestVersionToggleText();
                if (affected > 0) this.StartCoroutine(CoSwitchChannel(stableChannel));
            });
        }

        private void UpdateTestVersionToggleText()
        {
            if (_testVersionToggleText == null) return;
            bool on = VersionDisplay.ShowTestVersions();
            VanillaUI.SetText(_testVersionToggleText, on ? UTSLocalization.Tr("uts.modmanagerui.test_versions_on") : UTSLocalization.Tr("uts.modmanagerui.test_versions_off"));
            VanillaUI.Recolor(_testVersionToggle, on ? VanillaUI.Green : VanillaUI.Grey);
        }

        // Switch every eligible mod to its newest release of the given channel, sequentially (each
        // updater is a single-busy state machine). A deliberate, possibly DOWNgrading switch.
        private IEnumerator CoSwitchChannel(bool stable)
        {
            if (_updateAllRunning) yield break;
            _updateAllRunning = true;
            RefreshUpdateAllButton();

            List<ModInfo> mods;
            try { mods = ModManagerRegistry.GetAllMods(); } catch { mods = new List<ModInfo>(); }
            int done = 0, failed = 0;
            foreach (var mod in mods)
            {
                bool has = false;
                try { has = mod.RuntimeEnabled && (mod.HasChannelRelease?.Invoke(stable) ?? false); } catch { }
                if (!has || mod.TriggerChannelSwitch == null || mod.GetUpdateState == null) continue;

                try { mod.TriggerChannelSwitch(stable); }
                catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"Channel switch trigger failed for {mod.Name}: {ex.Message}");
                    failed++;
                    continue;
                }
                yield return WaitForUpdater(mod, () => mod.TriggerChannelSwitch(stable));
                int state = 0;
                try { state = mod.GetUpdateState?.Invoke() ?? 0; } catch { state = 3; }
                if (state == 2) done++; else failed++;
            }

            _updateAllRunning = false;
            RefreshUpdateAllButton();
            SetSummary(failed == 0
                ? UTSLocalization.Tr("uts.modmanagerui.channel_switch_summary_ok", done, stable ? "Stable" : "Test")
                : UTSLocalization.Tr("uts.modmanagerui.channel_switch_summary_partial", done, failed),
                failed == 0 ? VanillaUI.Good : VanillaUI.Warn);
        }

        // The updater ignores a trigger while it runs its own re-check (audit 04.10.): wait up to 20 s
        // for it to start, re-trigger every 2 s, then wait up to 90 s for success (2) or error (3).
        private static IEnumerator WaitForUpdater(ModInfo mod, Action retrigger)
        {
            float startWait = 20f, retryIn = 2f;
            int st = 0;
            while (startWait > 0f)
            {
                try { st = mod.GetUpdateState?.Invoke() ?? 0; } catch { st = 3; }
                if (st == 1 || st == 2 || st == 3) break;
                startWait -= Time.deltaTime;
                retryIn -= Time.deltaTime;
                if (retryIn <= 0f) { retryIn = 2f; try { retrigger(); } catch { } }
                yield return null;
            }
            float timeout = 90f;
            while (timeout > 0f)
            {
                try { st = mod.GetUpdateState?.Invoke() ?? 0; } catch { st = 3; }
                if (st == 2 || st == 3) break;
                timeout -= Time.deltaTime;
                yield return null;
            }
        }

        // ====================================================================
        // Confirmation dialog
        // ====================================================================
        private void ShowConfirm(string title, string message, Action onYes)
        {
            HideConfirm();
            if (_popup == null) { onYes?.Invoke(); return; }
            _confirmOverlay = VanillaUI.Backdrop(_popup, null);
            _confirmOverlay.name = "ConfirmOverlay";
            var box = VanillaUI.CenterPanel(_confirmOverlay, new Vector2(600, 320));
            float y = VanillaUI.Title(box, title, 14f, 26f);
            VanillaUI.Label(box, message, 15, Color.white, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(-2 * (VanillaUI.FrameW + 24f), -(VanillaUI.FrameW + 70f + 76f) - 10f), TMPro.TextAlignmentOptions.Top)
                .rectTransform.offsetMax = new Vector2(-(VanillaUI.FrameW + 24f), y);
            VanillaUI.Button(box, UTSLocalization.Tr("uts.modmanagerui.confirm_yes"), new Vector2(-100, VanillaUI.FrameW + 16), new Vector2(180, 46),
                VanillaUI.Green, () => { HideConfirm(); onYes?.Invoke(); });
            VanillaUI.Button(box, UTSLocalization.Tr("uts.modmanagerui.confirm_cancel"), new Vector2(100, VanillaUI.FrameW + 16), new Vector2(180, 46),
                VanillaUI.Grey, HideConfirm);
        }

        private void HideConfirm()
        {
            if (_confirmOverlay != null) { Destroy(_confirmOverlay); _confirmOverlay = null; }
        }

        // ====================================================================
        // Update all
        // ====================================================================
        private void RefreshUpdateAllButton()
        {
            if (_updateAllButton == null) return;
            bool any = false;
            try { any = ModManagerRegistry.GetAllMods().Any(m => { try { return m.RuntimeEnabled && (m.HasUpdate?.Invoke() ?? false); } catch { return false; } }); }
            catch { }
            bool on = any && !_updateAllRunning;
            VanillaUI.Recolor(_updateAllButton, on ? VanillaUI.Blue : VanillaUI.Grey);
            var b = _updateAllButton.GetComponentInChildren<Button>();
            if (b != null) b.interactable = on;
        }

        private IEnumerator CoUpdateAll()
        {
            if (_updateAllRunning) yield break;
            _updateAllRunning = true;
            RefreshUpdateAllButton();
            if (_updateAllButtonText != null) VanillaUI.SetText(_updateAllButtonText, UTSLocalization.Tr("uts.modmanagerui.updating_label"));

            int updated = 0, failed = 0;
            List<ModInfo> mods;
            try { mods = ModManagerRegistry.GetAllMods(); }
            catch { mods = new List<ModInfo>(); }

            foreach (var mod in mods)
            {
                bool has = false;
                try { has = mod.RuntimeEnabled && (mod.HasUpdate?.Invoke() ?? false); } catch { }
                if (!has || mod.TriggerUpdate == null || mod.GetUpdateState == null) continue;
                int pre = 0; try { pre = mod.GetUpdateState(); } catch { }
                if (pre == 2) { updated++; continue; }

                try { mod.TriggerUpdate(); }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"Update All: trigger failed for {mod.Name}: {ex.Message}");
                    failed++;
                    continue;
                }
                yield return WaitForUpdater(mod, () => mod.TriggerUpdate());
                int state = 0;
                try { state = mod.GetUpdateState?.Invoke() ?? 0; } catch { state = 3; }
                if (state == 2) updated++; else failed++;
            }

            _updateAllRunning = false;
            if (updated == 0 && failed == 0) SetSummary(UTSLocalization.Tr("uts.modmanagerui.update_all_none"), VanillaUI.Muted);
            else SetSummary(failed == 0
                    ? UTSLocalization.Tr("uts.modmanagerui.update_all_ok", updated)
                    : UTSLocalization.Tr("uts.modmanagerui.update_all_partial", updated, failed),
                    failed == 0 ? VanillaUI.Good : VanillaUI.Warn);
            if (_updateAllButtonText != null) VanillaUI.SetText(_updateAllButtonText, UTSLocalization.Tr("uts.modmanagerui.update_all_button"));
            RefreshUpdateAllButton();
        }

        // ====================================================================
        // Content: a scroll view with the active tab's rows
        // ====================================================================
        private const float ScrollbarWidth = 14f;

        private void BuildContent(GameObject panel, float top)
        {
            var scrollView = new GameObject("ScrollView");
            scrollView.transform.SetParent(panel.transform, false);
            var svr = VanillaUI.Rect(scrollView, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            svr.offsetMin = new Vector2(VanillaUI.FrameW + Inset, VanillaUI.FrameW + 84f);
            svr.offsetMax = new Vector2(-(VanillaUI.FrameW + Inset), top);

            var scrollRect = scrollView.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 35f;
            scrollRect.inertia = false;

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollView.transform, false);
            var viewportRect = VanillaUI.Rect(viewport, Vector2.zero, Vector2.one, new Vector2(0, 1));
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = new Vector2(-ScrollbarWidth, 0);
            viewport.AddComponent<RectMask2D>();

            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = VanillaUI.Rect(content, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = Vector2.zero;

            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.verticalScrollbar = CreateScrollbar(scrollView);
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            float yPos = -4;
            switch (_tab)
            {
                case Tab.Installed:
                    _entryRefs.Clear();
                    foreach (var mod in ModManagerRegistry.GetAllMods()) yPos = CreateModEntry(content, mod, yPos);
                    break;
                case Tab.Catalog:
                    _catalogRefs.Clear();
                    yPos = CreateCatalogRows(content, yPos);
                    break;
                case Tab.Modpacks:
                    yPos = CreateModpackRows(content, yPos);
                    break;
            }
            contentRect.sizeDelta = new Vector2(0, Mathf.Abs(yPos) + 12);
            scrollRect.verticalNormalizedPosition = 1f;
        }

        private UnityEngine.UI.Scrollbar CreateScrollbar(GameObject scrollView)
        {
            var bar = VanillaUI.Rounded(scrollView, VanillaUI.Field, 5);
            bar.name = "Scrollbar";
            var barRect = VanillaUI.Rect(bar, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 1));
            barRect.sizeDelta = new Vector2(ScrollbarWidth - 4f, 0);
            barRect.anchoredPosition = Vector2.zero;

            var slidingArea = new GameObject("SlidingArea");
            slidingArea.transform.SetParent(bar.transform, false);
            VanillaUI.Stretch(slidingArea);

            var handle = VanillaUI.Rounded(slidingArea, VanillaUI.Frame, 5);
            handle.name = "Handle";
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = Vector2.zero;
            var handleImg = handle.GetComponent<Image>();
            handleImg.raycastTarget = true;

            var scrollbar = bar.AddComponent<UnityEngine.UI.Scrollbar>();
            scrollbar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImg;
            return scrollbar;
        }

        // ====================================================================
        // Tab 1: installed mods
        // ====================================================================
        private float CreateModEntry(GameObject parent, ModInfo mod, float yPos)
        {
            bool runtimeEnabled = mod.RuntimeEnabled;
            bool configEnabled = mod.Enabled?.Value ?? true;

            var entry = VanillaUI.Row(parent, yPos, 132, 2, VanillaUI.Field);
            entry.name = $"Mod_{mod.Guid}";
            var entryRect = entry.GetComponent<RectTransform>();

            // a coloured edge in the mod's colour, grey when it is not running
            var edge = VanillaUI.Box(entry, Vector2.zero, new Vector2(6, 132), runtimeEnabled ? mod.ButtonColor : VanillaUI.Frame, 3);
            edge.GetComponent<Image>().raycastTarget = false;

            var name = VanillaUI.Text(entry, $"{mod.Name} <size=70%><color=#B9C1C5>v{VersionDisplay.Format(mod.Version)}</color></size>", 24,
                runtimeEnabled ? Color.white : VanillaUI.Muted, new Vector2(20, -10), new Vector2(700, 32), TMPro.FontStyles.Bold);
            name.enableWordWrapping = false; name.overflowMode = TMPro.TextOverflowModes.Ellipsis;

            var statusText = VanillaUI.Text(entry, "", 15, VanillaUI.Muted, new Vector2(20, -46), new Vector2(700, 22));
            var refs = new ModEntryRefs { Mod = mod, StatusText = statusText, RuntimeEnabled = runtimeEnabled };

            // buttons, right: on/off and update in the first row, extra toggle and GitHub in the second
            CreateToggleButton(entry, mod, runtimeEnabled, configEnabled);
            CreateUpdateButton(entry, mod, refs);
            if (mod.ExtraToggle != null) CreateExtraToggleButton(entry, mod);
            if (HasRepository(mod))
                VanillaUI.Button(entry, UTSLocalization.Tr("uts.modmanagerui.github_button"), new Vector2(-14, -58), new Vector2(160, 36), VanillaUI.Grey,
                    () => {
                        try { Application.OpenURL($"https://github.com/{mod.RepositoryOwner}/{mod.RepositoryName}"); }
                        catch (Exception ex) { UsefulTORStuffPlugin.Logger?.LogError($"Failed to open GitHub: {ex}"); }
                    }, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));

            _entryRefs.Add(refs);
            RefreshEntry(refs);

            // release notes of the newest version when an update is available and the updater has them
            float notesHeight = 0f;
            bool updateAvail = false;
            try { updateAvail = mod.HasUpdate?.Invoke() ?? false; } catch { }
            if (updateAvail && mod.GetReleaseNotes != null)
            {
                string rawNotes = null;
                try { rawNotes = mod.GetReleaseNotes(); } catch { }
                string notes = StripAndTruncateNotes(rawNotes);
                if (notes.Length > 0)
                {
                    int lineCount = notes.Split('\n').Length;
                    notesHeight = Mathf.Clamp(lineCount * 18f + 26f, 50f, 200f);
                    var notesBox = VanillaUI.Box(entry, new Vector2(20, -76), new Vector2(PanelW - 2 * (VanillaUI.FrameW + Inset) - 60, notesHeight), VanillaUI.Body, 6);
                    notesBox.GetComponent<Image>().raycastTarget = false;
                    var notesText = VanillaUI.Text(notesBox, UTSLocalization.Tr("uts.modmanagerui.whats_new", notes), 13, new Color(0.82f, 0.82f, 0.86f),
                        new Vector2(12, -8), new Vector2(PanelW - 2 * (VanillaUI.FrameW + Inset) - 84, notesHeight - 12));
                    notesText.overflowMode = TMPro.TextOverflowModes.Truncate;
                    refs.NotesShown = true;
                }
            }
            float h = 132 + (notesHeight > 0 ? notesHeight + 10 : 0);
            entryRect.sizeDelta = new Vector2(entryRect.sizeDelta.x, h);
            edge.GetComponent<RectTransform>().sizeDelta = new Vector2(6, h);

            // repository and id at the bottom
            VanillaUI.Label(entry, HasRepository(mod)
                    ? UTSLocalization.Tr("uts.modmanagerui.repository_line", mod.RepositoryOwner, mod.RepositoryName)
                    : UTSLocalization.Tr("uts.modmanagerui.local_mod_line"),
                13, VanillaUI.Muted, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(20, 30), new Vector2(-40, 18), TMPro.TextAlignmentOptions.Left);
            string displayGuid = mod.Guid.Length > 50 ? mod.Guid.Substring(0, 47) + "..." : mod.Guid;
            VanillaUI.Label(entry, UTSLocalization.Tr("uts.modmanagerui.id_line", displayGuid), 12, VanillaUI.Rule,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(20, 12), new Vector2(-40, 16), TMPro.TextAlignmentOptions.Left);

            return yPos - (h + 10);
        }

        private void CreateToggleButton(GameObject parent, ModInfo mod, bool runtimeEnabled, bool configEnabled)
        {
            // a pending change = the wanted (config) state differs from the running one: restart needed
            bool pendingChange = configEnabled != runtimeEnabled;
            GameObject button = null;
            TMPro.TextMeshProUGUI btnText = null;

            void Apply(bool cfg)
            {
                bool pending = cfg != runtimeEnabled;
                VanillaUI.SetText(btnText, pending ? UTSLocalization.Tr("uts.modmanagerui.restart_required")
                    : cfg ? UTSLocalization.Tr("uts.modmanagerui.disable_button") : UTSLocalization.Tr("uts.modmanagerui.enable_button"));
                VanillaUI.Recolor(button, pending ? VanillaUI.Amber : cfg ? VanillaUI.Red : VanillaUI.Green);
            }

            button = VanillaUI.Button(parent, "", new Vector2(-184, -12), new Vector2(160, 36), VanillaUI.Grey, () => {
                try
                {
                    if (mod.Enabled == null) return;
                    bool newValue = !mod.Enabled.Value;
                    mod.Enabled.Value = newValue;
                    mod.Enabled.ConfigFile?.Save();
                    Apply(newValue);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"Toggled {mod.Name} to {(newValue ? "ENABLED" : "DISABLED")}{(newValue != runtimeEnabled ? " - restart required" : " - reverted")}");
                }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogError($"Failed to toggle mod: {ex}");
                    VanillaUI.SetText(btnText, UTSLocalization.Tr("uts.modmanagerui.error_label"));
                }
            }, out btnText, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            Apply(configEnabled);
            _ = pendingChange;
        }

        // Extra live toggle per mod (HostFix' Snitch fallback): takes effect at once, no restart.
        private void CreateExtraToggleButton(GameObject parent, ModInfo mod)
        {
            string label = string.IsNullOrEmpty(mod.ExtraToggleLabel) ? UTSLocalization.Tr("uts.modmanagerui.extra_toggle_default_label") : mod.ExtraToggleLabel;
            GameObject button = null;
            TMPro.TextMeshProUGUI btnText = null;
            void Apply(bool on)
            {
                VanillaUI.SetText(btnText, UTSLocalization.Tr("uts.modmanagerui.extra_toggle_label", label, on ? "ON" : "OFF"));
                VanillaUI.Recolor(button, on ? VanillaUI.Green : VanillaUI.Grey);
            }
            button = VanillaUI.Button(parent, "", new Vector2(-184, -58), new Vector2(160, 36), VanillaUI.Grey, () => {
                try
                {
                    bool newValue = !mod.ExtraToggle.Value;
                    mod.ExtraToggle.Value = newValue;
                    mod.ExtraToggle.ConfigFile?.Save();
                    Apply(newValue);
                }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogError($"Failed to toggle {label} for {mod.Name}: {ex}");
                    VanillaUI.SetText(btnText, UTSLocalization.Tr("uts.modmanagerui.error_label"));
                }
            }, out btnText, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            Apply(mod.ExtraToggle.Value);
        }

        private void CreateUpdateButton(GameObject parent, ModInfo mod, ModEntryRefs refs)
        {
            TMPro.TextMeshProUGUI btnText = null;
            var button = VanillaUI.Button(parent, UTSLocalization.Tr("uts.modmanagerui.update_now_button"), new Vector2(-14, -12), new Vector2(160, 36),
                VanillaUI.Blue, () => {
                    try
                    {
                        UsefulTORStuffPlugin.Logger?.LogInfo($"Triggering update for {mod.Name}...");
                        mod.TriggerUpdate?.Invoke();
                        SetUpdateButton(refs, true, UTSLocalization.Tr("uts.modmanagerui.downloading_button"), false);
                    }
                    catch (Exception ex)
                    {
                        UsefulTORStuffPlugin.Logger?.LogError($"Failed to trigger update: {ex}");
                        VanillaUI.SetText(btnText, UTSLocalization.Tr("uts.modmanagerui.error_label"));
                    }
                }, out btnText, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
            refs.UpdateButton = button;
            refs.UpdateButtonText = btnText;
        }

        // Status line and update button of one installed-mod card; on build and from CoRefreshStates.
        private void RefreshEntry(ModEntryRefs r)
        {
            if (r == null || r.Mod == null || r.StatusText == null) return;
            bool runtime = r.RuntimeEnabled;
            bool config = r.Mod.Enabled?.Value ?? true;

            int state = 0;
            float progress = 0f;
            if (runtime)
            {
                try { state = r.Mod.GetUpdateState?.Invoke() ?? 0; } catch { }
                try { progress = r.Mod.GetUpdateProgress?.Invoke() ?? 0f; } catch { }
            }

            switch (state)
            {
                case 1:
                    int pct = Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f);
                    int stars = Mathf.CeilToInt(Mathf.Clamp01(progress) * 10);
                    string bar = new string('#', stars) + new string('.', 10 - stars);
                    Status(r, UTSLocalization.Tr("uts.modmanagerui.status_downloading_progress", pct, bar), VanillaUI.Blue);
                    SetUpdateButton(r, true, UTSLocalization.Tr("uts.modmanagerui.downloading_button"), false);
                    return;
                case 2:
                    Status(r, UTSLocalization.Tr("uts.modmanagerui.status_updated_restart"), VanillaUI.Warn);
                    SetUpdateButton(r, false, null, false);
                    return;
                case 3:
                    Status(r, UTSLocalization.Tr("uts.modmanagerui.status_update_failed"), VanillaUI.Bad);
                    SetUpdateButton(r, true, UTSLocalization.Tr("uts.modmanagerui.retry_button"), true);
                    return;
            }

            if (config != runtime)
            {
                Status(r, config ? UTSLocalization.Tr("uts.modmanagerui.status_restart_enable") : UTSLocalization.Tr("uts.modmanagerui.status_restart_disable"), VanillaUI.Warn);
                SetUpdateButton(r, false, null, false);
                return;
            }
            if (!runtime)
            {
                Status(r, UTSLocalization.Tr("uts.modmanagerui.status_disabled"), VanillaUI.Muted);
                SetUpdateButton(r, false, null, false);
                return;
            }

            bool hasUpdate = false;
            try { hasUpdate = r.Mod.HasUpdate?.Invoke() ?? false; } catch { }
            if (hasUpdate)
            {
                Status(r, UTSLocalization.Tr("uts.modmanagerui.status_update_available"), VanillaUI.Amber);
                SetUpdateButton(r, true, UTSLocalization.Tr("uts.modmanagerui.update_now_button"), true);
                return;
            }
            // "up to date" only once the check has completed AND the release list was loaded
            bool completed = true, loaded = true;
            try { completed = r.Mod.GetCheckCompleted?.Invoke() ?? true; } catch { }
            try { loaded = r.Mod.ReleasesLoaded?.Invoke() ?? true; } catch { }
            if (!HasRepository(r.Mod)) Status(r, UTSLocalization.Tr("uts.modmanagerui.status_local"), VanillaUI.Muted);
            else if (!completed) Status(r, UTSLocalization.Tr("uts.modmanagerui.status_checking"), VanillaUI.Warn);
            else if (!loaded) Status(r, UTSLocalization.Tr("uts.modmanagerui.status_check_unavailable"), VanillaUI.Amber);
            else Status(r, UTSLocalization.Tr("uts.modmanagerui.status_up_to_date"), VanillaUI.Good);
            SetUpdateButton(r, false, null, false);
        }

        private static void Status(ModEntryRefs r, string text, Color color)
        {
            r.StatusText.color = color;
            VanillaUI.SetText(r.StatusText, text);
        }

        private static void SetUpdateButton(ModEntryRefs r, bool active, string label, bool interactable)
        {
            if (r.UpdateButton == null) return;
            r.UpdateButton.SetActive(active);
            if (!active) return;
            if (label != null) VanillaUI.SetText(r.UpdateButtonText, label);
            var b = r.UpdateButton.GetComponentInChildren<Button>();
            if (b != null) b.interactable = interactable;
            VanillaUI.Recolor(r.UpdateButton, interactable ? VanillaUI.Blue : VanillaUI.Grey);
        }

        private static bool NotesWaiting(ModInfo mod)
        {
            try
            {
                if (mod.GetReleaseNotes == null || !(mod.HasUpdate?.Invoke() ?? false)) return false;
                if ((mod.GetUpdateState?.Invoke() ?? 0) != 0) return false;
                return StripAndTruncateNotes(mod.GetReleaseNotes()).Length > 0;
            }
            catch { return false; }
        }

        private static bool HasRepository(ModInfo mod) =>
            mod != null && !string.IsNullOrWhiteSpace(mod.RepositoryOwner) && !string.IsNullOrWhiteSpace(mod.RepositoryName);

        // ====================================================================
        // Tab 2: the catalog
        // ====================================================================
        private float CreateCatalogRows(GameObject parent, float yPos)
        {
            VanillaUI.Text(parent, UTSLocalization.Tr("uts.modmanagerui.catalog_hint"), 14, VanillaUI.Muted, new Vector2(4, yPos), new Vector2(1000, 22));
            yPos -= 30;
            bool pre = false;
            try { pre = VersionDisplay.ShowTestVersions(); } catch { }
            foreach (var e in UTSModCatalog.Entries)
            {
                var row = VanillaUI.Row(parent, yPos, 66, 2);
                var state = UTSModCatalog.StateOf(e, out var ver);
                var edge = VanillaUI.Box(row, Vector2.zero, new Vector2(6, 66),
                    state == LocalModState.Active ? VanillaUI.Green : state == LocalModState.Disabled ? VanillaUI.Amber : VanillaUI.Frame, 3);
                edge.GetComponent<Image>().raycastTarget = false;
                VanillaUI.Label(row, e.DisplayName, 20, state == LocalModState.Missing ? VanillaUI.Muted : Color.white,
                    new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(20, 8), new Vector2(420, 0), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
                VanillaUI.Label(row, UTSLocalization.Tr("uts.modmanagerui.repository_line", e.RepositoryOwner, e.RepositoryName), 12, VanillaUI.Rule,
                    new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(20, -16), new Vector2(420, 0), TMPro.TextAlignmentOptions.Left);
                var status = VanillaUI.Label(row, "", 15, VanillaUI.Muted,
                    new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(450, 0), new Vector2(420, 0), TMPro.TextAlignmentOptions.Left);
                var refs = new CatalogRefs { Entry = e, StatusText = status };

                var captured = e;
                refs.DownloadButton = VanillaUI.Button(row, UTSLocalization.Tr(pre ? "uts.modmanagerui.download_pre_button" : "uts.modmanagerui.download_button"),
                    new Vector2(-14, 0), new Vector2(200, 40), VanillaUI.Teal, () => {
                        var dl = UTSModDownloader.Instance;
                        if (dl == null) return;
                        dl.EnqueueLatest(captured, pre);
                        RefreshCatalogRow(refs);
                    }, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
                _catalogRefs.Add(refs);
                RefreshCatalogRow(refs);
                yPos -= 74;
            }
            return yPos;
        }

        private void RefreshCatalogRow(CatalogRefs r)
        {
            if (r?.StatusText == null) return;
            var dl = UTSModDownloader.Instance;
            var job = dl != null ? dl.JobOf(r.Entry.Id) : null;
            var state = UTSModCatalog.StateOf(r.Entry, out var ver);
            string vs = ver != null ? "v" + VersionDisplay.Format(ver) : "";
            string text; Color col; bool button = true;
            if (job != null && job.State == JobState.Working)
            {
                int blocks = Mathf.Clamp(Mathf.CeilToInt(job.Progress * 10), 0, 10);
                text = UTSLocalization.Tr("uts.modsync.row_downloading", new string('#', blocks) + new string('.', 10 - blocks), Mathf.RoundToInt(job.Progress * 100));
                col = VanillaUI.Blue; button = false;
            }
            else if (job != null && job.State == JobState.Pending) { text = UTSLocalization.Tr("uts.modmanagerui.catalog_queued"); col = VanillaUI.Blue; button = false; }
            else if (job != null && job.State == JobState.Failed) { text = UTSLocalization.Tr(job.ErrorKey ?? "uts.modsync.error_download"); col = VanillaUI.Bad; }
            else if (job != null && job.State == JobState.Done && job.NoChange) { text = UTSLocalization.Tr("uts.modmanagerui.catalog_current", "v" + VersionDisplay.Format(job.TargetVersion)); col = VanillaUI.Good; }
            else if (job != null && job.State == JobState.Done) { text = UTSLocalization.Tr("uts.modmanagerui.catalog_installed_restart", "v" + VersionDisplay.Format(job.TargetVersion)); col = VanillaUI.Warn; button = false; }
            else if (state == LocalModState.Active) { text = UTSLocalization.Tr("uts.modmanagerui.catalog_active", vs); col = VanillaUI.Good; }
            else if (state == LocalModState.Disabled) { text = UTSLocalization.Tr("uts.modmanagerui.catalog_disabled", vs); col = VanillaUI.Warn; }
            else { text = UTSLocalization.Tr("uts.modmanagerui.catalog_missing"); col = VanillaUI.Muted; }
            r.StatusText.color = col;
            VanillaUI.SetText(r.StatusText, text);
            if (r.DownloadButton != null && r.DownloadButton.activeSelf != button) r.DownloadButton.SetActive(button);
        }

        // ====================================================================
        // Tab 3: modpacks
        // ====================================================================
        private float CreateModpackRows(GameObject parent, float yPos)
        {
            VanillaUI.Text(parent, UTSLocalization.Tr("uts.modmanagerui.modpacks_hint"), 14, VanillaUI.Muted, new Vector2(4, yPos), new Vector2(1100, 40));
            yPos -= 46;
            VanillaUI.Button(parent, UTSLocalization.Tr("uts.modmanagerui.modpack_new"), new Vector2(4, yPos), new Vector2(300, 44), VanillaUI.Teal, () => {
                var p = UTSModpacks.FromCurrent(UTSModpacks.FreeName(UTSLocalization.Tr("uts.modmanagerui.modpack_stem")));
                UTSModpacks.Add(p);
                SetSummary(UTSLocalization.Tr("uts.modmanagerui.modpack_saved", p.Name, p.Mods.Count), VanillaUI.Good);
                Rebuild();
            }, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            VanillaUI.Button(parent, UTSLocalization.Tr("uts.modmanagerui.modpack_import"), new Vector2(316, yPos), new Vector2(300, 44), VanillaUI.Blue, () => {
                string code = "";
                try { code = GUIUtility.systemCopyBuffer ?? ""; } catch { }
                var p = UTSModpacks.FromCode(code, out string err, out int unknown);
                if (p == null) { SetSummary(UTSLocalization.Tr(err ?? "uts.modpacks.err_format"), VanillaUI.Bad); return; }
                if (UTSModpacks.All.Any(x => UTSModpacks.ToCode(x) == UTSModpacks.ToCode(p)))
                { SetSummary(UTSLocalization.Tr("uts.modmanagerui.modpack_duplicate", p.Name), VanillaUI.Warn); return; }
                UTSModpacks.Add(p);
                SetSummary(unknown > 0 ? UTSLocalization.Tr("uts.modmanagerui.modpack_imported_unknown", p.Name, unknown)
                                       : UTSLocalization.Tr("uts.modmanagerui.modpack_imported", p.Name), unknown > 0 ? VanillaUI.Warn : VanillaUI.Good);
                Rebuild();
            }, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            yPos -= 60;

            if (UTSModpacks.All.Count == 0)
            {
                VanillaUI.Text(parent, UTSLocalization.Tr("uts.modmanagerui.modpack_none"), 15, VanillaUI.Muted, new Vector2(4, yPos), new Vector2(1000, 24), TMPro.FontStyles.Italic);
                return yPos - 30;
            }

            foreach (var pack in UTSModpacks.All.ToList())
            {
                var row = VanillaUI.Row(parent, yPos, 86, 2);
                VanillaUI.Label(row, pack.Name, 20, Color.white, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1),
                    new Vector2(20, -10), new Vector2(-560, 28), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
                var desc = VanillaUI.Label(row, UTSModpacks.Describe(pack), 14, VanillaUI.Muted, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1),
                    new Vector2(20, -42), new Vector2(-560, 36), TMPro.TextAlignmentOptions.TopLeft);
                desc.enableWordWrapping = true;

                var captured = pack;
                VanillaUI.Button(row, UTSLocalization.Tr("uts.modmanagerui.modpack_apply"), new Vector2(-14, 0), new Vector2(160, 42), VanillaUI.Teal, () => {
                    ShowConfirm(UTSLocalization.Tr("uts.modmanagerui.modpack_apply_title"),
                        UTSLocalization.Tr("uts.modmanagerui.modpack_apply_msg", captured.Name, UTSModpacks.Describe(captured)), () => {
                            var res = UTSModpacks.Apply(captured);
                            SetSummary(UTSLocalization.Tr("uts.modmanagerui.modpack_applied", res.Downloads, res.Enabled, res.Disabled)
                                       + (res.Errors.Count > 0 ? " " + UTSLocalization.Tr("uts.modmanagerui.modpack_apply_errors", string.Join(", ", res.Errors)) : ""),
                                       res.Errors.Count > 0 ? VanillaUI.Warn : VanillaUI.Good);
                            _tab = Tab.Catalog;
                            Rebuild();
                        });
                }, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
                VanillaUI.Button(row, UTSLocalization.Tr("uts.modmanagerui.modpack_copy"), new Vector2(-186, 0), new Vector2(190, 42), VanillaUI.Blue, () => {
                    try { GUIUtility.systemCopyBuffer = UTSModpacks.ToCode(captured); } catch { }
                    SetSummary(UTSLocalization.Tr("uts.modmanagerui.modpack_copied", captured.Name), VanillaUI.Good);
                }, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
                VanillaUI.Button(row, UTSLocalization.Tr("uts.modmanagerui.modpack_delete"), new Vector2(-388, 0), new Vector2(130, 42), VanillaUI.Red, () => {
                    ShowConfirm(UTSLocalization.Tr("uts.modmanagerui.modpack_delete_title"),
                        UTSLocalization.Tr("uts.modmanagerui.modpack_delete_msg", captured.Name), () => { UTSModpacks.Delete(captured); Rebuild(); });
                }, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
                yPos -= 94;
            }
            return yPos;
        }

        // ====================================================================
        // Polling while the panel is open
        // ====================================================================
        private IEnumerator CoRefreshStates()
        {
            while (IsUIOpen)
            {
                bool rebuildForNotes = false;
                foreach (var r in _entryRefs)
                {
                    if (r?.Mod == null) continue;
                    RefreshEntry(r);
                    if (!r.NotesShown && NotesWaiting(r.Mod)) rebuildForNotes = true;
                }
                foreach (var c in _catalogRefs) RefreshCatalogRow(c);
                // an update found by the re-check AFTER the panel was built: its release notes only
                // exist in a fresh build (audit 04.10.). Once.
                if (rebuildForNotes && !_updateAllRunning && !_rebuiltForNotes)
                {
                    _rebuiltForNotes = true;
                    Rebuild();
                    yield break;
                }
                if (!_updateAllRunning) RefreshUpdateAllButton();
                yield return new WaitForSeconds(0.25f);
            }
        }

        public void Hide()
        {
            try
            {
                IsUIOpen = false;
                _entryRefs.Clear(); _catalogRefs.Clear();
                EnableBackgroundUI();
                if (_popup != null) { Destroy(_popup); _popup = null; }
                _tab = Tab.Installed;
                _summary = "";
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to hide Mod Manager UI: {ex}");
            }
        }
    }
}
