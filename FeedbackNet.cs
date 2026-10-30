// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackNet - delivery of the outbox to the author's client.
 *
 * Own callId 233 (free as a callId; 233 is only a module byte on UC's channel 230, see ID-Registry.md):
 *
 *   [233][0][id][created i32][map][x f32][y f32][shape][w f32][h f32][phase][ghost][roundSec f32][category][text][author][version]
 *        note, player -> author only (targeted at the author's client id)
 *        (since 2026-10-28 two more fields at the end: [parts][edited i32]; the text field holds the first 300 chars)
 *   [233][1][id]
 *        ack, author -> that player only; the player drops the note from its outbox
 *   [233][2][id][index][total][chunk]
 *        the rest of a long note (up to 1000 chars) in parts of 300 chars, sent one per send slot BEFORE the note
 *        itself; the author assembles them when the note arrives and acks only a complete note
 *
 * Rules that keep this harmless:
 *  - Sending happens only in the lobby (no ShipStatus), so a position never travels during a round.
 *  - Paced: one note every 0.5 s, a note without an ack is retried after 20 s. A lobby full of notes
 *    therefore never becomes a burst (the Modded EU server drops clients on bursts, see UTS 1.5.2).
 *  - The sender is the PlayerControl the message arrived on (transport), never a payload field.
 *    An ack counts only from the author; the author's client caps notes per sender and process.
 *  - Text and names are cut to fixed lengths on both ends.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace UsefulTORStuff;

internal static class FeedbackNet
{
    internal const byte CallId = 233;
    private const byte SubNote = 0, SubAck = 1, SubPart = 2;

    internal const int MaxText = FeedbackMultipart.MaxText;
    private const int Chunk = FeedbackMultipart.Chunk, MaxParts = FeedbackMultipart.MaxParts;
    private static readonly Dictionary<string, int> partProgress = new();                 // sender: next part to send
    private static readonly Dictionary<(string Key, string Id), string[]> partsIn = new();  // author: received parts
    private const int MaxName = 32, MaxMap = 32, MaxPerSender = 200;
    private const float SendGap = 0.5f, RetryAfter = 20f;

    private static readonly Dictionary<string, float> lastSent = new();
    private static readonly Dictionary<string, int> receivedFrom = new();
    private static float nextSend;
    private static int ackedThisLobby;
    private static float lastAckAt = -1f;
    private static string ownerName = "";

    internal static void Init() { }

    internal static bool InLobby()
    {
        try {
            var c = AmongUsClient.Instance;
            return c != null && c.NetworkMode != NetworkModes.FreePlay
                   && LobbyBehaviour.Instance != null && ShipStatus.Instance == null
                   && PlayerControl.LocalPlayer != null;
        } catch { return false; }
    }

    /// <summary>Called every frame (HudManager.Update postfix).</summary>
    internal static void Tick()
    {
        // one summary line once the acks have stopped coming for a moment
        if (ackedThisLobby > 0 && Time.realtimeSinceStartup - lastAckAt > 2.5f) {
            FeedbackToast.Show(FeedbackText.Get("delivered", ackedThisLobby, FeedbackText.Plain(ownerName)), FeedbackToast.Kind.Good);
            ackedThisLobby = 0;
        }

        if (FeedbackStore.Outbox.Count == 0 || !InLobby()) return;
        if (FeedbackOwner.LocalIsOwner) {
            // notes written on this machine under another account end up here; the author keeps them
            foreach (var n in new List<FeedbackNote>(FeedbackStore.Outbox)) {
                n.Received = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                n.FromKey = FeedbackOwner.KeyOf(PlayerControl.LocalPlayer);
                FeedbackStore.AddToInbox(n);
                FeedbackStore.RemoveFromOutbox(n.Id);
            }
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (now < nextSend) return;
        var owner = FeedbackOwner.Find();
        if (owner == null) return;

        foreach (var n in FeedbackStore.Outbox) {
            if (lastSent.TryGetValue(n.Id, out float t) && now - t < RetryAfter) continue;
            int parts = Parts(n.Text);
            partProgress.TryGetValue(n.Id, out int next);
            if (next < 1) next = 1;
            if (next < parts) {
                // die Teile 1..k-1 zuerst, je einer pro Sendetakt
                SendPart(owner, n, next, parts);
                partProgress[n.Id] = next + 1;
                nextSend = now + SendGap;
                return;
            }
            partProgress.Remove(n.Id);
            if (Send(owner, n)) {
                lastSent[n.Id] = now;
                ownerName = owner.Data?.PlayerName ?? "";
            }
            nextSend = now + SendGap;
            return;
        }
    }

    private static bool Send(PlayerControl owner, FeedbackNote n)
    {
        try {
            var client = AmongUsClient.Instance;
            var w = client.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, CallId, SendOption.Reliable, owner.OwnerId);
            w.Write(SubNote);
            w.Write(n.Id);
            w.Write((int)n.Created);
            w.Write(Cut(n.Map, MaxMap));
            w.Write(n.X);
            w.Write(n.Y);
            w.Write(n.Shape);
            w.Write(n.W);
            w.Write(n.H);
            w.Write(Cut(n.Phase, 16));
            w.Write(n.Ghost);
            w.Write(n.RoundSec);
            w.Write(n.Category);
            w.Write(Cut(n.Text, Chunk));
            w.Write(Cut(n.Author, MaxName));
            w.Write(Cut(n.Version, 16));
            w.Write((byte)Parts(n.Text));
            w.Write((int)n.Edited);
            client.FinishRpcImmediately(w);
            UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] sent note {n.Id} to the author (client {owner.OwnerId})");
            return true;
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogError($"[Feedback] send failed: {e.Message}");
            return false;
        }
    }

    private static int Parts(string text)
    {
        int len = Math.Min((text ?? "").Length, MaxText);
        return Math.Max(1, Math.Min(MaxParts, (len + Chunk - 1) / Chunk));
    }

    private static void SendPart(PlayerControl owner, FeedbackNote n, int index, int total)
    {
        try {
            var client = AmongUsClient.Instance;
            var w = client.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, CallId, SendOption.Reliable, owner.OwnerId);
            w.Write(SubPart);
            w.Write(n.Id);
            w.Write((byte)index);
            w.Write((byte)total);
            string text = Cut(n.Text, MaxText);
            w.Write(text.Substring(index * Chunk, Math.Min(Chunk, text.Length - index * Chunk)));
            client.FinishRpcImmediately(w);
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogError($"[Feedback] part send failed: {e.Message}");
        }
    }

    private static void ReceivePart(PlayerControl sender, MessageReader r)
    {
        string id = Cut(r.ReadString(), 16);
        int index = r.ReadByte(), total = r.ReadByte();
        string chunk = Cut(r.ReadString(), Chunk);
        if (sender == null || !FeedbackOwner.LocalIsOwner || total < 2 || total > MaxParts || index < 1 || index >= total) return;
        var key = (FeedbackOwner.KeyOf(sender), id);
        if (!partsIn.TryGetValue(key, out var arr) || arr.Length != total) partsIn[key] = arr = new string[total];
        arr[index] = chunk;
    }

    private static void SendAck(PlayerControl to, string id)
    {
        try {
            var client = AmongUsClient.Instance;
            var w = client.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, CallId, SendOption.Reliable, to.OwnerId);
            w.Write(SubAck);
            w.Write(id);
            client.FinishRpcImmediately(w);
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogError($"[Feedback] ack failed: {e.Message}");
        }
    }

    private static void Handle(PlayerControl sender, MessageReader r)
    {
        byte sub = r.ReadByte();
        if (sub == SubNote) ReceiveNote(sender, r);
        else if (sub == SubAck) ReceiveAck(sender, r);
        else if (sub == SubPart) ReceivePart(sender, r);
    }

    private static void ReceiveNote(PlayerControl sender, MessageReader r)
    {
        var n = new FeedbackNote {
            Id = Cut(r.ReadString(), 16),
            Created = r.ReadInt32(),
            Map = Cut(r.ReadString(), MaxMap),
            X = r.ReadSingle(),
            Y = r.ReadSingle(),
            Shape = r.ReadByte(),
            W = r.ReadSingle(),
            H = r.ReadSingle(),
            Phase = Cut(r.ReadString(), 16),
            Ghost = r.ReadBoolean(),
            RoundSec = r.ReadSingle(),
            Category = r.ReadByte(),
            Text = Cut(r.ReadString(), Chunk),
        };
        r.ReadString();   // the sender's own idea of its name; the transport's name below is what counts
        n.Version = Cut(r.ReadString(), 16);
        int parts = 1;
        if (r.BytesRemaining >= 5) { parts = r.ReadByte(); n.Edited = r.ReadInt32(); }   // since 2026-10-28
        if (parts < 1 || parts > MaxParts) return;
        if (parts >= 2 && sender != null) {
            var pkey = (FeedbackOwner.KeyOf(sender), n.Id);
            if (!partsIn.TryGetValue(pkey, out var arr) || arr.Length != parts) return;            // parts missing: no ack, the sender retries
            if (!FeedbackMultipart.TryJoin(n.Text, arr, parts, out var complete)) return;
            n.Text = complete;
            partsIn.Remove(pkey);
        }

        if (sender == null || !FeedbackOwner.LocalIsOwner) return;   // targeted at the author; anything else is stray
        if (string.IsNullOrEmpty(n.Id) || string.IsNullOrWhiteSpace(n.Text)) return;
        if (!Finite(n.X) || !Finite(n.Y) || !Finite(n.W) || !Finite(n.H)) return;
        if (n.Shape > 2) n.Shape = 0;
        n.W = Mathf.Clamp(n.W, 0f, 300f);
        n.H = Mathf.Clamp(n.H, 0f, 300f);

        string key = FeedbackOwner.KeyOf(sender);
        bool known = FeedbackStore.InInbox(n.Id);
        if (known && n.Edited > FeedbackStore.InboxEdited(n.Id)) {
            // an edited note: replaces the old line (same sender only, the id is the sender's)
            n.Author = Cut(sender.Data?.PlayerName ?? "", MaxName);
            n.FromKey = key;
            n.Received = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!FeedbackStore.AddToInbox(n)) return;
            UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] note {n.Id} from {n.Author} edited");
            FeedbackToast.Show(FeedbackText.Get("received.edit", FeedbackText.Plain(n.Author), FeedbackText.Plain(n.Text)), FeedbackToast.Kind.Info);
        }
        if (!known) {
            receivedFrom.TryGetValue(key, out int count);
            if (count >= MaxPerSender) {
                UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] {key} sent more than {MaxPerSender} notes this session - ignored");
                return;   // no ack either: the notes stay in that player's outbox for another day
            }
            receivedFrom[key] = count + 1;
            n.Author = Cut(sender.Data?.PlayerName ?? "", MaxName);
            n.FromKey = key;
            n.Received = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (FeedbackStore.AddToInbox(n)) {
                UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] note {n.Id} from {n.Author} ({n.Map}, {FeedbackCategories.Key(n.Category)})");
                FeedbackToast.Show(FeedbackText.Get("received", FeedbackText.Plain(n.Author), FeedbackText.MapName(n.Map),
                                                    FeedbackText.Category(n.Category), FeedbackText.Plain(n.Text)),
                                   FeedbackToast.Kind.Info);
            } else return;   // write failed: no ack, the sender retries
        }
        SendAck(sender, n.Id);
    }

    private static void ReceiveAck(PlayerControl sender, MessageReader r)
    {
        string id = r.ReadString();
        if (!FeedbackOwner.IsOwner(sender)) return;
        if (FeedbackStore.RemoveFromOutbox(id)) {
            lastSent.Remove(id);
            ackedThisLobby++;
            lastAckAt = Time.realtimeSinceStartup;
            ownerName = sender.Data?.PlayerName ?? ownerName;
            UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] note {id} delivered");
        }
    }

    private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    private static string Cut(string s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s.Substring(0, max);
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class HandleRpcPatch
    {
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != CallId) return true;
            try { Handle(__instance, reader); }
            catch (Exception e) { UsefulTORStuffPlugin.Logger.LogError($"[Feedback] receive failed: {e.Message}"); }
            return false;   // callId 233 is ours
        }
    }
}
