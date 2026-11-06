// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackReplies - telling a player that one of their notes made it into the game (2026-11-05: only when something
 * was really done, never a "no").
 *
 * Author side: BepInEx/UnknownsFeedback/done.json lists the notes that were put in. It is written outside the game
 * (by hand or by the overview tool); the game itself only flips "Delivered":
 *   [{ "Id": "<note id>", "FromKey": "puid#...", "Version": "Halloween 1.0.4", "Reply": "optional short line",
 *      "Snippet": "first words of the note", "Delivered": false }]
 * A rejected or postponed note simply has no entry. In the lobby the author's client sends each undelivered entry to
 * the player it belongs to (FeedbackNet Sub 3, paced like the notes) and marks it delivered once that player acks
 * (Sub 4). The file is re-read when it changes on disk, so a new list needs no restart.
 *
 * Player side: replies.json keeps what arrived (one per note id, newest 300). A burst of replies becomes one line
 * ("Mo put 12 of your notes into the game"); the full list sits behind the "Done" button of the feedback editor.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using UnityEngine;

namespace UsefulTORStuff;

public sealed class FeedbackReply
{
    public string Id = "";          // the note's id
    public string FromKey = "";     // author side: whom the note came from (FeedbackOwner.KeyOf)
    public string Version = "";     // where it went in, e.g. "Halloween 1.0.4"
    public string Reply = "";       // optional short line from the author
    public string Snippet = "";     // the first words of the note, so the player knows which one
    public bool Delivered;          // author side: the player acked it
    public long Received;           // player side: unix seconds
}

internal static class FeedbackReplies
{
    internal const int MaxVersion = 40, MaxReply = 160, MaxSnippet = 90;
    private const int MaxKept = 300;
    private const float SummaryAfter = 2.5f, ReloadEvery = 5f;

    private static string DonePath => Path.Combine(FeedbackStore.Dir, "done.json");
    private static string RepliesPath => Path.Combine(FeedbackStore.Dir, "replies.json");

    private static readonly List<FeedbackReply> done = new();       // author
    private static DateTime doneStamp = DateTime.MinValue;
    private static float nextReload;
    private static readonly List<FeedbackReply> replies = new();    // player

    private static int arrived;
    private static float lastArrivedAt = -1f;
    private static string authorName = "", lastSnippet = "", lastVersion = "";

    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static IReadOnlyList<FeedbackReply> Replies => replies;
    internal static bool Has(string noteId) => !string.IsNullOrEmpty(noteId) && replies.Exists(r => r.Id == noteId);

    internal static void Init()
    {
        try {
            if (!File.Exists(RepliesPath)) return;
            var list = JsonSerializer.Deserialize<List<FeedbackReply>>(File.ReadAllText(RepliesPath, Encoding.UTF8), Json);
            if (list != null) replies.AddRange(list);
        } catch (Exception e) { UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] replies.json unreadable: {e.Message}"); }
    }

    // ---- author ----

    /// <summary>Re-reads done.json when it changed on disk (at most every few seconds).</summary>
    internal static void ReloadDoneIfChanged()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextReload) return;
        nextReload = now + ReloadEvery;
        try {
            if (!File.Exists(DonePath)) { done.Clear(); doneStamp = DateTime.MinValue; return; }
            var stamp = File.GetLastWriteTimeUtc(DonePath);
            if (stamp == doneStamp) return;
            var list = JsonSerializer.Deserialize<List<FeedbackReply>>(File.ReadAllText(DonePath, Encoding.UTF8), Json);
            done.Clear();
            if (list != null) done.AddRange(list);
            doneStamp = stamp;
            int open = done.FindAll(r => !r.Delivered).Count;
            UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] done.json: {done.Count} entries, {open} still to tell");
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] done.json unreadable: {e.Message}");
            doneStamp = DateTime.MinValue;
        }
    }

    /// <summary>Undelivered entries of one player, oldest first.</summary>
    internal static IEnumerable<FeedbackReply> PendingFor(string key)
    {
        if (string.IsNullOrEmpty(key)) yield break;
        foreach (var r in done)
            if (!r.Delivered && r.FromKey == key && !string.IsNullOrEmpty(r.Id)) yield return r;
    }

    /// <summary>The player acked: the entry is not sent again. Only that player's own entry counts.</summary>
    internal static void MarkDelivered(string key, string id)
    {
        var r = done.Find(x => x.Id == id && x.FromKey == key);
        if (r == null || r.Delivered) return;
        r.Delivered = true;
        if (Write(DonePath, done)) {
            try { doneStamp = File.GetLastWriteTimeUtc(DonePath); } catch { }
        }
    }

    // ---- player ----

    /// <summary>A reply from the author arrived. False for one that is already here (lost ack, resent).</summary>
    internal static bool Store(FeedbackReply r, string author)
    {
        if (r == null || string.IsNullOrEmpty(r.Id) || Has(r.Id)) return false;
        r.FromKey = "";
        r.Delivered = false;
        r.Received = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        replies.Add(r);
        while (replies.Count > MaxKept) replies.RemoveAt(0);
        Write(RepliesPath, replies);
        arrived++;
        lastArrivedAt = Time.realtimeSinceStartup;
        authorName = author ?? "";
        lastSnippet = r.Snippet;
        lastVersion = r.Version;
        return true;
    }

    /// <summary>One line for a burst of replies, once they stop coming for a moment.</summary>
    internal static void Tick()
    {
        if (arrived == 0 || Time.realtimeSinceStartup - lastArrivedAt < SummaryAfter) return;
        string who = FeedbackText.Plain(authorName);
        string line = arrived == 1
            ? FeedbackText.Get("reply.one", who, FeedbackText.Plain(lastVersion), FeedbackText.Plain(lastSnippet))
            : FeedbackText.Get("reply.many", who, arrived);
        FeedbackToast.Show(line, FeedbackToast.Kind.Good, 8f);
        arrived = 0;
    }

    /// <summary>The list for the "Done" view, newest first, as TMP text.</summary>
    internal static string ListText()
    {
        if (replies.Count == 0) return FeedbackText.Get("done.empty");
        var sb = new StringBuilder();
        for (int i = replies.Count - 1; i >= 0; i--) {
            var r = replies[i];
            sb.Append("<color=#9BE89B>[OK]</color> <b>").Append(FeedbackText.Plain(r.Version)).Append("</b>  ")
              .Append(FeedbackText.Plain(r.Snippet));
            if (!string.IsNullOrWhiteSpace(r.Reply))
                sb.Append("\n      <color=#B9C1C5>").Append(FeedbackText.Plain(r.Reply)).Append("</color>");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool Write<T>(string path, T value)
    {
        try {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogError($"[Feedback] {Path.GetFileName(path)} save failed: {e.Message}");
            return false;
        }
    }
}
