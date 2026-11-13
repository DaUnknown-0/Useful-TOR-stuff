// Copyright (C) 2026 DaUnknown-0. Licensed under GPL-3.0-or-later.
//
// SHARED NETWORK CHECKS.
//
// Two classes of bug kept coming back in every mod separately, because every mod carried its own copy of the
// code: a receiver that believed what a message said about its sender, and an updater that wrote whatever a
// download returned. This file is the one place for both. No cross-assembly references: it is duplicated
// verbatim per mod (only the namespace differs), exactly like UnknownsCollective.cs. Fix a rule here and copy
// the file to the other mods.

using UnityEngine;

namespace UsefulTORStuff {
    internal static class UnknownsNet {
        // ---------------------------------------------------------------- downloads
        /// <summary>Deadline in seconds for a feed or API request. Without one a hanging request keeps the
        /// updater busy for the whole session.</summary>
        internal const int RequestTimeout = 15;
        /// <summary>Deadline in seconds for the download of a plugin DLL.</summary>
        internal const int DownloadTimeout = 300;

        /// <summary>Only a PE image may replace a plugin: an error page or a cut-off body that still came back
        /// as HTTP 200 must never be written over the working DLL.</summary>
        internal static bool IsPluginImage(byte[] payload) =>
            payload != null && payload.Length >= 4096 && payload[0] == (byte)'M' && payload[1] == (byte)'Z';

        // ---------------------------------------------------------------- senders
        /// <summary>The client a message really came from: the owner of the object the RPC arrived on. Never
        /// take a client id out of the payload, any client can write any number there. -1 without a sender.</summary>
        internal static int SenderClientId(PlayerControl sender) => sender == null ? -1 : sender.OwnerId;

        /// <summary>The sender exists, is in the game and alive.</summary>
        internal static bool SenderAlive(PlayerControl sender) =>
            sender != null && sender.Data != null && !sender.Data.IsDead && !sender.Data.Disconnected;

        /// <summary>The message came from the host (owner of the sending object, not a field in the payload).</summary>
        internal static bool SenderIsHost(PlayerControl sender) =>
            sender != null && AmongUsClient.Instance != null && sender.OwnerId == AmongUsClient.Instance.HostId;

        /// <summary>Host side: the sender stands within reach of a point. slack covers the distance a player
        /// moves between pressing and the message arriving; keep it generous, a wrongly refused action is worse
        /// than a cheat by a modified client.</summary>
        internal static bool SenderNear(PlayerControl sender, Vector2 point, float reach, float slack = 2f) =>
            SenderAlive(sender) && Vector2.Distance(sender.GetTruePosition(), point) <= reach + slack;
    }
}
