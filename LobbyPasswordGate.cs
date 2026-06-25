// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LobbyPasswordGate — autor-kontrollierte Zugangssperre für den Spielstart-Button.
 *
 * Der erwartete Passwort-Hash wird aus einer Datei im GitHub-Repo geladen (HashFileUrl).
 * So lässt er sich ohne Neu-Kompilieren ändern: einfach password_hash.txt im Repo anpassen.
 * Nur der Host sieht das Eingabe-Panel. Das Passwort wird niemals per RPC übertragen.
 *
 * BEDROHUNGSMODELL: Nur ein Casual-Deterrent. Der Check ist client-seitig und der Code ist
 * Open Source — er lässt sich per Harmony-Patch umgehen. Das ist bewusst so akzeptiert.
 *
 * ─── WIE DU DEN HASH ÄNDERST ─────────────────────────────────────────────────────────────
 *  1. Wähle ein Passwort, z. B. "NeuesPasswort99".
 *  2. Berechne den SHA-256-Hash (hex, lowercase, 64 Zeichen) in PowerShell:
 *
 *       $b = [System.Text.Encoding]::UTF8.GetBytes("NeuesPasswort99")
 *       $h = [System.Security.Cryptography.SHA256]::Create().ComputeHash($b)
 *       ($h | ForEach-Object { $_.ToString("x2") }) -join ""
 *
 *     Oder online: https://emn178.github.io/online-tools/sha256.html  (Input type: Text)
 *  3. Den 64-stelligen Hex-String in die Datei password_hash.txt im Repo-Root eintragen
 *     (nur der Hash, kein Zeilenumbruch nötig) und auf GitHub pushen. Fertig.
 *
 * ─── URL ANPASSEN ────────────────────────────────────────────────────────────────────────
 *  Die Konstante HashFileUrl unten zeigt auf password_hash.txt im GitHub-Repo. Falls du
 *  den Dateinamen oder Branch änderst, passe die URL einmalig an und kompiliere neu.
 * ─────────────────────────────────────────────────────────────────────────────────────────
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
        // URL zur Roh-Textdatei im GitHub-Repo, die nur den 64-stelligen SHA-256-Hash enthält.
        // Einmalig anpassen falls Branch/Dateiname sich ändert; der Hash selbst wird online gesetzt.
        private const string HashFileUrl =
            "https://raw.githubusercontent.com/" +
            UsefulTORStuffUpdater.RepositoryOwner + "/" +
            UsefulTORStuffUpdater.RepositoryName +
            "/main/password_hash.txt";

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

        public LobbyPasswordGate(IntPtr ptr) : base(ptr) { }

        // AppDomain-Schlüssel, über die ChanceMod (ohne Compile-Zeit-Referenz) erkennt,
        // dass dieses Gate geladen ist und ob es bereits entsperrt wurde.
        internal const string AppKeyActive   = "LobbyPasswordGate.Active";
        internal const string AppKeyUnlocked = "LobbyPasswordGate.Unlocked";

        public void Awake()
        {
            if (Instance != null) Destroy(Instance);
            Instance = this;
            DontDestroyOnLoad(gameObject);
            AppDomain.CurrentDomain.SetData(AppKeyActive, true);
            AppDomain.CurrentDomain.SetData(AppKeyUnlocked, false);
            this.StartCoroutine(CoFetchHash());
        }

        public void Update()
        {
            if (_panel == null || !_panel.activeSelf) return;
            if (_fetchState != FetchState.Ready) return;

            if (_errorClearTimer > 0f)
            {
                _errorClearTimer -= Time.deltaTime;
                if (_errorClearTimer <= 0f && _statusLabel != null)
                    _statusLabel.text = "";
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
            if (_panel != null)
            {
                _panel.SetActive(true);
                return;
            }
            BuildPanel();
        }

        public void HidePanel()
        {
            if (_panel != null)
                _panel.SetActive(false);
        }

        public static void ResetLock()
        {
            Unlocked = false;
            AppDomain.CurrentDomain.SetData(AppKeyUnlocked, false);
            if (Instance != null)
            {
                Instance._inputBuffer = "";
                Instance.HidePanel();
            }
        }

        // ── Hash aus GitHub laden ─────────────────────────────────────────────────────────────

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
                    $"[LobbyPasswordGate] Hash-Datei nicht erreichbar ({www.error}). " +
                    $"URL: {HashFileUrl}");
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
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Hash erfolgreich geladen.");
            }
            else
            {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[LobbyPasswordGate] Ungültige Hash-Datei (Länge {raw.Length}, " +
                    $"erwartet 64 Hex-Zeichen). Inhalt: '{raw}'");
                _fetchState = FetchState.Failed;
            }

            ApplyFetchStateToPanel();
        }

        private static bool IsValidHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        // ── Passwort prüfen ───────────────────────────────────────────────────────────────────

        private void TryUnlock()
        {
            if (_fetchState != FetchState.Ready || _fetchedHash == null) return;

            try
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(_inputBuffer);
                byte[] hashBytes = SHA256.Create().ComputeHash(inputBytes);
                string hex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                _inputBuffer = "";
                if (_maskedLabel != null) _maskedLabel.text = "";

                if (hex == _fetchedHash)
                {
                    Unlocked = true;
                    AppDomain.CurrentDomain.SetData(AppKeyUnlocked, true);
                    HidePanel();
                    UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Entsperrt.");
                }
                else
                {
                    ShowError("Falsches Passwort.");
                    UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Falscher Passwort-Versuch.");
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"[LobbyPasswordGate] Hash-Check fehlgeschlagen: {ex}");
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

        // ── Panel-UI ──────────────────────────────────────────────────────────────────────────

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

            // Halbtransparentes Overlay (kein Button → Lobby-Klicks landen trotzdem)
            var overlay = new GameObject("Overlay");
            overlay.transform.SetParent(_panel.transform, false);
            var overlayRect = overlay.AddComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.sizeDelta = Vector2.zero;
            overlay.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f);

            // Zentrierte Box
            var box = new GameObject("Box");
            box.transform.SetParent(_panel.transform, false);
            var boxRect = box.AddComponent<RectTransform>();
            boxRect.anchorMin = new Vector2(0.5f, 0.5f);
            boxRect.anchorMax = new Vector2(0.5f, 0.5f);
            boxRect.pivot = new Vector2(0.5f, 0.5f);
            boxRect.sizeDelta = new Vector2(520, 290);
            box.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.14f, 0.98f);

            // Titel
            MakeLabel(box, "Title", new Vector2(0, -22), new Vector2(-20, 52),
                "LOBBY PASSWORT", 30, FontStyles.Bold, new Color(0.3f, 0.7f, 1f));

            // Hinweiszeile (wird je nach FetchState aktualisiert)
            _hintLabel = MakeLabel(box, "Hint", new Vector2(0, -82), new Vector2(-30, 30),
                "", 17, FontStyles.Normal, new Color(0.82f, 0.82f, 0.82f));

            // Eingabe-Anzeigefeld
            var inputBox = new GameObject("InputBox");
            inputBox.transform.SetParent(box.transform, false);
            var inputBoxRect = inputBox.AddComponent<RectTransform>();
            inputBoxRect.anchorMin = new Vector2(0.08f, 1f);
            inputBoxRect.anchorMax = new Vector2(0.92f, 1f);
            inputBoxRect.pivot = new Vector2(0.5f, 1f);
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
            _maskedLabel.text = "";
            _maskedLabel.fontSize = 28;
            _maskedLabel.alignment = TextAlignmentOptions.Center;
            _maskedLabel.color = Color.white;

            // Status- / Fehlermeldung
            var statusObj = new GameObject("Status");
            statusObj.transform.SetParent(box.transform, false);
            var statusRect = statusObj.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(1, 1);
            statusRect.pivot = new Vector2(0.5f, 1);
            statusRect.anchoredPosition = new Vector2(0, -183);
            statusRect.sizeDelta = new Vector2(-20, 28);
            _statusLabel = statusObj.AddComponent<TextMeshProUGUI>();
            _statusLabel.text = "";
            _statusLabel.fontSize = 18;
            _statusLabel.alignment = TextAlignmentOptions.Center;
            _statusLabel.color = new Color(1f, 0.3f, 0.3f);

            // Fußzeile
            MakeLabel(box, "Footer", new Vector2(0, -222), new Vector2(-20, 24),
                "[Enter] bestätigen    [Backspace] löschen", 14, FontStyles.Normal,
                new Color(0.5f, 0.5f, 0.5f));

            _panel.SetActive(true);
            ApplyFetchStateToPanel();
        }

        // Zeigt den aktuellen Lade-/Fehler-/Bereit-Zustand im Panel an.
        private void ApplyFetchStateToPanel()
        {
            if (_panel == null) return;
            switch (_fetchState)
            {
                case FetchState.Loading:
                    if (_hintLabel != null)
                    {
                        _hintLabel.text = "Lade Konfiguration...";
                        _hintLabel.color = new Color(0.9f, 0.9f, 0.4f);
                    }
                    if (_maskedLabel != null) _maskedLabel.text = "";
                    if (_statusLabel != null) _statusLabel.text = "";
                    break;

                case FetchState.Failed:
                    if (_hintLabel != null)
                    {
                        _hintLabel.text = "Fehler: password_hash.txt nicht erreichbar.";
                        _hintLabel.color = new Color(1f, 0.35f, 0.35f);
                    }
                    if (_maskedLabel != null) _maskedLabel.text = "";
                    if (_statusLabel != null)
                    {
                        _statusLabel.text = "Spielstart dauerhaft blockiert.";
                        _statusLabel.color = new Color(1f, 0.35f, 0.35f);
                    }
                    break;

                case FetchState.Ready:
                    if (_hintLabel != null)
                    {
                        _hintLabel.text = "Passwort eingeben und mit Enter bestätigen:";
                        _hintLabel.color = new Color(0.82f, 0.82f, 0.82f);
                    }
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
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = sizeDelta;
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;
            return tmp;
        }

        // ── Harmony-Patches (werden via harmony.PatchAll(Assembly) automatisch erfasst) ──────

        // Bei jedem Lobby-Beitritt zurücksetzen.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch
        {
            public static void Postfix()
            {
                Unlocked = false;
                AppDomain.CurrentDomain.SetData(AppKeyUnlocked, false);
                if (Instance != null)
                {
                    Instance._inputBuffer = "";
                    Instance.HidePanel();
                }
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Lobby beigetreten — Sperre zurückgesetzt.");
            }
        }

        // Jeden Lobby-Frame: Panel für den Host zeigen (gesperrt) oder verstecken (entsperrt).
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

        // Spielstart blockieren, solange das Passwort nicht korrekt eingegeben wurde.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
        static class GameStartManagerBeginGamePatch
        {
            public static bool Prefix()
            {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return true;
                if (Unlocked) return true;

                Instance?.ShowPanel();
                UsefulTORStuffPlugin.Logger?.LogInfo("[LobbyPasswordGate] Spielstart blockiert — Passwort erforderlich.");
                return false;
            }
        }
    }
}
