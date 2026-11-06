// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackSkinVote - a short lobby vote on which skin wish gets built next (2026-11-05).
 *
 * Only the author starts one ("Skin vote" in the feedback editor, lobby only). The options are the lines of
 * BepInEx/UnknownsFeedback/skinvote.txt (one wish per line, up to four), or without that file the four newest notes
 * in the "skin wish" category. Every client with UTS shows a small panel for 40 s; a click sends the choice to the
 * author only and can be changed until the time is up. Then the author sends the result to everyone and appends it
 * to skinvotes.jsonl.
 *
 *   [233][5][voteId][seconds][n][option x n]     author -> everyone: start
 *   [233][6][voteId][choice]                     player -> author: the last choice of a player counts
 *   [233][7][voteId][winner][n][count x n]       author -> everyone: result (winner 255 = no votes)
 *
 * Nothing here is game information: it runs only in the lobby, and only the author's client may start or end a vote.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UsefulTORStuff;

internal static class FeedbackSkinVote
{
    internal const int MaxOptions = 4, MaxOption = 60;
    private const int Seconds = 40;

    // author
    private static string runningId;
    private static string[] runningOptions;
    private static float endsAt;
    private static readonly Dictionary<string, byte> votes = new();

    // every client
    private static string shownId;
    private static string[] shownOptions = Array.Empty<string>();
    private static float shownEndsAt;
    private static int myChoice = -1;
    private static GameObject root;
    private static readonly List<GameObject> buttons = new();
    private static TMPro.TextMeshProUGUI leftLabel;

    /// <summary>Self test only: the panel stays up in freeplay.</summary>
    internal static bool TestOutsideLobby;

    private static string OptionsPath => Path.Combine(FeedbackStore.Dir, "skinvote.txt");
    private static string ResultsPath => Path.Combine(FeedbackStore.Dir, "skinvotes.jsonl");

    // ---- author ----

    internal static void Start()
    {
        if (!FeedbackOwner.LocalIsOwner) return;
        if (!FeedbackNet.InLobby()) { FeedbackToast.Show(FeedbackText.Get("vote.lobby"), FeedbackToast.Kind.Warn, 3f); return; }
        if (runningId != null) { FeedbackToast.Show(FeedbackText.Get("vote.running"), FeedbackToast.Kind.Warn, 3f); return; }
        var options = LoadOptions();
        if (options.Count < 2) { FeedbackToast.Show(FeedbackText.Get("vote.few"), FeedbackToast.Kind.Warn, 4f); return; }
        runningId = FeedbackStore.NewId();
        runningOptions = options.ToArray();
        endsAt = Time.realtimeSinceStartup + Seconds;
        votes.Clear();
        FeedbackNet.SendVoteStart(runningId, Seconds, runningOptions);
        Show(runningId, Seconds, runningOptions);
        UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] skin vote {runningId} started: {string.Join(" | ", runningOptions)}");
    }

    private static List<string> LoadOptions()
    {
        var list = new List<string>();
        try {
            if (File.Exists(OptionsPath)) {
                foreach (var line in File.ReadAllLines(OptionsPath, Encoding.UTF8)) {
                    string s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    list.Add(Cut(s, MaxOption));
                    if (list.Count == MaxOptions) break;
                }
                return list;
            }
        } catch (Exception e) { UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] skinvote.txt unreadable: {e.Message}"); }
        var wishes = FeedbackStore.ReadInbox().FindAll(n => n.Category == FeedbackCategories.Skin);
        wishes.Sort((a, b) => b.Received.CompareTo(a.Received));
        foreach (var n in wishes) {
            string s = Cut((n.Text ?? "").Replace('\n', ' ').Trim(), MaxOption);
            if (s.Length == 0 || list.Contains(s)) continue;
            list.Add(s);
            if (list.Count == MaxOptions) break;
        }
        return list;
    }

    internal static void ReceiveChoice(string key, string voteId, byte choice)
    {
        if (runningId == null || voteId != runningId || runningOptions == null || choice >= runningOptions.Length) return;
        if (Time.realtimeSinceStartup > endsAt || string.IsNullOrEmpty(key)) return;
        votes[key] = choice;
    }

    private static void Finish()
    {
        var counts = new int[runningOptions.Length];
        foreach (var v in votes.Values) counts[v]++;
        int winner = -1;
        for (int i = 0; i < counts.Length; i++) if (counts[i] > 0 && (winner < 0 || counts[i] > counts[winner])) winner = i;
        FeedbackNet.SendVoteResult(runningId, winner < 0 ? (byte)255 : (byte)winner, counts);
        ShowResult(runningId, winner, counts);
        try {
            var sb = new StringBuilder("{\"id\":\"").Append(runningId).Append("\",\"at\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .Append(",\"options\":[");
            for (int i = 0; i < runningOptions.Length; i++)
                sb.Append(i > 0 ? "," : "").Append(System.Text.Json.JsonSerializer.Serialize(runningOptions[i]));
            sb.Append("],\"counts\":[").Append(string.Join(",", counts)).Append("],\"winner\":").Append(winner).Append("}\n");
            File.AppendAllText(ResultsPath, sb.ToString(), new UTF8Encoding(false));
        } catch (Exception e) { UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] skin vote result not saved: {e.Message}"); }
        UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] skin vote {runningId} over: {string.Join("/", counts)}, winner {winner}");
        runningId = null;
        runningOptions = null;
        votes.Clear();
    }

    // ---- every client ----

    internal static void Show(string voteId, int seconds, string[] options)
    {
        if (options == null || options.Length < 2) return;
        shownId = voteId;
        shownOptions = options;
        shownEndsAt = Time.realtimeSinceStartup + Mathf.Clamp(seconds, 5, 120);
        myChoice = -1;
        Build();
        root.SetActive(true);
        Refresh();
    }

    internal static void ShowResult(string voteId, int winner, int[] counts)
    {
        if (shownId != null && voteId != shownId) return;
        int total = 0;
        foreach (var c in counts) total += c;
        string line = winner < 0 || winner >= shownOptions.Length
            ? FeedbackText.Get("vote.none")
            : FeedbackText.Get("vote.result", FeedbackText.Plain(shownOptions[winner]), counts[winner], total);
        FeedbackToast.Show(line, FeedbackToast.Kind.Good, 8f);
        Hide();
    }

    private static void Choose(int i)
    {
        if (shownId == null || i < 0 || i >= shownOptions.Length) return;
        myChoice = i;
        if (FeedbackOwner.LocalIsOwner) ReceiveChoice(FeedbackOwner.KeyOf(PlayerControl.LocalPlayer), shownId, (byte)i);
        else FeedbackNet.SendVoteChoice(shownId, (byte)i);
        Refresh();
    }

    internal static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (runningId != null && (now >= endsAt || !FeedbackNet.InLobby())) Finish();
        if (root == null || !root.activeSelf) return;
        // the round started, or the author never sent a result (left the lobby): close on our own
        if ((!FeedbackNet.InLobby() && !TestOutsideLobby) || now > shownEndsAt + 8f) { Hide(); return; }
        Refresh();
    }

    private static void Hide()
    {
        shownId = null;
        if (root != null) root.SetActive(false);
    }

    private static void Refresh()
    {
        if (root == null) return;
        for (int i = 0; i < buttons.Count; i++) {
            bool used = i < shownOptions.Length;
            buttons[i].SetActive(used);
            if (!used) continue;
            var t = buttons[i].GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (t != null) VanillaUI.SetText(t, shownOptions[i]);
            VanillaUI.Recolor(buttons[i], i == myChoice ? VanillaUI.TealBright : VanillaUI.Grey);
        }
        int left = Mathf.Max(0, Mathf.CeilToInt(shownEndsAt - Time.realtimeSinceStartup));
        VanillaUI.SetText(leftLabel, (myChoice >= 0 ? FeedbackText.Get("vote.thanks") + "  ·  " : "") + FeedbackText.Get("vote.left", left));
    }

    private static void Build()
    {
        if (root != null) return;
        const float W = 640f, H = 400f;
        root = VanillaUI.Canvas("UnknownsFeedbackSkinVote", 30900, false);
        // below the toast line (FeedbackToast sits at the top and carries the result)
        var panel = VanillaUI.Panel(root, new Vector2(W, H), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -150f));
        float y = VanillaUI.Title(panel, FeedbackText.Get("vote.title"), 14f, 26f);
        float inset = VanillaUI.FrameW + 22f;
        VanillaUI.Text(panel, FeedbackText.Get("vote.question"), 19f, VanillaUI.TextColor, new Vector2(inset, y - 2f), new Vector2(W - 2 * inset, 28f));
        y -= 38f;
        for (int i = 0; i < MaxOptions; i++) {
            int c = i;
            buttons.Add(VanillaUI.Button(panel, "", new Vector2(0f, y), new Vector2(W - 2 * inset, 46f), VanillaUI.Grey, () => Choose(c),
                                         new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f)));
            y -= 56f;
        }
        leftLabel = VanillaUI.Text(panel, "", 15f, VanillaUI.Muted, new Vector2(inset, y - 2f), new Vector2(W - 2 * inset, 24f));
        root.SetActive(false);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max);
}
