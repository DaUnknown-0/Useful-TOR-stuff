// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections.Generic;

namespace UsefulTORStuff {

    /// <summary>
    /// Local on/off switches shown on the "Unknown's Collection" page of TOR's Mod Options. Contract
    /// (BCL types only, no compile dependency either way): AppDomain data "Unknowns.Options.Toggles" is a
    /// List&lt;object&gt;; each entry is object[] { Func&lt;string&gt; title, Func&lt;bool&gt; get, Action&lt;bool&gt; set }.
    /// UC appends the entries after its own switches whenever the page opens.
    /// </summary>
    internal static class ExternalToggles {
        private const string Key = "Unknowns.Options.Toggles";

        internal static void Register(Func<string> title, Func<bool> get, Action<bool> set) {
            try {
                var domain = AppDomain.CurrentDomain;
                lock (domain) {
                    if (domain.GetData(Key) is not List<object> list) {
                        list = new List<object>();
                        domain.SetData(Key, list);
                    }
                    list.Add(new object[] { title, get, set });
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning("[ExternalToggles] " + e.Message);
            }
        }
    }
}
