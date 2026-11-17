using System;
using System.Collections.Generic;

namespace UsefulTORStuff {
    /// <summary>
    /// Extension point for companion mods whose roles kill through TOR's UncheckedMurderPlayer as a non-host.
    /// The RPC boundary (Audit 16.11., package A) only accepts kills from roles it knows. A companion mod adds
    /// one rule without any compile-time reference and independent of the plugin load order, through the
    /// AppDomain slot <see cref="DomainKey"/>: a <c>List&lt;object&gt;</c> holding
    /// <c>Func&lt;PlayerControl, PlayerControl, bool?&gt;</c> delegates (create the list if it is missing).
    /// A rule returns null when the source is not one of its roles, otherwise whether this attack is legal.
    /// </summary>
    public static class UTSAttackRules {
        public const string DomainKey = "UTS.AttackRules";

        public static void Register(Func<PlayerControl, PlayerControl, bool?> rule) {
            if (rule == null) return;
            var list = Slot(true);
            lock (list) if (!list.Contains(rule)) list.Add(rule);
        }

        private static List<object> Slot(bool create) {
            var domain = AppDomain.CurrentDomain;
            lock (typeof(AppDomain)) { // dieselbe Sperre nutzen die Begleit-Mods beim Anlegen
                if (domain.GetData(DomainKey) is List<object> list) return list;
                if (!create) return null;
                list = new List<object>();
                domain.SetData(DomainKey, list);
                return list;
            }
        }

        // null: no registered rule is responsible. A throwing rule counts as "not legal".
        internal static bool? Evaluate(PlayerControl source, PlayerControl target) {
            var list = Slot(false);
            if (list == null) return null;
            object[] copy;
            lock (list) copy = list.ToArray();
            foreach (var entry in copy) {
                if (entry is not Func<PlayerControl, PlayerControl, bool?> rule) continue;
                try {
                    var verdict = rule(source, target);
                    if (verdict.HasValue) return verdict;
                } catch { return false; }
            }
            return null;
        }
    }
}
