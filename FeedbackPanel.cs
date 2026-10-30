// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackPanel - the note editor that the hotkey opens.
 *
 * Map, phase and the player's own position are taken the moment the hotkey is pressed (walking on
 * while typing changes nothing). "Pick a point" / "Drag an area" hand over to FeedbackPick and come
 * back with the chosen place. The panel sits at the bottom of the screen so the spot stays visible.
 *
 * Typing: characters come from Input.inputString (so umlauts, auto-repeat and Windows voice typing
 * all arrive like in any text field) and go in at the cursor. Arrow keys move the cursor (up/down by
 * the lines as shown, wrapped lines included), Home/End to the line ends, Ctrl+arrows by words,
 * Delete/Backspace on either side. Enter starts a new line; saving is the button or Ctrl+Enter
 * (2026-10-28: Enter used to save and closed the panel by accident, also when Windows voice typing
 * sent a line break). Dictation (FeedbackSpeech) starts only after a double-click on its button;
 * one click stops it (the hotkey never switches it). A note under 20 characters needs a second Save to confirm. "Earlier" loads the
 * player's own earlier notes (FeedbackStore.Mine) for editing; saving sends the edit with the same
 * id, the author's client replaces the old note. Up to 1000 characters (FeedbackNet sends parts).
 * While the panel is open:
 *  - every key is locked for everything but the panel (FeedbackInputLock, native, 2026-10-30);
 *  - TOR's (and so UC's) ability buttons ignore their hotkeys (CustomButton.onClickEvent prefix);
 *  - the game's keyboard handling (KeyboardJoystick.Update: walking, E/Q/R/Tab/Space) is skipped,
 *    so typing "e" does not use a console. TOR patches the same method (postfixes), so the detour
 *    is on a substantial, much-patched method, not on a tiny one (see the Il2Cpp dedup pitfall);
 *  - the local player is made unmovable, which also gates TOR's and UC's button hotkeys (their
 *    CouldUse checks CanMove). Restored on close unless a meeting / exile / intro took over.
 */

using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff;

internal static class FeedbackPanel
{
    internal static bool IsOpen { get; private set; }

    private static GameObject root, panel, textBox, micButton;
    private static TMPro.TextMeshProUGUI whereLabel, textLabel, counterLabel, micText, hintLabel;
    private static readonly List<GameObject> categoryButtons = new();
    private static readonly List<GameObject> placeButtons = new();
    private static readonly StringBuilder buffer = new();
    private static int caret;                 // insert position in buffer
    private static int editIndex = -1;        // -1 = new note, else index in FeedbackStore.Mine (counted from the newest)
    private static bool confirmShort;
    private const int ShortNote = 20;
    private static KeyCode heldKey = KeyCode.None;
    private static float heldNext, blinkFrom;
    private static byte category = 4;   // "other" until the player picks one; then the last pick
    private static FeedbackNote draft;
    private static bool lockedMove;
    private static int closeMenuFrames;
    private const float DictationDoubleClickWindow = 0.4f;
    private static float dictationClickAt = float.NegativeInfinity;

    // ---- per frame ----

    internal static void Tick()
    {
        if (closeMenuFrames > 0) {
            // Esc also opens the game's menu on some screens; the Esc was meant for us
            closeMenuFrames--;
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.GameMenu != null && hud.GameMenu.IsOpen) { hud.GameMenu.Close(); closeMenuFrames = 0; }
            } catch { }
        }

        if (!IsOpen) {
            if (Input.GetKeyDown(FeedbackModule.Hotkey) && CanOpen()) Open();
            return;
        }
        if (PlayerControl.LocalPlayer == null) { Close(); return; }
        if (FeedbackPick.Active) { FeedbackPick.Tick(); return; }
        if (Input.GetKeyDown(KeyCode.Escape)) { Close(); closeMenuFrames = 3; return; }
        string heard = FeedbackSpeech.Drain();
        if (!string.IsNullOrEmpty(heard)) AppendWords(heard);
        HandleTyping();
        Refresh();
    }

    internal static bool CanOpen()
    {
        try {
            if (PlayerControl.LocalPlayer == null || PlayerControl.LocalPlayer.Data == null) return false;
            var hud = HudManager.Instance;
            if (hud == null) return false;
            if (hud.Chat != null && hud.Chat.IsOpenOrOpening) return false;   // F-keys never type, but the chat owns the keyboard
            if (hud.GameMenu != null && hud.GameMenu.IsOpen) return false;
            return true;
        } catch { return false; }
    }

    private static void HandleTyping()
    {
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        caret = Mathf.Clamp(caret, 0, buffer.Length);
        if (ctrl && Input.GetKeyDown(KeyCode.V)) {
            try { Append(GUIUtility.systemCopyBuffer ?? ""); } catch { }
            return;
        }
        bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        if (ctrl && enter) { Save(); return; }

        // cursor keys with key repeat (0.4 s, then every 0.045 s)
        foreach (var k in CursorKeys) {
            if (Input.GetKeyDown(k)) { heldKey = k; heldNext = Time.realtimeSinceStartup + 0.4f; Move(k, ctrl); }
            else if (k == heldKey && Input.GetKey(k) && Time.realtimeSinceStartup >= heldNext) { heldNext = Time.realtimeSinceStartup + 0.045f; Move(k, ctrl); }
        }
        if (heldKey != KeyCode.None && !Input.GetKey(heldKey)) heldKey = KeyCode.None;
        if (ctrl && Input.GetKeyDown(KeyCode.Backspace)) { DeleteWord(); return; }

        string typed = Input.inputString;
        if (string.IsNullOrEmpty(typed)) return;
        foreach (char c in typed) {
            if (c == '\b') {
                if (caret > 0) { buffer.Remove(caret - 1, 1); caret--; Changed(); }
            } else if (c == '\n' || c == '\r') {
                if (!ctrl) Append("\n");          // Enter = new line, never save (Ctrl+Enter above)
            } else if (!char.IsControl(c)) {
                Append(c.ToString());
            }
        }
    }

    private static readonly KeyCode[] CursorKeys =
        { KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.Home, KeyCode.End, KeyCode.Delete };

    private static void Changed() { confirmShort = false; blinkFrom = Time.realtimeSinceStartup; }

    private static void Append(string s)
    {
        caret = Mathf.Clamp(caret, 0, buffer.Length);
        foreach (char c in s.Replace("\r\n", "\n")) {
            if (buffer.Length >= FeedbackNet.MaxText) break;
            if (c == '\n' || !char.IsControl(c)) { buffer.Insert(caret, c); caret++; }
        }
        Changed();
    }

    /// <summary>Dictated words at the cursor, separated from the text before it by a space.</summary>
    private static void AppendWords(string words)
    {
        caret = Mathf.Clamp(caret, 0, buffer.Length);
        if (caret > 0 && !char.IsWhiteSpace(buffer[caret - 1])) Append(" ");
        if (buffer.Length == 0 && words.Length > 0) words = char.ToUpper(words[0]) + words.Substring(1);
        Append(words);
    }

    private static void DeleteWord()
    {
        int i = caret;
        while (i > 0 && char.IsWhiteSpace(buffer[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(buffer[i - 1])) i--;
        buffer.Remove(i, caret - i);
        caret = i;
        Changed();
    }

    private static void Move(KeyCode k, bool ctrl)
    {
        int n = buffer.Length;
        switch (k) {
            case KeyCode.LeftArrow:
                if (ctrl) { while (caret > 0 && char.IsWhiteSpace(buffer[caret - 1])) caret--; while (caret > 0 && !char.IsWhiteSpace(buffer[caret - 1])) caret--; }
                else if (caret > 0) caret--;
                break;
            case KeyCode.RightArrow:
                if (ctrl) { while (caret < n && !char.IsWhiteSpace(buffer[caret])) caret++; while (caret < n && char.IsWhiteSpace(buffer[caret])) caret++; }
                else if (caret < n) caret++;
                break;
            case KeyCode.Home:
                caret = ctrl ? 0 : LineEdge(-1);
                break;
            case KeyCode.End:
                caret = ctrl ? n : LineEdge(1);
                break;
            case KeyCode.UpArrow: caret = LineStep(-1); break;
            case KeyCode.DownArrow: caret = LineStep(1); break;
            case KeyCode.Delete:
                if (caret < n) { buffer.Remove(caret, 1); Changed(); }
                break;
        }
        blinkFrom = Time.realtimeSinceStartup;
        Refresh();
    }

    // The text label shows buffer[0..caret] + cursor glyph + buffer[caret..]: in TMP's character list the cursor is
    // character number `caret`, every buffer character after it sits one further on.

    /// <summary>Start (dir -1) or end (dir 1) of the line the cursor is shown on.</summary>
    private static int LineEdge(int dir)
    {
        try {
            textLabel.ForceMeshUpdate();
            var ti = textLabel.textInfo;
            if (caret >= ti.characterCount) return caret;
            int line = ti.characterInfo[caret].lineNumber;
            var li = ti.lineInfo[line];
            int r = dir < 0 ? li.firstCharacterIndex : li.lastCharacterIndex;
            int b = r > caret ? r - 1 : r;
            if (dir > 0 && b < buffer.Length && buffer[b] == '\n') return b;      // before the line break
            if (dir > 0 && b < buffer.Length && r != caret) b++;                  // behind the last character
            return Mathf.Clamp(b, 0, buffer.Length);
        } catch { return caret; }
    }

    /// <summary>Cursor one shown line up or down, keeping its horizontal position as well as the lines allow.</summary>
    private static int LineStep(int dir)
    {
        try {
            textLabel.ForceMeshUpdate();
            var ti = textLabel.textInfo;
            if (caret >= ti.characterCount) return caret;
            var ci = ti.characterInfo[caret];
            int target = ci.lineNumber + dir;
            if (target < 0) return 0;
            if (target >= ti.lineCount) return buffer.Length;
            float x = ci.origin;
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < ti.characterCount; i++) {
                if (ti.characterInfo[i].lineNumber != target || i == caret) continue;
                float d = Mathf.Abs(ti.characterInfo[i].origin - x);
                if (d < bd) { bd = d; best = i; }
            }
            if (best < 0) return caret;
            return best > caret ? best - 1 : best;
        } catch { return caret; }
    }

    private static void ToggleDictation()
    {
        if (FeedbackSpeech.Current == FeedbackSpeech.State.Listening) {
            dictationClickAt = float.NegativeInfinity;
            FeedbackSpeech.Stop();
            return;
        }
        float now = Time.realtimeSinceStartup;
        float sinceClick = now - dictationClickAt;
        if (sinceClick < 0f || sinceClick > DictationDoubleClickWindow) {
            dictationClickAt = now;
            return;
        }
        dictationClickAt = float.NegativeInfinity;
        if (!FeedbackSpeech.Available) {
            FeedbackToast.Show(FeedbackText.Get("mic.unavailable"), FeedbackToast.Kind.Warn, 5f);
            return;
        }
        if (!FeedbackSpeech.Start())
            FeedbackToast.Show(FeedbackText.Get("mic.failed", FeedbackSpeech.Problem), FeedbackToast.Kind.Warn, 5f);
    }

    // ---- open / close / save ----

    internal static void Open()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp == null) return;
        Vector3 pos = lp.transform.position;
        draft = new FeedbackNote {
            Id = FeedbackStore.NewId(),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Map = FeedbackContext.MapKey(),
            X = pos.x,
            Y = pos.y,
            Shape = FeedbackPick.ShapeHere,
            Phase = FeedbackContext.Phase(),
            Ghost = FeedbackContext.Ghost(),
            RoundSec = FeedbackContext.RoundSeconds(),
            Author = lp.Data?.PlayerName ?? "",
            Version = UsefulTORStuffPlugin.PluginVersion,
        };
        ownX = pos.x; ownY = pos.y;
        buffer.Clear();
        caret = 0;
        editIndex = -1;
        confirmShort = false;
        dictationClickAt = float.NegativeInfinity;
        Build();
        root.SetActive(true);
        IsOpen = true;
        LockMovement(lp);
        FeedbackInputLock.Engage();
        FeedbackButton.HideNow();
        Refresh();
        UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] panel open ({draft.Map} {F(draft.X)}|{F(draft.Y)}, {draft.Phase})");
    }

    private static float ownX, ownY;

    internal static void Close()
    {
        dictationClickAt = float.NegativeInfinity;
        FeedbackSpeech.Stop();
        FeedbackPick.End();
        IsOpen = false;
        draft = null;
        if (root != null) root.SetActive(false);
        UnlockMovement();
        FeedbackInputLock.Release();
    }

    /// <summary>Stores the draft. Also used by the self test.</summary>
    internal static void Save()
    {
        if (draft == null) return;
        dictationClickAt = float.NegativeInfinity;
        FeedbackSpeech.Stop();
        string late = FeedbackSpeech.Drain();
        if (!string.IsNullOrEmpty(late)) AppendWords(late);
        string text = buffer.ToString().Trim();
        if (text.Length == 0) { FeedbackToast.Show(FeedbackText.Get("empty"), FeedbackToast.Kind.Warn, 2f); return; }
        if (text.Length < ShortNote && !confirmShort) {
            confirmShort = true;
            FeedbackToast.Show(FeedbackText.Get("short", ShortNote), FeedbackToast.Kind.Warn, 3f);
            return;
        }
        confirmShort = false;
        var n = draft;
        n.Text = text;
        n.Category = category;
        if (editIndex >= 0) n.Edited = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        FeedbackStore.Remember(n);
        if (FeedbackOwner.LocalIsOwner) {
            n.Received = n.Created;
            n.FromKey = FeedbackOwner.KeyOf(PlayerControl.LocalPlayer);
            FeedbackStore.AddToInbox(n);
            FeedbackToast.Show(FeedbackText.Get("saved.owner", FeedbackStore.InboxCount), FeedbackToast.Kind.Good, 2.5f);
        } else {
            FeedbackStore.AddToOutbox(n);
            bool lobby = n.Phase == "lobby";
            FeedbackToast.Show(lobby ? FeedbackText.Get("saved.lobby") : FeedbackText.Get("saved.round", FeedbackStore.Outbox.Count),
                               FeedbackToast.Kind.Good, 3f);
        }
        UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] note {n.Id} saved ({FeedbackCategories.Key(n.Category)}, shape {n.Shape}, " +
                                      $"{n.Text.Length} chars, {(FeedbackOwner.LocalIsOwner ? "inbox" : "outbox")})");
        Close();
    }

    private static void StartPick(bool area)
    {
        if (draft == null) return;
        dictationClickAt = float.NegativeInfinity;
        FeedbackSpeech.Stop();
        root.SetActive(false);
        FeedbackPick.Begin(area, (shape, centre, size) => {
            if (draft != null) {
                draft.Shape = shape;
                draft.X = centre.x; draft.Y = centre.y;
                draft.W = size.x; draft.H = size.y;
                UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] picked shape {shape} at {F(centre.x)}|{F(centre.y)}, size {F(size.x)} x {F(size.y)}");
            }
            if (IsOpen) root.SetActive(true);
        }, () => { if (IsOpen) root.SetActive(true); });
    }

    /// <summary>"Earlier": the next older own note into the editor (same id, place and category); past the oldest back to a new note.</summary>
    private static void EditEarlier()
    {
        var mine = FeedbackStore.Mine;
        if (mine.Count == 0) { FeedbackToast.Show(FeedbackText.Get("earlier.none"), FeedbackToast.Kind.Warn, 2f); return; }
        FeedbackSpeech.Stop();
        editIndex++;
        if (editIndex >= mine.Count) { NewNote(); return; }
        var old = FeedbackStore.Copy(mine[mine.Count - 1 - editIndex]);
        draft = old;
        category = old.Category;
        buffer.Clear(); buffer.Append(old.Text ?? "");
        caret = buffer.Length;
        confirmShort = false;
        Refresh();
    }

    private static void NewNote()
    {
        if (PlayerControl.LocalPlayer == null) return;
        Close();
        Open();
    }

    private static void UseOwnPosition()
    {
        if (draft == null) return;
        draft.Shape = FeedbackPick.ShapeHere;
        draft.X = ownX; draft.Y = ownY;
        draft.W = draft.H = 0f;
    }

    // self test hooks
    internal static void TestType(string s) { buffer.Clear(); Append(s); Refresh(); }
    internal static void TestCategory(byte c) { category = c; Refresh(); }
    internal static void TestPick(bool area) => StartPick(area);
    internal static string TestBuffer => buffer.ToString();
    internal static FeedbackNote Draft => draft;
    internal static string LastDraftId => draft?.Id;

    private static void LockMovement(PlayerControl lp)
    {
        try {
            if (lp.moveable) { lp.moveable = false; lockedMove = true; }
            if (lp.MyPhysics != null && lp.MyPhysics.body != null) lp.MyPhysics.body.velocity = Vector2.zero;
        } catch { }
    }

    private static void UnlockMovement()
    {
        if (!lockedMove) return;
        lockedMove = false;
        try {
            var lp = PlayerControl.LocalPlayer;
            // a meeting, exile or intro that started meanwhile hands movement back on its own
            if (lp != null && !lp.moveable && MeetingHud.Instance == null && ExileController.Instance == null && IntroCutscene.Instance == null)
                lp.moveable = true;
        } catch { }
    }

    private static string F(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // ---- view ----

    private static void Refresh()
    {
        if (root == null || draft == null) return;
        string phase = FeedbackText.Get("phase." + draft.Phase) + (draft.Ghost ? " " + FeedbackText.Get("phase.ghost") : "");
        VanillaUI.SetText(whereLabel, FeedbackText.Get("where." + draft.Shape, FeedbackText.MapName(draft.Map), phase,
                                                       draft.X, draft.Y, draft.W, draft.H));

        bool listening = FeedbackSpeech.Current == FeedbackSpeech.State.Listening;
        bool blink = (int)((Time.realtimeSinceStartup - blinkFrom) * 2f) % 2 == 0;
        string cursor = blink ? "<color=#5ee0d2>|</color>" : "<color=#00000000>|</color>";
        caret = Mathf.Clamp(caret, 0, buffer.Length);
        string heard = listening && FeedbackSpeech.Hypothesis.Length > 0
            ? " <color=#8a95a5>" + FeedbackText.Plain(FeedbackSpeech.Hypothesis) + "</color>" : "";
        if (buffer.Length == 0 && heard.Length == 0)
            textLabel.text = cursor + "<color=#8a95a5>" + FeedbackText.Get("placeholder") + "</color>";
        else
        {
            string all = buffer.ToString();
            textLabel.text = FeedbackText.Plain(all.Substring(0, caret)) + cursor + FeedbackText.Plain(all.Substring(caret)) + heard;
            // long notes: show the page with the cursor on it
            try {
                textLabel.ForceMeshUpdate();
                var ti = textLabel.textInfo;
                if (caret < ti.characterCount) textLabel.pageToDisplay = ti.characterInfo[caret].pageNumber + 1;
            } catch { }
        }
        counterLabel.text = (editIndex >= 0 ? FeedbackText.Get("editing", editIndex + 1, FeedbackStore.Mine.Count) + "   " : "")
                            + $"{buffer.Length}/{FeedbackNet.MaxText}";

        for (int i = 0; i < categoryButtons.Count; i++)
            VanillaUI.Recolor(categoryButtons[i], i == category ? VanillaUI.TealBright : VanillaUI.Grey);
        for (int i = 0; i < placeButtons.Count; i++)
            VanillaUI.Recolor(placeButtons[i], i == draft.Shape ? VanillaUI.Amber : VanillaUI.Grey);

        bool micAvailable = FeedbackSpeech.Current != FeedbackSpeech.State.Unavailable;
        VanillaUI.Recolor(micButton, listening ? (blink ? VanillaUI.Red : new Color(0.62f, 0.18f, 0.22f)) : micAvailable ? VanillaUI.Blue : VanillaUI.Grey);
        bool confirmDictation = Time.realtimeSinceStartup - dictationClickAt <= DictationDoubleClickWindow;
        VanillaUI.SetText(micText, FeedbackText.Get(listening ? "mic.on" : micAvailable ? (confirmDictation ? "mic.confirm" : "mic") : "mic.none"));
    }

    private static void Build()
    {
        if (root != null) return;
        const float W = 1080f, H = 486f;
        root = VanillaUI.Canvas("UnknownsFeedbackPanel", 31000, true);
        panel = VanillaUI.Panel(root, new Vector2(W, H), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 24f));
        float y = VanillaUI.Title(panel, FeedbackText.Get("title"), 14f, 28f);

        float inset = VanillaUI.FrameW + 26f;
        whereLabel = VanillaUI.Text(panel, "", 19f, VanillaUI.Muted, new Vector2(inset, y - 4f), new Vector2(W - 2 * inset, 28f));
        y -= 40f;

        // categories
        float bw = 176f, gap = 14f, x = inset;
        for (byte i = 0; i < FeedbackCategories.Keys.Length; i++) {
            byte c = i;
            categoryButtons.Add(VanillaUI.Button(panel, FeedbackText.Category(c), new Vector2(x, y), new Vector2(bw, 46f), VanillaUI.Grey,
                                                 () => { category = c; Refresh(); },
                                                 new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1)));
            x += bw + gap;
        }
        y -= 46f + 14f;

        // place: own position, point, area
        VanillaUI.Text(panel, FeedbackText.Get("loc"), 20f, VanillaUI.Muted, new Vector2(inset, y - 10f), new Vector2(70f, 30f));
        x = inset + 76f;
        string[] keys = { "loc.here", "loc.point", "loc.area" };
        Action[] acts = { () => { UseOwnPosition(); Refresh(); }, () => StartPick(false), () => StartPick(true) };
        float[] widths = { 150f, 230f, 240f };
        for (int i = 0; i < keys.Length; i++) {
            placeButtons.Add(VanillaUI.Button(panel, FeedbackText.Get(keys[i]), new Vector2(x, y), new Vector2(widths[i], 44f), VanillaUI.Grey,
                                              acts[i], new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1)));
            x += widths[i] + gap;
        }
        // own earlier notes for editing (2026-10-28)
        VanillaUI.Button(panel, FeedbackText.Get("earlier"), new Vector2(-inset - 112f, y), new Vector2(150f, 44f), VanillaUI.Blue,
                         EditEarlier, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
        VanillaUI.Button(panel, FeedbackText.Get("new"), new Vector2(-inset, y), new Vector2(100f, 44f), VanillaUI.Grey,
                         NewNote, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
        y -= 44f + 14f;

        // text field
        float boxH = 140f;
        textBox = VanillaUI.Box(panel, new Vector2(inset, y), new Vector2(W - 2 * inset, boxH), VanillaUI.Field);
        textLabel = VanillaUI.Label(textBox, "", 24f, VanillaUI.TextColor, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                                    Vector2.zero, new Vector2(-32f, -24f), TMPro.TextAlignmentOptions.TopLeft);
        textLabel.richText = true;
        textLabel.enableWordWrapping = true;
        textLabel.overflowMode = TMPro.TextOverflowModes.Page;
        counterLabel = VanillaUI.Label(textBox, "", 15f, VanillaUI.Muted, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0),
                                       new Vector2(-12f, 6f), new Vector2(120f, 22f), TMPro.TextAlignmentOptions.BottomRight);

        // bottom row: dictation, hint, save, cancel
        float by = VanillaUI.FrameW + 18f;
        micButton = VanillaUI.Button(panel, FeedbackText.Get("mic"), new Vector2(inset, by), new Vector2(180f, 50f), VanillaUI.Blue,
                                     ToggleDictation, out micText, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
        hintLabel = VanillaUI.Label(panel, FeedbackText.Get("hint", FeedbackModule.Hotkey), 14f, VanillaUI.Muted,
                                    new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(inset + 196f, by - 2f),
                                    new Vector2(W - 2 * inset - 196f - 390f, 54f), TMPro.TextAlignmentOptions.MidlineLeft);
        VanillaUI.Button(panel, FeedbackText.Get("save"), new Vector2(-inset - 186f, by), new Vector2(176f, 50f),
                         VanillaUI.Green, Save, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
        VanillaUI.Button(panel, FeedbackText.Get("cancel"), new Vector2(-inset, by), new Vector2(170f, 50f),
                         VanillaUI.Red, Close, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
        root.SetActive(false);
    }

    // ---- hooks ----

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    private static class HudTick
    {
        public static void Postfix()
        {
            if (!FeedbackModule.Active) return;
            // the panel's own key reads see the real keys; everything else is locked while it is open
            using var _ = FeedbackInputLock.Own();
            try {
                FeedbackContext.Tick();
                Tick();
                if (!IsOpen && FeedbackInputLock.Engaged) FeedbackInputLock.Release();   // closed by something else
                FeedbackNet.Tick();
                FeedbackToast.Tick();
                FeedbackButton.Tick();
                FeedbackSelfTest.Tick();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger.LogError($"[Feedback] tick failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    private static class KeyboardBlock
    {
        public static bool Prefix() => !IsOpen;
    }

    /// <summary>TOR's and UC's ability buttons fire on their hotkey through onClickEvent; nothing fires while typing.</summary>
    [HarmonyPatch(typeof(TheOtherRoles.Objects.CustomButton), "onClickEvent")]
    private static class AbilityBlock
    {
        public static bool Prefix() => !IsOpen;
    }
}
