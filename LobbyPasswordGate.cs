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
 *
 * TRUSTED PLAYERS
 *   While at least one trusted player is in the lobby (host or guest, recognised by PUID), the gate
 *   is off: no panel, no freeze, start allowed. The trusted PUIDs live in trusted_hashes.txt in the
 *   repo as SHA-256 hashes (same scheme as EasterEggCoding.Matches: UTF-8 bytes of the PUID, hex,
 *   lower case), one per line, '#' starts a comment. If the file cannot be fetched, the built-in
 *   list below applies. Only hashes ever appear in code, files and logs, never a plain PUID.
 *   The host client checks every connected player twice a second; when the trusted player leaves
 *   before the start, the gate is back. A game unlocked by password stays unlocked as before.
 */

using System;
using System.Collections;
using System.Collections.Generic;
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

        private const string TrustedFileUrl =
            "https://raw.githubusercontent.com/" +
            UsefulTORStuffUpdater.RepositoryOwner + "/" +
            UsefulTORStuffUpdater.RepositoryName +
            "/main/trusted_hashes.txt";

        // Built-in fallback when trusted_hashes.txt cannot be fetched (SHA-256 of the PUIDs, see header).
        private static readonly string[] BuiltInTrusted =
        {
            "d10f0e308524211c45d93ec3300efca28e35488ea35598196c67b1ababb7fc19",   // Mo
            "6000e7a041af292fa9c437cf6d85c0c0d813f67d354b55298fe25f5cb9c8a124",   // DrSuspect
        };

        // AppDomain keys read by ChanceMod (no cross-assembly reference needed).
        // Unlocked is true when the password was entered OR a trusted player is present, so other mods
        // need no change; Trusted tells them apart (informational).
        internal const string AppKeyActive   = "LobbyPasswordGate.Active";
        internal const string AppKeyUnlocked = "LobbyPasswordGate.Unlocked";
        internal const string AppKeyTrusted  = "LobbyPasswordGate.Trusted";

        // Letterbox for OTHER mods to hand in a password (Role Control's local web page uses it, so
        // the host can type into a real text field instead of into the frozen lobby - the gate blocks
        // the mouse and eats the keyboard, which is exactly when a second screen is handy).
        // Same AppDomain-only contract as the two keys above: no assembly reference either way.
        //   Submit  (string)  written by the caller, consumed and cleared here
        //   Result  (string)  written here: "ok" | "wrong" | "notready" | "inactive"
        // The password itself never leaves this process: the page posts to 127.0.0.1 and the value is
        // hashed here, exactly like a typed one.
        internal const string AppKeySubmit = "LobbyPasswordGate.Submit";
        internal const string AppKeyResult = "LobbyPasswordGate.SubmitResult";

        private enum FetchState { Loading, Ready, Failed }

        public static LobbyPasswordGate Instance { get; private set; }
        public static bool Unlocked { get; private set; }
        // A trusted player is in the lobby right now (host-side check, see TrustTick).
        public static bool TrustedPresent { get; private set; }
        // The gate lets the start through: password entered, or a trusted player present.
        public static bool Open => Unlocked || TrustedPresent;

        private static HashSet<string> _trustedHashes = new HashSet<string>(BuiltInTrusted);
        private static float _nextTrustCheck;
        private static string _trustedWho;

        private FetchState _fetchState = FetchState.Loading;
        private string _fetchedHash;
        private System.Threading.Tasks.Task<bool> _passwordCheck;
        private string _passwordCheckRecord;

        private GameObject _panel;
        private TextMeshProUGUI _titleLabel;
        private TextMeshProUGUI _hintLabel;
        private TextMeshProUGUI _maskedLabel;
        private TextMeshProUGUI _statusLabel;
        private TextMeshProUGUI _footerLabel;
        private string _inputBuffer = "";
        private float _errorClearTimer;
        private float _escArmedUntil;     // a second Esc before this leaves the lobby
        private float _nextFetchRetry;    // Failed state: the hash is fetched again from here on
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
            PublishState();
            UTSLocalization.LanguageApplied += OnLanguageApplied;
            this.StartCoroutine(CoFetchHash());
            this.StartCoroutine(CoFetchTrusted());
        }

        // Mirrors Unlocked/TrustedPresent into the AppDomain for ChanceMod and Role Control.
        private static void PublishState()
        {
            try
            {
                AppDomain.CurrentDomain.SetData(AppKeyUnlocked, Open);
                AppDomain.CurrentDomain.SetData(AppKeyTrusted,  TrustedPresent);
            }
            catch { }
        }

        public void OnDestroy()
        {
            UTSLocalization.LanguageApplied -= OnLanguageApplied;
        }

        // Title/footer are set once in BuildPanel and the hint/status lines are only
        // recomputed on fetch-state changes, so re-apply all of them on a language switch.
        private void OnLanguageApplied()
        {
            if (_titleLabel != null) _titleLabel.text = UTSLocalization.Tr("uts.lobbypasswordgate.title");
            if (_footerLabel != null) _footerLabel.text = UTSLocalization.Tr("uts.lobbypasswordgate.footer");
            ApplyFetchStateToPanel();
        }

        public void Update()
        {
            // A password handed in from outside (see AppKeySubmit). Checked FIRST and unconditionally:
            // if it is left lying around while the panel is closed, the next lobby would swallow it.
            PollPasswordCheck();
            ConsumeSubmittedPassword();

            // Trusted player in the lobby? (throttled, host only)
            TrustTick(false);

            // Left the lobby / returned to the menu: the GameStartManager.Update postfix that drives
            // this panel stops firing, so close it here and unfreeze. Unlocked is kept for the session.
            // LobbyScreen.Exists, never GameStartManager.Instance: the getter constructs a blank
            // instance when none exists (see LobbyScreen in LobbyLeakGuard.cs).
            if (_panel != null && _panel.activeSelf && !LobbyScreen.Exists)
            {
                HidePanel();
                return;
            }

            if (_panel == null || !_panel.activeSelf) return;

            // The overlay only blocks the mouse — freeze the bean so the host can't walk around or
            // interact with the lobby while the gate is locked.
            FreezeLocalPlayer();

            // Escape twice → leave the lobby (audit 04.10.: one press, the usual "close this" key,
            // left at once).
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (Time.unscaledTime < _escArmedUntil)
                {
                    LeaveGame();
                    return;
                }
                _escArmedUntil = Time.unscaledTime + 2f;
                ShowError(UTSLocalization.Tr("uts.lobbypasswordgate.esc_again"));
            }

            // A failed hash fetch is retried every 15 s instead of blocking the whole lobby until it
            // is left and made anew (audit 04.10.).
            if (_fetchState == FetchState.Failed)
            {
                if (_nextFetchRetry <= 0f) _nextFetchRetry = Time.unscaledTime + 15f;
                else if (Time.unscaledTime >= _nextFetchRetry)
                {
                    _nextFetchRetry = Time.unscaledTime + 15f;
                    try { this.StartCoroutine(CoFetchHash()); } catch { }
                }
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

        // ── Trusted players ───────────────────────────────────────────────────────────────────

        // Looks for a trusted player among all connected players (the host included). Only the host
        // client decides, and only inside a lobby. Runs at most twice a second unless forced (the start
        // button forces a fresh answer). Logs on change with the player NAME, never the PUID.
        private static void TrustTick(bool force)
        {
            float now = Time.unscaledTime;
            if (!force && now < _nextTrustCheck) return;
            _nextTrustCheck = now + 0.5f;

            string who = null;
            try
            {
                var ac = AmongUsClient.Instance;
                if (ac != null && ac.AmHost && LobbyScreen.Exists)
                {
                    foreach (var p in PlayerControl.AllPlayerControls.ToArray())
                    {
                        if (p == null || p.Data == null) continue;
                        if (IsTrustedPuid(p.Data.Puid)) { who = p.Data.PlayerName ?? "?"; break; }
                    }
                }
            }
            catch { who = null; }

            bool present = who != null;
            if (present == TrustedPresent) return;

            TrustedPresent = present;
            PublishState();
            if (present)
            {
                _trustedWho = who;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[LobbyPasswordGate] Trusted player '{who}' in the lobby, password gate off.");
                Instance?.HidePanel();
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogInfo($"[LobbyPasswordGate] Trusted player '{_trustedWho}' gone, password gate " +
                    (Unlocked ? "stays open (unlocked by password)." : "back on."));
                _trustedWho = null;
            }
        }

        // Same scheme as EasterEggCoding.Matches: SHA-256 of the UTF-8 bytes, hex, lower case.
        private static bool IsTrustedPuid(string puid)
        {
            if (string.IsNullOrEmpty(puid)) return false;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(puid));
            var sb = new StringBuilder(64);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return _trustedHashes.Contains(sb.ToString());
        }

        private static HashSet<string> ParseTrusted(string text)
        {
            var set = new HashSet<string>();
            foreach (string rawLine in (text ?? "").Split('\n'))
            {
                string line = rawLine;
                int c = line.IndexOf('#');
                if (c >= 0) line = line.Substring(0, c);
                line = line.Trim().TrimStart('﻿').Trim();
                if (line.Length == 64 && IsValidHex(line)) set.Add(line.ToLowerInvariant());
            }
            return set;
        }

        // Fetches trusted_hashes.txt. Any failure or a file without a single valid hash keeps the
        // current list (built-in at first), so a hiccup never changes who is trusted.
        [HideFromIl2Cpp]
        private IEnumerator CoFetchTrusted()
        {
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(TrustedFileUrl);
            www.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            www.timeout = 15;
            var op = www.SendWebRequest();

            while (!op.isDone)
                yield return new WaitForEndOfFrame();

            if (www.isNetworkError || www.isHttpError)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[LobbyPasswordGate] trusted_hashes.txt unreachable ({www.error}), keeping the current list ({_trustedHashes.Count}).");
                www.downloadHandler.Dispose();
                www.Dispose();
                yield break;
            }

            string raw = www.downloadHandler.text ?? "";
            www.downloadHandler.Dispose();
            www.Dispose();

            var set = ParseTrusted(raw);
            if (set.Count == 0)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "[LobbyPasswordGate] trusted_hashes.txt has no valid hash, keeping the current list.");
                yield break;
            }

            _trustedHashes = set;
            _nextTrustCheck = 0f;   // re-evaluate against the new list right away
            UsefulTORStuffPlugin.Logger?.LogInfo($"[LobbyPasswordGate] Trusted list loaded ({set.Count} entries).");
        }

        public void ShowPanel()
        {
            if (Open) return;       // a trusted player is here (or the password was entered)
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
            www.timeout = 15; // a hanging request would otherwise keep the state on Loading forever
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

            if (HostGuardSecurity.HostPasswordHash.IsSupported(raw))
            {
                _fetchedHash = HostGuardSecurity.HostPasswordHash.Normalize(raw);
                _fetchState = FetchState.Ready;
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Hash loaded successfully.");
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[LobbyPasswordGate] Invalid hash file (length {raw.Length}; expected a supported password verifier).");
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
            www.timeout = 15; // a hanging request would otherwise keep the state on Loading forever
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

            if (!HostGuardSecurity.HostPasswordHash.IsSupported(raw))
            {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[LobbyPasswordGate] Re-check skipped — invalid hash file (length {raw.Length}).");
                yield break; // skip
            }

            string newHash = HostGuardSecurity.HostPasswordHash.Normalize(raw);

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
                PublishState();
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

        // ── Password from another mod (Role Control's web page) ───────────────────────────────

        private void ConsumeSubmittedPassword()
        {
            string submitted;
            try { submitted = AppDomain.CurrentDomain.GetData(AppKeySubmit) as string; }
            catch { return; }
            if (submitted == null) return;

            // Take it out of the box before doing anything else, so a throw cannot make it repeat.
            try { AppDomain.CurrentDomain.SetData(AppKeySubmit, null); } catch { }

            if (Open)                           { SetSubmitResult("ok");       return; }
            if (_panel == null || !_panel.activeSelf) { SetSubmitResult("inactive"); return; }
            if (_fetchState != FetchState.Ready)     { SetSubmitResult("notready"); return; }

            _inputBuffer = submitted;
            TryUnlock();                        // writes the result itself
        }

        private void SetSubmitResult(string result)
        {
            try { AppDomain.CurrentDomain.SetData(AppKeyResult, result); } catch { }
        }

        // ── Password check ────────────────────────────────────────────────────────────────────

        private void TryUnlock()
        {
            if (_fetchState != FetchState.Ready || _fetchedHash == null) { SetSubmitResult("notready"); return; }
            if (_passwordCheck != null)
            {
                SetSubmitResult("checking");
                _inputBuffer = "";
                if (_maskedLabel != null) _maskedLabel.text = "";
                return;
            }
            _errorClearTimer = 0f;
            string submitted = _inputBuffer;
            _inputBuffer = "";
            if (_maskedLabel != null) _maskedLabel.text = "";
            _passwordCheckRecord = _fetchedHash;
            _passwordCheck = HostGuardSecurity.HostPasswordHash.VerifyAsync(submitted, _passwordCheckRecord);
            SetSubmitResult("checking");
            if (_statusLabel != null)
            {
                _statusLabel.text = "Checking password...";
                _statusLabel.color = new Color(1f, 0.85f, 0.4f);
            }
        }

        private void PollPasswordCheck()
        {
            var check = _passwordCheck;
            if (check == null || !check.IsCompleted) return;
            string record = _passwordCheckRecord;
            _passwordCheck = null;
            _passwordCheckRecord = null;
            if (check.IsFaulted) _ = check.Exception;
            // Results for a rotated verifier must never unlock the current gate.
            if (_fetchState != FetchState.Ready || !string.Equals(record, _fetchedHash, StringComparison.Ordinal))
            { SetSubmitResult("notready"); return; }
            if (check.IsFaulted)
            {
                _ = check.Exception;
                ShowError("Password check unavailable. Try again."); SetSubmitResult("notready");
                return;
            }
            if (check.IsCompletedSuccessfully && check.Result)
            {
                Unlocked = true;
                PublishState();
                SetSubmitResult("ok");
                HidePanel();
            }
            else
            {
                SetSubmitResult("wrong");
                ShowError(UTSLocalization.Tr("uts.lobbypasswordgate.wrong_password"));
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
            // The game's lobby-pane look (VanillaUI): dim backdrop that swallows the lobby's clicks,
            // framed card, upper-case title, dark input field.
            _panel = VanillaUI.Canvas("LobbyPasswordGatePanel", 8000, false);
            VanillaUI.Backdrop(_panel, null);

            var box = VanillaUI.CenterPanel(_panel, new Vector2(600, 340));
            float ty = VanillaUI.Title(box, UTSLocalization.Tr("uts.lobbypasswordgate.title"), 16f, 30f);
            _titleLabel = null;

            _hintLabel = MakeLabel(box, "Hint", new Vector2(0, ty - 8), new Vector2(-60, 30),
                "", 17, FontStyles.Normal, VanillaUI.Muted);

            // Masked input display: a dark field like the game's text boxes.
            var inputBox = VanillaUI.Rounded(box, VanillaUI.Field, VanillaUI.RadiusBox);
            inputBox.name = "InputBox";
            var inputBoxRect = inputBox.GetComponent<RectTransform>();
            inputBoxRect.anchorMin = new Vector2(0.08f, 1f);
            inputBoxRect.anchorMax = new Vector2(0.92f, 1f);
            inputBoxRect.pivot     = new Vector2(0.5f, 1f);
            inputBoxRect.anchoredPosition = new Vector2(0, ty - 50);
            inputBoxRect.sizeDelta = new Vector2(0, 50);

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
            statusRect.anchoredPosition = new Vector2(0, ty - 112);
            statusRect.sizeDelta = new Vector2(-40, 28);
            _statusLabel = statusObj.AddComponent<TextMeshProUGUI>();
            _statusLabel.text      = "";
            _statusLabel.fontSize  = 18;
            _statusLabel.alignment = TextAlignmentOptions.Center;
            _statusLabel.color     = VanillaUI.Bad;
            VanillaUI.SetText(_statusLabel, "");

            _footerLabel = MakeLabel(box, "Footer", new Vector2(0, -340 + VanillaUI.FrameW + 40), new Vector2(-40, 24),
                UTSLocalization.Tr("uts.lobbypasswordgate.footer"), 14, FontStyles.Normal, VanillaUI.Muted);

            _panel.SetActive(true);
            ApplyFetchStateToPanel();
        }

        private void ApplyFetchStateToPanel()
        {
            if (_panel == null) return;
            switch (_fetchState)
            {
                case FetchState.Loading:
                    if (_hintLabel != null) { _hintLabel.text = UTSLocalization.Tr("uts.lobbypasswordgate.hint_loading"); _hintLabel.color = new Color(0.9f, 0.9f, 0.4f); }
                    if (_maskedLabel != null) _maskedLabel.text = "";
                    if (_statusLabel != null) _statusLabel.text = "";
                    break;
                case FetchState.Failed:
                    if (_hintLabel != null) { _hintLabel.text = UTSLocalization.Tr("uts.lobbypasswordgate.hint_failed"); _hintLabel.color = new Color(1f, 0.35f, 0.35f); }
                    if (_maskedLabel != null) _maskedLabel.text = "";
                    if (_statusLabel != null) { _statusLabel.text = UTSLocalization.Tr("uts.lobbypasswordgate.status_blocked"); _statusLabel.color = new Color(1f, 0.35f, 0.35f); }
                    break;
                case FetchState.Ready:
                    if (_hintLabel != null) { _hintLabel.text = UTSLocalization.Tr("uts.lobbypasswordgate.hint_ready"); _hintLabel.color = new Color(0.82f, 0.82f, 0.82f); }
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
            tmp.fontSize  = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = color;
            VanillaUI.SetText(tmp, text);
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
                try { Instance.StartCoroutine(Instance.CoFetchTrusted()); }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyPasswordGate] Trusted list refresh not started: {ex.Message}");
                }
            }
        }

        // Show panel each lobby frame for host while locked; hide once unlocked.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        static class GameStartManagerUpdatePatch
        {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix()
            {
                if (Instance == null) return;
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

                TrustTick(false);   // before deciding, so the panel never flashes up for a trusted player

                if (!Open)
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
                TrustTick(true);    // fresh answer: the trusted player may have left a moment ago
                if (TrustedPresent) return true;

                Instance?.ShowPanel();
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Game start blocked — password required.");
                return false;
            }
        }
    }
}
