// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LobbyPasswordGate — author-controlled access gate for the game start button.
 *
 * The expected hash is fetched from password_hash.txt in the GitHub repo (HashFileUrl).
 * Only the host sees the panel. The password is never transmitted over the network.
 * Once unlocked, the gate stays open for the entire game session (until the process exits).
 *
 * HOW TO CHANGE THE PASSWORD
 *   1. Pick a password, e.g. "NewPassword99".
 *   2. Compute its SHA-256 hash (hex, lowercase, 64 chars) in PowerShell:
 *        $b = [System.Text.Encoding]::UTF8.GetBytes("NewPassword99")
 *        $h = [System.Security.Cryptography.SHA256]::Create().ComputeHash($b)
 *        ($h | ForEach-Object { $_.ToString("x2") }) -join ""
 *      Or use set_password.ps1 (reads from .env, writes password_hash.txt).
 *   3. Push password_hash.txt to GitHub — takes effect on next game launch.
 */

using System;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace UsefulTORStuff
{
    public class LobbyPasswordGate : MonoBehaviour
    {
        private const string HashFileUrl =
            "https://raw.githubusercontent.com/" +
            UsefulTORStuffUpdater.RepositoryOwner + "/" +
            UsefulTORStuffUpdater.RepositoryName +
            "/main/password_hash.txt";

        // AppDomain keys read by ChanceMod (no cross-assembly reference needed).
        internal const string AppKeyActive   = "LobbyPasswordGate.Active";
        internal const string AppKeyUnlocked = "LobbyPasswordGate.Unlocked";

        private enum FetchState { Loading, Ready, Failed }

        public static LobbyPasswordGate Instance { get; private set; }
        public static bool Unlocked { get; private set; }

        private FetchState _fetchState = FetchState.Loading;
        private string _fetchedHash;

        private GameObject _panel;
        private TextMeshProUGUI _hintLabel;
        private TextMeshProUGUI _maskedLabel;
        private TextMeshProUGUI _statusLabel;
        private string _inputBuffer = "";
        private float _errorClearTimer;
        // True only while WE are holding the player frozen. Used so we restore moveable exactly once
        // (never every frame), otherwise we fight the game's own moveable control (lobby walk-in
        // animation) and the bean gets stuck mid-walk.
        private bool _frozen;

        public LobbyPasswordGate(IntPtr ptr) : base(ptr) { }

        public void Awake()
        {
            if (Instance != null) Destroy(Instance);
            Instance = this;
            DontDestroyOnLoad(gameObject);
            AppDomain.CurrentDomain.SetData(AppKeyActive,   true);
            AppDomain.CurrentDomain.SetData(AppKeyUnlocked, false);
            this.StartCoroutine(CoFetchHash());
        }

        public void Update()
        {
            // Left the lobby / returned to the menu: the GameStartManager.Update postfix that drives
            // this panel stops firing, so close it here and unfreeze. Unlocked is kept for the session.
            if (_panel != null && _panel.activeSelf && GameStartManager.Instance == null)
            {
                HidePanel();
                return;
            }

            if (_panel == null || !_panel.activeSelf) return;

            // The overlay only blocks the mouse — freeze the bean so the host can't walk around or
            // interact with the lobby while the gate is locked.
            FreezeLocalPlayer();

            // Escape → leave lobby instead of entering password.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                LeaveGame();
                return;
            }

            if (_fetchState != FetchState.Ready) return;

            if (_errorClearTimer > 0f)
            {
                _errorClearTimer -= Time.deltaTime;
                if (_errorClearTimer <= 0f && _statusLabel != null)
                    _statusLabel.text = "";
            }

            // Ctrl+V / Shift+Insert → paste from clipboard.
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if ((ctrl && Input.GetKeyDown(KeyCode.V)) ||
                (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.Insert)))
            {
                string paste = GUIUtility.systemCopyBuffer ?? "";
                bool pasted = false;
                foreach (char c in paste)
                    if (!char.IsControl(c)) { _inputBuffer += c; pasted = true; }
                if (pasted && _maskedLabel != null)
                    _maskedLabel.text = new string('●', _inputBuffer.Length);
                return;
            }

            string typed = Input.inputString;
            if (string.IsNullOrEmpty(typed)) return;

            bool bufferChanged = false;
            foreach (char c in typed)
            {
                if (c == '\b')
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer = _inputBuffer.Substring(0, _inputBuffer.Length - 1);
                        bufferChanged = true;
                    }
                }
                else if (c == '\n' || c == '\r')
                {
                    TryUnlock();
                    return;
                }
                else if (!char.IsControl(c))
                {
                    _inputBuffer += c;
                    bufferChanged = true;
                }
            }

            if (bufferChanged && _maskedLabel != null)
                _maskedLabel.text = new string('●', _inputBuffer.Length);
        }

        public void ShowPanel()
        {
            if (_panel != null) { _panel.SetActive(true); return; }
            BuildPanel();
        }

        public void HidePanel()
        {
            if (_panel != null) _panel.SetActive(false);
            // Restore movement whenever the gate is dismissed (unlocked, left, etc.).
            UnfreezeLocalPlayer();
        }

        // The overlay can't block keyboard movement, so we pin moveable=false each frame while the
        // panel is up (the same field TOR's Hacker/Trap/etc. use to freeze a player).
        private void FreezeLocalPlayer()
        {
            try { if (PlayerControl.LocalPlayer != null) PlayerControl.LocalPlayer.moveable = false; }
            catch { }
            _frozen = true;
        }

        // Restore movement ONCE — only if we actually froze. Setting moveable=true every frame would
        // override the game's own moveable=false during the lobby walk-in animation and freeze the bean.
        private void UnfreezeLocalPlayer()
        {
            if (!_frozen) return;
            _frozen = false;
            try { if (PlayerControl.LocalPlayer != null) PlayerControl.LocalPlayer.moveable = true; }
            catch { }
        }

        private void LeaveGame()
        {
            try
            {
                HidePanel();
                AmongUsClient.Instance?.ExitGame(DisconnectReasons.ExitGame);
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"[LobbyPasswordGate] LeaveGame failed: {ex}");
            }
        }

        // ── Fetch hash from GitHub ────────────────────────────────────────────────────────────

        [HideFromIl2Cpp]
        private IEnumerator CoFetchHash()
        {
            _fetchState = FetchState.Loading;
            ApplyFetchStateToPanel();

            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(HashFileUrl);
            www.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            var op = www.SendWebRequest();

            while (!op.isDone)
                yield return new WaitForEndOfFrame();

            if (www.isNetworkError || www.isHttpError)
            {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[LobbyPasswordGate] Hash file unreachable ({www.error}). URL: {HashFileUrl}");
                www.downloadHandler.Dispose();
                www.Dispose();
                _fetchState = FetchState.Failed;
                ApplyFetchStateToPanel();
                yield break;
            }

            string raw = www.downloadHandler.text?.Trim() ?? "";
            www.downloadHandler.Dispose();
            www.Dispose();

            if (raw.Length == 64 && IsValidHex(raw))
            {
                _fetchedHash = raw.ToLowerInvariant();
                _fetchState = FetchState.Ready;
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Hash loaded successfully.");
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[LobbyPasswordGate] Invalid hash file (length {raw.Length}, expected 64 hex chars).");
                _fetchState = FetchState.Failed;
            }

            ApplyFetchStateToPanel();
        }

        // Re-check on each lobby join: if the published hash changed, require the password again.
        // ANY error (network/HTTP failure or an invalid file) skips the re-check entirely — the
        // current hash and unlock state are kept, so a transient hiccup never locks the host out.
        [HideFromIl2Cpp]
        private IEnumerator CoRecheckHash()
        {
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(HashFileUrl);
            www.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            var op = www.SendWebRequest();

            while (!op.isDone)
                yield return new WaitForEndOfFrame();

            if (www.isNetworkError || www.isHttpError)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[LobbyPasswordGate] Re-check skipped — hash file unreachable ({www.error}).");
                www.downloadHandler.Dispose();
                www.Dispose();
                yield break; // skip: keep current hash + unlock state
            }

            string raw = www.downloadHandler.text?.Trim() ?? "";
            www.downloadHandler.Dispose();
            www.Dispose();

            if (raw.Length != 64 || !IsValidHex(raw))
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[LobbyPasswordGate] Re-check skipped — invalid hash file (length {raw.Length}).");
                yield break; // skip
            }

            string newHash = raw.ToLowerInvariant();

            // If the initial load had failed, treat this success as the (now recovered) initial load.
            if (_fetchState != FetchState.Ready || _fetchedHash == null)
            {
                _fetchedHash = newHash;
                _fetchState = FetchState.Ready;
                ApplyFetchStateToPanel();
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Hash recovered on re-check.");
                yield break;
            }

            if (newHash != _fetchedHash)
            {
                _fetchedHash = newHash;
                Unlocked = false;
                AppDomain.CurrentDomain.SetData(AppKeyUnlocked, false);
                _inputBuffer = "";
                if (_maskedLabel != null) _maskedLabel.text = "";
                ApplyFetchStateToPanel();
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Password changed — re-locked, re-entry required.");
            }
            // else: unchanged → keep current state (stays unlocked if it already was).
        }

        private static bool IsValidHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        // ── Password check ────────────────────────────────────────────────────────────────────

        private void TryUnlock()
        {
            if (_fetchState != FetchState.Ready || _fetchedHash == null) return;
            try
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(_inputBuffer);
                byte[] hashBytes  = SHA256.Create().ComputeHash(inputBytes);
                string hex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                _inputBuffer = "";
                if (_maskedLabel != null) _maskedLabel.text = "";

                if (hex == _fetchedHash)
                {
                    Unlocked = true;
                    AppDomain.CurrentDomain.SetData(AppKeyUnlocked, true);
                    HidePanel();
                    UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Unlocked.");
                }
                else
                {
                    ShowError("Wrong password.");
                    UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Wrong password attempt.");
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"[LobbyPasswordGate] Hash check failed: {ex}");
                _inputBuffer = "";
            }
        }

        private void ShowError(string message)
        {
            if (_statusLabel != null)
            {
                _statusLabel.text = message;
                _statusLabel.color = new Color(1f, 0.3f, 0.3f);
            }
            _errorClearTimer = 2f;
        }

        // ── Panel UI ──────────────────────────────────────────────────────────────────────────

        private void BuildPanel()
        {
            _panel = new GameObject("LobbyPasswordGatePanel");
            DontDestroyOnLoad(_panel);

            var canvas = _panel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8000;

            var scaler = _panel.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            _panel.AddComponent<GraphicRaycaster>();

            // Full-screen overlay — blocks all mouse input from reaching the lobby behind it.
            var overlay = new GameObject("Overlay");
            overlay.transform.SetParent(_panel.transform, false);
            var overlayRect = overlay.AddComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.sizeDelta = Vector2.zero;
            overlay.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f);

            // Centered dialog box.
            var box = new GameObject("Box");
            box.transform.SetParent(_panel.transform, false);
            var boxRect = box.AddComponent<RectTransform>();
            boxRect.anchorMin = new Vector2(0.5f, 0.5f);
            boxRect.anchorMax = new Vector2(0.5f, 0.5f);
            boxRect.pivot     = new Vector2(0.5f, 0.5f);
            boxRect.sizeDelta = new Vector2(560, 310);
            box.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.14f, 0.98f);

            MakeLabel(box, "Title", new Vector2(0, -22), new Vector2(-20, 52),
                "LOBBY PASSWORD", 30, FontStyles.Bold, new Color(0.3f, 0.7f, 1f));

            _hintLabel = MakeLabel(box, "Hint", new Vector2(0, -82), new Vector2(-30, 30),
                "", 17, FontStyles.Normal, new Color(0.82f, 0.82f, 0.82f));

            // Masked input display.
            var inputBox = new GameObject("InputBox");
            inputBox.transform.SetParent(box.transform, false);
            var inputBoxRect = inputBox.AddComponent<RectTransform>();
            inputBoxRect.anchorMin = new Vector2(0.08f, 1f);
            inputBoxRect.anchorMax = new Vector2(0.92f, 1f);
            inputBoxRect.pivot     = new Vector2(0.5f, 1f);
            inputBoxRect.anchoredPosition = new Vector2(0, -122);
            inputBoxRect.sizeDelta = new Vector2(0, 48);
            inputBox.AddComponent<Image>().color = new Color(0.14f, 0.14f, 0.22f);

            var maskedObj = new GameObject("MaskedText");
            maskedObj.transform.SetParent(inputBox.transform, false);
            var maskedRect = maskedObj.AddComponent<RectTransform>();
            maskedRect.anchorMin = Vector2.zero;
            maskedRect.anchorMax = Vector2.one;
            maskedRect.offsetMin = new Vector2(10, 0);
            maskedRect.offsetMax = new Vector2(-10, 0);
            _maskedLabel = maskedObj.AddComponent<TextMeshProUGUI>();
            _maskedLabel.text      = "";
            _maskedLabel.fontSize  = 28;
            _maskedLabel.alignment = TextAlignmentOptions.Center;
            _maskedLabel.color     = Color.white;
            // Keep the dots inside the box for any password length: single line, shrink to fit, and
            // truncate with an ellipsis if even the minimum size would still overflow.
            _maskedLabel.enableWordWrapping = false;
            _maskedLabel.overflowMode  = TextOverflowModes.Ellipsis;
            _maskedLabel.enableAutoSizing = true;
            _maskedLabel.fontSizeMin   = 10;
            _maskedLabel.fontSizeMax   = 28;

            // Error / status line.
            var statusObj = new GameObject("Status");
            statusObj.transform.SetParent(box.transform, false);
            var statusRect = statusObj.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(1, 1);
            statusRect.pivot     = new Vector2(0.5f, 1);
            statusRect.anchoredPosition = new Vector2(0, -183);
            statusRect.sizeDelta = new Vector2(-20, 28);
            _statusLabel = statusObj.AddComponent<TextMeshProUGUI>();
            _statusLabel.text      = "";
            _statusLabel.fontSize  = 18;
            _statusLabel.alignment = TextAlignmentOptions.Center;
            _statusLabel.color     = new Color(1f, 0.3f, 0.3f);

            MakeLabel(box, "Footer", new Vector2(0, -240), new Vector2(-20, 24),
                "[Enter] confirm    [Backspace] delete    [Esc] leave lobby", 14, FontStyles.Normal,
                new Color(0.5f, 0.5f, 0.5f));

            _panel.SetActive(true);
            ApplyFetchStateToPanel();
        }

        private void ApplyFetchStateToPanel()
        {
            if (_panel == null) return;
            switch (_fetchState)
            {
                case FetchState.Loading:
                    if (_hintLabel != null) { _hintLabel.text = "Loading configuration..."; _hintLabel.color = new Color(0.9f, 0.9f, 0.4f); }
                    if (_maskedLabel != null) _maskedLabel.text = "";
                    if (_statusLabel != null) _statusLabel.text = "";
                    break;
                case FetchState.Failed:
                    if (_hintLabel != null) { _hintLabel.text = "Error: password_hash.txt not reachable."; _hintLabel.color = new Color(1f, 0.35f, 0.35f); }
                    if (_maskedLabel != null) _maskedLabel.text = "";
                    if (_statusLabel != null) { _statusLabel.text = "Game start permanently blocked."; _statusLabel.color = new Color(1f, 0.35f, 0.35f); }
                    break;
                case FetchState.Ready:
                    if (_hintLabel != null) { _hintLabel.text = "Enter password and confirm with Enter:"; _hintLabel.color = new Color(0.82f, 0.82f, 0.82f); }
                    if (_statusLabel != null) _statusLabel.text = "";
                    break;
            }
        }

        private static TextMeshProUGUI MakeLabel(GameObject parent, string name,
            Vector2 anchoredPos, Vector2 sizeDelta,
            string text, float fontSize, FontStyles style, Color color)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent.transform, false);
            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot     = new Vector2(0.5f, 1);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = sizeDelta;
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text      = text;
            tmp.fontSize  = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = color;
            return tmp;
        }

        // ── Harmony patches (picked up automatically by harmony.PatchAll(Assembly)) ──────────

        // On each lobby join, re-check whether the published hash changed (re-lock if so). Wrapped in
        // try/catch so any failure to even start the re-check is swallowed — the gate keeps working.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch
        {
            public static void Postfix()
            {
                if (Instance == null) return;
                try { Instance.StartCoroutine(Instance.CoRecheckHash()); }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyPasswordGate] Re-check not started: {ex.Message}");
                }
            }
        }

        // Show panel each lobby frame for host while locked; hide once unlocked.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class GameStartManagerUpdatePatch
        {
            public static void Postfix()
            {
                if (Instance == null) return;
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

                if (!Unlocked)
                    Instance.ShowPanel();
                else
                    Instance.HidePanel();
            }
        }

        // Block game start while locked.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
        static class GameStartManagerBeginGamePatch
        {
            public static bool Prefix()
            {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return true;
                if (Unlocked) return true;

                Instance?.ShowPanel();
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Game start blocked — password required.");
                return false;
            }
        }
    }
}
