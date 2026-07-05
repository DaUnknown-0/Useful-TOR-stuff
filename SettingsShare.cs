// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SettingsShare - rounds out TOR's built-in settings copy/paste (the Copy/Paste icons in the TOR
 * settings menu) into a full export/import for the host.
 *
 * What TOR already does: copyToClipboard() puts "<version>!<base64 custom options>!<base64 vanilla
 * options>" into the clipboard - that string covers EVERY CustomOption (TOR's AND every loaded
 * mod's, they all live in CustomOption.options) plus the Among-Us-native GameOptions.
 * pasteFromClipboard() applies it with per-option fallback: unknown option ids (a mod that isn't
 * loaded / an option that no longer exists) are logged and skipped; options missing from the
 * string keep their current value.
 *
 * What this module adds:
 *   1. FRESHNESS - TOR's export uses the vanillaSettings snapshot, which is only refreshed on
 *      preset switches. A Prefix on copyToClipboard re-snapshots the CURRENT native settings, so
 *      the exported string always matches what the host sees in the menu right now.
 *   2. FILE EXPORT - a Postfix additionally saves every export to
 *      BepInEx/config/TOR-SettingsExports/ (timestamped .txt), silently. The clipboard string
 *      itself stays the primary share channel (paste it to friends), the files feed the picker.
 *   3. IMPORT PICKER - a third button next to TOR's Copy/Paste icons opens an in-game file picker
 *      that lists the saved exports (newest first, with date/time); clicking one imports it
 *      through TOR's own paste logic (including all its fallbacks). No path juggling needed.
 *   4. PASTE DIALOG - the picker's top row opens a password-gate-style input box (same key handling
 *      as LobbyPasswordGate): Ctrl+V pastes the settings string, typing/backspace edits it, Enter
 *      imports, Escape closes. Both import paths (file OR string) are always available.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TMPro;

namespace UsefulTORStuff {
    public static class SettingsShare {
        private static string ExportDir =>
            Path.Combine(BepInEx.Paths.ConfigPath, "TOR-SettingsExports");

        private static void PostChat(string text) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.Chat != null && PlayerControl.LocalPlayer != null)
                    hud.Chat.AddChat(PlayerControl.LocalPlayer, text);
            } catch { }
        }

        // ====================================================================
        // Export: freshness prefix + silent file save.
        // ====================================================================
        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.copyToClipboard))]
        static class CopyPatch {
            public static void Prefix() {
                try {
                    if (GameManager.Instance != null && GameManager.Instance.LogicOptions != null)
                        CustomOption.saveVanillaOptions();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsShare] vanilla snapshot failed: {e.Message}");
                }
            }

            public static void Postfix() {
                try {
                    string data = GUIUtility.systemCopyBuffer;
                    if (string.IsNullOrWhiteSpace(data)) return;
                    Directory.CreateDirectory(ExportDir);
                    string stamped = Path.Combine(ExportDir, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                    File.WriteAllText(stamped, data);
                    PostChat(UTSLocalization.Tr("uts.settingsshare.export_ok_chat"));
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SettingsShare] settings exported to {stamped}.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] file export failed: {e}");
                }
            }
        }

        // ====================================================================
        // Import picker: third button next to TOR's Copy/Paste in the settings menu.
        // TOR creates its two buttons in a GameSettingMenu.Start postfix (holder object
        // "copyPasteButtonParent"); this runs after it (Priority.Low) and clones one more.
        // ====================================================================
        private static GameObject picker;   // open picker panel (lives inside the settings menu)

        [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Start))]
        [HarmonyPriority(Priority.Low)]
        static class SettingsMenuButtonPatch {
            public static void Postfix(GameSettingMenu __instance) {
                try {
                    picker = null;      // menu was rebuilt - any old picker/dialog died with it
                    pasteDialog = null;
                    pasteBuffer = "";
                    var holder = GameObject.Find("copyPasteButtonParent");
                    var menu = GameObject.Find("PlayerOptionsMenu(Clone)");
                    var template = menu?.transform.Find("CloseButton")?.gameObject;
                    if (holder == null || template == null) return; // TOR block absent (e.g. HideNSeek)

                    var importButton = UnityEngine.Object.Instantiate(template, holder.transform);
                    importButton.name = "UTSImportFileButton";
                    importButton.transform.localPosition = new Vector3(0.9f, 0.02f, -2f);
                    var passive = importButton.GetComponent<PassiveButton>();
                    var renderer = importButton.GetComponentInChildren<SpriteRenderer>();
                    var activeRenderer = importButton.transform.childCount > 1
                        ? importButton.transform.GetChild(1).GetComponent<SpriteRenderer>() : null;
                    if (passive == null || renderer == null) return;
                    renderer.sprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.Paste.png", 100f);
                    if (activeRenderer != null) {
                        activeRenderer.sprite = Helpers.loadSpriteFromResources("TheOtherRoles.Resources.PasteActive.png", 100f);
                        activeRenderer.transform.localPosition = Vector3.zero;
                    }
                    // Cyan tint = "paste from FILE" (vs. TOR's white paste-from-clipboard).
                    var cyan = new Color(0.45f, 0.85f, 1f);
                    renderer.color = cyan;
                    if (activeRenderer != null) activeRenderer.color = cyan;

                    passive.OnClick.RemoveAllListeners();
                    passive.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                    passive.OnClick.AddListener((Action)(() => TogglePicker(holder)));
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] import button failed: {e}");
                }
            }
        }

        private static void TogglePicker(GameObject holder) {
            if (picker != null || pasteDialog != null) { ClosePicker(); ClosePasteDialog(); return; }
            BuildPicker(holder);
        }

        private static void ClosePicker() {
            try { if (picker != null) UnityEngine.Object.Destroy(picker); } catch { }
            picker = null;
        }

        // Among Us renders the HUD/UI layer through a SEPARATE "UI Camera", not Camera.main -
        // measuring the visible rect through the wrong camera lands the panel off-centre. Resolve
        // the camera that actually renders our layer (by name + culling mask), cached.
        private static Camera fitCam;
        private static Camera FitCamera(int layer) {
            if (fitCam != null && fitCam.isActiveAndEnabled && (fitCam.cullingMask & (1 << layer)) != 0)
                return fitCam;
            fitCam = null;
            var all = Camera.allCameras;
            foreach (var c in all)
                if (c != null && c.gameObject.name == "UI Camera" && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null)
                foreach (var c in all)
                    if (c != null && c != Camera.main && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null) fitCam = Camera.main;
            if (fitCam != null)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[SettingsShare] fit camera resolved: {fitCam.gameObject.name} ortho={fitCam.orthographicSize} " +
                    $"pos={fitCam.transform.position} hasLayer={(fitCam.cullingMask & (1 << layer)) != 0}");
            return fitCam;
        }

        // Centre the panel on the ACTUALLY visible screen area of the rendering camera and scale it
        // screen-relative (design proportion: "panel on a 6-unit-tall screen", clamped to the
        // visible width). World scale is targeted (divided by the parent's lossyScale) in case the
        // menu hierarchy is scaled. Re-applied every frame from the HudManager.Update patch below.
        private static void CameraFit(GameObject go, float designW) {
            if (go == null) return;
            var cam = FitCamera(go.layer);
            if (cam == null) return;
            Vector3 bl = cam.ScreenToWorldPoint(new Vector3(0f, 0f, 10f));
            Vector3 tr = cam.ScreenToWorldPoint(new Vector3(Screen.width, Screen.height, 10f));
            float visW = Mathf.Abs(tr.x - bl.x), visH = Mathf.Abs(tr.y - bl.y);
            if (visW < 0.01f || visH < 0.01f) return;
            float scale = Mathf.Min(visH / 6f, visW * 0.98f / designW);
            float parentScale = go.transform.parent != null ? go.transform.parent.lossyScale.x : 1f;
            if (parentScale < 0.0001f) parentScale = 1f;
            go.transform.localScale = Vector3.one * (scale / parentScale);
            var p = go.transform.position;
            go.transform.position = new Vector3((bl.x + tr.x) / 2f, (bl.y + tr.y) / 2f, p.z);
        }

        private const float PickerDesignW = 4.9f;
        private const float DialogDesignW = 5.6f;

        private static Sprite whiteSprite;
        private static Sprite WhiteSprite() {
            if (whiteSprite != null) return whiteSprite;
            var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags |= HideFlags.HideAndDontSave;
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            whiteSprite.hideFlags |= HideFlags.HideAndDontSave;
            return whiteSprite;
        }

        private static SpriteRenderer NewRect(Transform parent, Vector3 localPos, Vector2 size, Color color, int sort) {
            var go = new GameObject("UTSRect");
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite();
            sr.color = color;
            sr.sortingOrder = sort;
            return sr;
        }

        private static TextMeshPro NewText(Transform parent, string text, float fontSize, Color color,
                                           TextAlignmentOptions alignment = TextAlignmentOptions.Left) {
            var template = HudManager.Instance.KillButton.cooldownTimerText;
            var tmp = UnityEngine.Object.Instantiate(template, parent);
            tmp.gameObject.SetActive(true);
            tmp.gameObject.layer = parent.gameObject.layer;
            tmp.transform.localScale = Vector3.one;
            tmp.transform.localPosition = Vector3.zero;
            // The clone inherits the kill button's RectTransform; TMP aligns text INSIDE that rect,
            // so Left/Right-aligned labels land half a rect away from the intended anchor. Collapse
            // the rect to a point: the transform position becomes the exact alignment anchor
            // (Left = text starts there, Center = centred there, Right = text ends there).
            var rt = tmp.rectTransform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            tmp.margin = Vector4.zero;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.enableAutoSizing = false;
            tmp.enableWordWrapping = false;
            tmp.alignment = alignment;
            tmp.color = color;
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 601;
            return tmp;
        }

        // A wide invisible click row cloned from the CloseButton template: sprites hidden, collider
        // resized - the menu's own PassiveButton/raycast machinery does the click handling.
        private static PassiveButton NewRowButton(GameObject template, Transform parent, Vector3 localPos, Vector2 clickSize) {
            var go = UnityEngine.Object.Instantiate(template, parent);
            go.name = "UTSPickerRow";
            go.layer = parent.gameObject.layer;
            go.transform.localPosition = localPos;
            foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true)) sr.enabled = false;
            var collider = go.GetComponent<BoxCollider2D>();
            if (collider != null) { collider.size = clickSize; collider.offset = Vector2.zero; }
            var passive = go.GetComponent<PassiveButton>();
            passive.OnClick.RemoveAllListeners();
            passive.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            return passive;
        }

        // Pretty display name: "settings-20260704-000904.txt" -> "04.07.2026  00:09:04".
        private static string DisplayName(FileInfo f) {
            var name = Path.GetFileNameWithoutExtension(f.Name);
            if (name.StartsWith("settings-") && name.Length >= 24
                && DateTime.TryParseExact(name.Substring(9), "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.ToString("dd.MM.yyyy  HH:mm:ss");
            return f.Name;
        }

        private static void BuildPicker(GameObject holder) {
            try {
                ClosePicker();
                var template = GameObject.Find("PlayerOptionsMenu(Clone)")?.transform.Find("CloseButton")?.gameObject;
                if (template == null) return;

                picker = new GameObject("UTSImportPicker");
                picker.layer = holder.layer;
                // Child of the copy/paste holder so it dies with the menu; the actual position/scale
                // is camera-fitted (screen centre) right below and re-applied every frame.
                picker.transform.SetParent(holder.transform, false);
                picker.transform.localPosition = new Vector3(0f, 0f, -10f);
                CameraFit(picker, PickerDesignW);

                List<FileInfo> files;
                try {
                    // Cap at 5: an 8-entry list grows past the visible settings area (reviewer finding).
                    files = Directory.Exists(ExportDir)
                        ? new DirectoryInfo(ExportDir).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTime).Take(5).ToList()
                        : new List<FileInfo>();
                } catch { files = new List<FileInfo>(); }

                float rowH = 0.42f;
                float panelW = 4.9f;
                float panelH = 1.35f + (files.Count == 0 ? 1.2f : files.Count * rowH) + rowH; // + paste row

                NewRect(picker.transform, Vector3.zero, new Vector2(panelW, panelH), new Color(0.05f, 0.065f, 0.11f, 0.98f), 600);
                NewRect(picker.transform, new Vector3(0f, panelH / 2f - 0.02f, -0.01f), new Vector2(panelW, 0.04f), new Color(0.45f, 0.85f, 1f, 0.6f), 600);

                var title = NewText(picker.transform, UTSLocalization.Tr("uts.settingsshare.picker_title"), 1.5f, Color.white);
                title.transform.localPosition = new Vector3(-panelW / 2f + 0.2f, panelH / 2f - 0.32f, -0.1f);

                var closeText = NewText(picker.transform, UTSLocalization.Tr("uts.settingsshare.close_glyph"), 1.6f, new Color(1f, 0.5f, 0.5f), TextAlignmentOptions.Center);
                closeText.transform.localPosition = new Vector3(panelW / 2f - 0.25f, panelH / 2f - 0.32f, -0.1f);
                var closeBtn = NewRowButton(template, picker.transform, closeText.transform.localPosition, new Vector2(0.5f, 0.5f));
                closeBtn.OnClick.AddListener((Action)ClosePicker);

                float y = panelH / 2f - 0.85f;

                // Paste-a-string row (opens the password-gate-style input dialog).
                var pasteLabel = NewText(picker.transform, UTSLocalization.Tr("uts.settingsshare.paste_row_label"), 1.25f, new Color(1f, 0.9f, 0.6f));
                pasteLabel.transform.localPosition = new Vector3(-panelW / 2f + 0.25f, y, -0.1f);
                var pasteRow = NewRowButton(template, picker.transform, new Vector3(0f, y, -0.2f), new Vector2(panelW - 0.2f, rowH * 0.95f));
                var holderRef = holder;
                pasteRow.OnClick.AddListener((Action)(() => { ClosePicker(); BuildPasteDialog(holderRef); }));
                y -= rowH;

                if (files.Count == 0) {
                    var none = NewText(picker.transform,
                        UTSLocalization.Tr("uts.settingsshare.no_files_text"),
                        1.15f, new Color(1f, 1f, 1f, 0.7f));
                    none.transform.localPosition = new Vector3(-panelW / 2f + 0.25f, y, -0.1f);
                    return;
                }

                foreach (var f in files) {
                    var file = f;
                    var label = NewText(picker.transform, UTSLocalization.Tr("uts.settingsshare.file_row_label", DisplayName(file)), 1.25f, Color.white);
                    label.transform.localPosition = new Vector3(-panelW / 2f + 0.25f, y, -0.1f);

                    var row = NewRowButton(template, picker.transform, new Vector3(0f, y, -0.2f), new Vector2(panelW - 0.2f, rowH * 0.95f));
                    row.OnClick.AddListener((Action)(() => ImportFile(file, label)));
                    y -= rowH;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] picker build failed: {e}");
                ClosePicker();
            }
        }

        // ====================================================================
        // Paste dialog: password-gate-style string input (Ctrl+V / type / Backspace, Enter = import,
        // Escape = close). Driven by the HudManager.Update postfix below - same key-buffer approach
        // as LobbyPasswordGate, just for a visible (truncated) settings string instead of a mask.
        // ====================================================================
        private static GameObject pasteDialog;
        private static string pasteBuffer = "";
        private static TextMeshPro pasteDisplay;
        private static TextMeshPro pasteStatus;

        private static void ClosePasteDialog() {
            try { if (pasteDialog != null) UnityEngine.Object.Destroy(pasteDialog); } catch { }
            pasteDialog = null;
            pasteBuffer = "";
            pasteDisplay = null;
            pasteStatus = null;
        }

        private static void BuildPasteDialog(GameObject holder) {
            try {
                ClosePasteDialog();
                var template = GameObject.Find("PlayerOptionsMenu(Clone)")?.transform.Find("CloseButton")?.gameObject;
                if (template == null) return;

                pasteDialog = new GameObject("UTSPasteDialog");
                pasteDialog.layer = holder.layer;
                pasteDialog.transform.SetParent(holder.transform, false);
                pasteDialog.transform.localPosition = new Vector3(0f, 0f, -10f);
                CameraFit(pasteDialog, DialogDesignW);

                float w = 5.6f, h = 2.5f;
                NewRect(pasteDialog.transform, Vector3.zero, new Vector2(w, h), new Color(0.05f, 0.065f, 0.11f, 0.98f), 600);
                NewRect(pasteDialog.transform, new Vector3(0f, h / 2f - 0.02f, -0.01f), new Vector2(w, 0.04f), new Color(1f, 0.84f, 0.4f, 0.7f), 600);

                var title = NewText(pasteDialog.transform, UTSLocalization.Tr("uts.settingsshare.dialog_title"), 1.5f, Color.white);
                title.transform.localPosition = new Vector3(-w / 2f + 0.2f, h / 2f - 0.32f, -0.1f);

                var closeText = NewText(pasteDialog.transform, UTSLocalization.Tr("uts.settingsshare.close_glyph"), 1.6f, new Color(1f, 0.5f, 0.5f), TextAlignmentOptions.Center);
                closeText.transform.localPosition = new Vector3(w / 2f - 0.25f, h / 2f - 0.32f, -0.1f);
                var closeBtn = NewRowButton(template, pasteDialog.transform, closeText.transform.localPosition, new Vector2(0.5f, 0.5f));
                closeBtn.OnClick.AddListener((Action)ClosePasteDialog);

                var hint = NewText(pasteDialog.transform,
                    UTSLocalization.Tr("uts.settingsshare.dialog_hint"),
                    1.05f, new Color(1f, 1f, 1f, 0.75f));
                hint.transform.localPosition = new Vector3(-w / 2f + 0.2f, h / 2f - 0.75f, -0.1f);

                // Input box (visual) + truncated content display.
                NewRect(pasteDialog.transform, new Vector3(0f, 0f, -0.02f), new Vector2(w - 0.4f, 0.55f), new Color(0.12f, 0.14f, 0.22f, 1f), 600);
                pasteDisplay = NewText(pasteDialog.transform, UTSLocalization.Tr("uts.settingsshare.paste_empty_placeholder"), 1.15f, Color.white);
                pasteDisplay.transform.localPosition = new Vector3(-w / 2f + 0.35f, 0f, -0.1f);

                pasteStatus = NewText(pasteDialog.transform, "", 1.1f, Color.white);
                pasteStatus.transform.localPosition = new Vector3(-w / 2f + 0.2f, -0.65f, -0.1f);

                // IMPORT button row
                var importLabel = NewText(pasteDialog.transform, UTSLocalization.Tr("uts.settingsshare.import_button"), 1.4f, new Color(0.5f, 1f, 0.6f), TextAlignmentOptions.Center);
                importLabel.transform.localPosition = new Vector3(0f, -h / 2f + 0.35f, -0.1f);
                var importBtn = NewRowButton(template, pasteDialog.transform, importLabel.transform.localPosition, new Vector2(2.2f, 0.45f));
                importBtn.OnClick.AddListener((Action)ImportPasteBuffer);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] paste dialog failed: {e}");
                ClosePasteDialog();
            }
        }

        // Middle-truncated preview so the (long) settings string stays one line.
        private static void RefreshPasteDisplay() {
            if (pasteDisplay == null) return;
            if (string.IsNullOrEmpty(pasteBuffer)) {
                pasteDisplay.text = UTSLocalization.Tr("uts.settingsshare.paste_empty_placeholder");
                return;
            }
            string s = pasteBuffer;
            string shown = s.Length <= 42 ? s : s.Substring(0, 28) + "<color=#777777>...</color>" + s.Substring(s.Length - 8);
            pasteDisplay.text = $"{shown}  <color=#777777>({s.Length})</color>";
        }

        private static void ImportPasteBuffer() {
            try {
                if (string.IsNullOrWhiteSpace(pasteBuffer)) {
                    if (pasteStatus != null) { pasteStatus.text = UTSLocalization.Tr("uts.settingsshare.paste_status_empty"); pasteStatus.color = Color.yellow; }
                    return;
                }
                // Route through TOR's paste logic without permanently clobbering the user's clipboard.
                string clipBackup = GUIUtility.systemCopyBuffer;
                GUIUtility.systemCopyBuffer = pasteBuffer.Trim();
                int success = CustomOption.pasteFromClipboard();
                GUIUtility.systemCopyBuffer = clipBackup;

                if (pasteStatus != null) {
                    pasteStatus.text = success == 3 ? UTSLocalization.Tr("uts.settingsshare.paste_status_ok")
                        : success == 0 ? UTSLocalization.Tr("uts.settingsshare.paste_status_fail_format")
                        : UTSLocalization.Tr("uts.settingsshare.paste_status_partial");
                    pasteStatus.color = success == 3 ? Color.green : success == 0 ? Color.red : Color.yellow;
                }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[SettingsShare] string import result {success}.");
            } catch (Exception e) {
                if (pasteStatus != null) { pasteStatus.text = UTSLocalization.Tr("uts.settingsshare.paste_status_error"); pasteStatus.color = Color.red; }
                UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] string import failed: {e}");
            }
        }

        // Per-frame camera-fit for the open panels + key handling for the paste dialog
        // (HudManager.Update runs in the lobby too).
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class PasteDialogInputPatch {
            public static void Postfix() {
                try {
                    if (picker != null) CameraFit(picker, PickerDesignW);
                    if (pasteDialog == null) return;
                    CameraFit(pasteDialog, DialogDesignW);
                    if (Input.GetKeyDown(KeyCode.Escape)) { ClosePasteDialog(); return; }

                    bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    if ((ctrl && Input.GetKeyDown(KeyCode.V)) ||
                        (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.Insert))) {
                        string paste = GUIUtility.systemCopyBuffer ?? "";
                        var cleaned = new System.Text.StringBuilder();
                        foreach (char c in paste) if (!char.IsControl(c)) cleaned.Append(c);
                        // A settings string is one atomic token - REPLACE instead of append, so a second
                        // Ctrl+V never glues two exports together.
                        if (cleaned.Length > 0) pasteBuffer = cleaned.ToString();
                        RefreshPasteDisplay();
                        return;
                    }

                    string typed = Input.inputString;
                    if (string.IsNullOrEmpty(typed)) return;
                    bool changed = false;
                    foreach (char c in typed) {
                        if (c == '\b') {
                            if (pasteBuffer.Length > 0) { pasteBuffer = pasteBuffer.Substring(0, pasteBuffer.Length - 1); changed = true; }
                        } else if (c == '\n' || c == '\r') {
                            ImportPasteBuffer();
                            return;
                        } else if (!char.IsControl(c)) {
                            pasteBuffer += c;
                            changed = true;
                        }
                    }
                    if (changed) RefreshPasteDisplay();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] paste input failed: {e}");
                }
            }
        }

        private static void ImportFile(FileInfo file, TextMeshPro label) {
            try {
                string data = File.ReadAllText(file.FullName);
                GUIUtility.systemCopyBuffer = data;
                int success = CustomOption.pasteFromClipboard(); // TOR's paste incl. all fallbacks
                bool ok = success == 3;
                label.color = ok ? Color.green : success == 0 ? Color.red : Color.yellow;
                PostChat(ok
                    ? UTSLocalization.Tr("uts.settingsshare.file_import_ok_chat", DisplayName(file))
                    : UTSLocalization.Tr("uts.settingsshare.file_import_partial_chat", DisplayName(file)));
                UsefulTORStuffPlugin.Logger?.LogInfo($"[SettingsShare] imported {file.Name}, result {success}.");
            } catch (Exception e) {
                if (label != null) label.color = Color.red;
                UsefulTORStuffPlugin.Logger?.LogError($"[SettingsShare] import failed: {e}");
            }
        }
    }
}
