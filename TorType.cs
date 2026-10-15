// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections.Generic;
using HarmonyLib;

namespace UsefulTORStuff {

    /// <summary>
    /// A TOR type by its full name, looked up in TOR's own assembly. AccessTools.TypeByName walks the types of every
    /// loaded assembly (the big Il2Cpp interop ones come first) on each call; at startup the patch classes made 13 such
    /// calls, about 1.7 s together (measured 15.10.). Falls back to TypeByName when TOR's assembly does not have it.
    /// </summary>
    internal static class TorType {
        private static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();

        internal static Type Get(string fullName) {
            lock (cache) {
                if (cache.TryGetValue(fullName, out var t)) return t;
                try { t = typeof(TheOtherRoles.TheOtherRolesPlugin).Assembly.GetType(fullName, false); } catch { t = null; }
                t ??= AccessTools.TypeByName(fullName);
                cache[fullName] = t;
                return t;
            }
        }
    }
}
