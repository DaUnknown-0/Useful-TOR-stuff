// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackOwner - who receives the notes: the mod's author, recognised the same way as UTS's
 * EasterEggCoding does it (SHA-256 of the account's PUID, or of the friend code without one), so the
 * id itself is not in the source. Every client finds the author on its own; nothing is announced.
 */

using System.Security.Cryptography;
using System.Text;

namespace UsefulTORStuff;

internal static class FeedbackOwner
{
    private static readonly string[] OwnerHashes = {
        "d10f0e308524211c45d93ec3300efca28e35488ea35598196c67b1ababb7fc19",   // PUID
        "cf2eab42fba217e7af4237f7a3bcd35402f2790602e07d92c6344096701480c6",   // friend code
    };

    /// <summary>Self test only: the local client acts as a normal player for a moment.</summary>
    internal static bool ForceGuest;

    internal static bool IsOwner(PlayerControl p)
    {
        try {
            if (p == null || p.Data == null) return false;
            if (p == PlayerControl.LocalPlayer && (ForceGuest || FeedbackModule.DiagForceGuest?.Value == true)) return false;
            return Matches(p.Data.Puid) || Matches(p.Data.FriendCode);
        } catch { return false; }
    }

    internal static bool LocalIsOwner => IsOwner(PlayerControl.LocalPlayer);

    /// <summary>The author's PlayerControl in this lobby, or null.</summary>
    internal static PlayerControl Find()
    {
        try {
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p != null && p != PlayerControl.LocalPlayer && IsOwner(p)) return p;
        } catch { }
        return null;
    }

    /// <summary>Stable key of a player for the inbox: PUID, friend code, name (like UTS's PlayerIdentity).
    /// PUID and friend code only as SHA-256, the inbox never holds them in plain text (2026-10-30).</summary>
    internal static string KeyOf(PlayerControl p)
    {
        try {
            var d = p?.Data;
            if (d == null) return "";
            if (!string.IsNullOrEmpty(d.Puid)) return "puid#" + Sha(d.Puid);
            if (!string.IsNullOrEmpty(d.FriendCode)) return "fc#" + Sha(d.FriendCode);
            return "name:" + d.PlayerName;
        } catch { return ""; }
    }

    private static bool Matches(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        string h = Sha(id);
        foreach (var o in OwnerHashes) if (o == h) return true;
        return false;
    }

    internal static string Hash(string s) => Sha(s);

    private static string Sha(string s)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(64);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
