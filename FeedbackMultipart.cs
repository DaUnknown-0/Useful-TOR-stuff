// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System.Text;

namespace UsefulTORStuff;

internal static class FeedbackMultipart
{
    internal const int MaxText = 1000, Chunk = 300, MaxParts = 4;

    // Only a complete, bounded message may be stored and acknowledged.
    internal static bool TryJoin(string first, string[] parts, int total, out string text)
    {
        text = null;
        if (first == null || first.Length > Chunk || total < 1 || total > MaxParts) return false;
        if (total == 1) { text = first; return true; }
        if (first.Length != Chunk || parts == null || parts.Length != total) return false;
        var joined = new StringBuilder(first);
        for (int i = 1; i < total; i++)
        {
            string part = parts[i];
            if (string.IsNullOrEmpty(part) || part.Length > Chunk) return false;
            if (i < total - 1 && part.Length != Chunk) return false;
            if (joined.Length + part.Length > MaxText) return false;
            joined.Append(part);
        }
        text = joined.ToString();
        return true;
    }
}
