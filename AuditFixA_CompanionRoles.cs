using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    // Optionale Kopplung wie SheriffParityWin: keine harte Abhängigkeit von UC oder anderen Begleit-Mods.
    internal static class AuditFixA_CompanionRoles {
        private static readonly Dictionary<string, Type> types = new();
        private static Type Type(string name) {
            if (types.TryGetValue(name, out var found)) return found;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                found = assembly.GetType(name, false);
                if (found != null) { types[name] = found; return found; }
            }
            return null;
        }
        private static object Field(string type, string field) {
            var t = Type(type);
            return t == null ? null : AccessTools.Field(t, field)?.GetValue(null);
        }
        private static bool Holder(string type, string field, PlayerControl player) => player != null
            && Field(type, "active") is true && Field(type, field) is PlayerControl holder && holder == player;
        private static bool InvokeBool(string type, string method, params object[] args) {
            var t = Type(type);
            return t != null && AccessTools.Method(t, method)?.Invoke(null, args) is true;
        }

        // null bedeutet keine dieser Rollen. false darf nicht auf eine andere Killberechtigung fallen.
        internal static bool? Attack(PlayerControl source, PlayerControl target) {
            try {
                if (Holder("UnknownsCollection.Stalker", "stalker", source))
                    return source != target && Field("UnknownsCollection.Stalker", "complete") is true
                        && Field("UnknownsCollection.Stalker", "targetId") is byte id && id == target.PlayerId;
                if (Holder("UnknownsCollection.Hunter", "hunter", source)) {
                    if (source != target) return InvokeBool("UnknownsCollection.Hunter", "IsLegalPrey", target);
                    // Der Hunter darf beim Fehlschuss selbst sterben oder seine eigene Rüstung brechen.
                    return PlayerControl.AllPlayerControls.ToArray().Any(p => AuditFixA_RpcBoundary.Alive(p)
                        && p != source && Vector2.Distance(source.GetTruePosition(), p.GetTruePosition()) <= 3f
                        && !InvokeBool("UnknownsCollection.Hunter", "IsLegalPrey", p));
                }
                if (Holder("UnknownsCollection.Pelican", "pelican", source))
                    return source != target && Field("UnknownsCollection.Pelican", "huntEnded") is false;
                // Weitere Begleit-Mods melden ihre Angriffsrollen selbst an (UTSAttackRules).
                var external = UTSAttackRules.Evaluate(source, target);
                if (external.HasValue) return external;
            } catch { return false; }
            return null;
        }

        private static readonly HashSet<byte> mixerRevenge = new();
        internal static void Observe(PlayerControl sender, byte call, MessageReader reader) {
            if (call != 230 || !AuditFixA_RpcBoundary.Host(sender)) return;
            try {
                var data = new AuditFixA_Payload(reader);
                if (Field("UnknownsCollection.UnknownsCollectionPlugin", "MixerRpcId") is not byte module || data.Byte() != module) return;
                byte kind = data.Byte();
                if (kind == 2) {
                    byte target = data.Byte(); bool revenge = data.Bool(); data.End();
                    if (revenge) mixerRevenge.Add(target); else mixerRevenge.Remove(target);
                } else if (kind == 4) {
                    byte shooter = data.Byte(); data.Byte(); data.Byte(); data.End(); mixerRevenge.Remove(shooter);
                } else if (kind == 0) {
                    data.Byte(); data.End(); mixerRevenge.Clear();
                }
            } catch { }
        }
        internal static bool MixerShieldAttempt(PlayerControl sender) => sender != null && (mixerRevenge.Contains(sender.PlayerId)
            || Field("UnknownsCollection.Mixer", "revengeHolders") is HashSet<byte> holders && holders.Contains(sender.PlayerId));
        internal static void Clear() => mixerRevenge.Clear();

        private static string Plain(string name) => System.Text.RegularExpressions.Regex.Replace(name ?? "", "<[^>]*>", "").Trim();

        internal static bool Guess(byte killer, byte target, byte role, out byte expected, out bool correction) {
            expected = killer; correction = false;
            try {
                var names = Type("UnknownsCollection.UCGuessNames");
                var infos = Type("UnknownsCollection.UCGuesser");
                RoleInfo selected = null;
                if (names != null && infos != null && Field(names.FullName, "picks") is IDictionary picks && picks.Contains(killer)) {
                    object pick = picks[killer];
                    var all = AccessTools.Method(infos, "AllInfos").Invoke(null, null) as RoleInfo[];
                    selected = AccessTools.Method(names, "Resolve").Invoke(null, new[] { pick, all }) as RoleInfo;
                    if (selected == null) {
                        // Auch eine explizite TOR-Auswahl muss zum gesendeten Namen passen.
                        var idx = AccessTools.Field(pick.GetType(), "Idx").GetValue(pick);
                        var name = AccessTools.Field(pick.GetType(), "Name").GetValue(pick) as string;
                        if (idx is not byte index || index != 255) return false;
                        selected = RoleInfo.allRoleInfos.FirstOrDefault(r => r != null && !r.isModifier
                            && (byte)r.roleId == role && (name == null || Plain(r.name) == name));
                    }
                } else selected = RoleInfo.allRoleInfos.FirstOrDefault(r => r != null && !r.isModifier && (byte)r.roleId == role);
                if (selected == null || selected.isModifier || (byte)selected.roleId != role) return false;
                var player = AuditFixA_RpcBoundary.Player(target);
                var actual = RoleInfo.getRoleInfoForPlayer(player, false).FirstOrDefault();
                var truth = Type("UnknownsCollection.UCGuessTruth");
                if (truth != null) {
                    object[] args = { player, null, null };
                    if (AccessTools.Method(truth, "TryMain").Invoke(null, args) is true && args[2] is RoleInfo main) actual = main;
                }
                if (actual == null) return false;
                expected = actual == selected ? target : killer;
                // UCs Tiering-Korrektur nur zulassen, wenn sie der unabhängig ermittelten Wahrheit entspricht.
                if (names != null && Field(names.FullName, "verdicts") is IDictionary verdicts && verdicts.Contains(killer)) {
                    object verdict = verdicts[killer];
                    correction = AccessTools.Field(verdict.GetType(), "Target").GetValue(verdict) is byte id && id == target
                        && AccessTools.Field(verdict.GetType(), "Value").GetValue(verdict) is byte value
                        && value == (expected == target ? 1 : 2);
                }
                return true;
            } catch { return false; }
        }

        internal static void PrepareShot(byte killer, ref byte dying, byte target, byte role) {
            // Vor UCs Prefix (Priorität 1000) korrigieren und dessen ungeprüftes Urteil entfernen.
            // Mixer-Rache nutzt einen direkten lokalen Aufruf mit target=255, keinen TOR-Schuss.
            if (target != 255 && Guess(killer, target, role, out byte expected, out _)) dying = expected;
            if (Field("UnknownsCollection.UCGuessNames", "verdicts") is IDictionary verdicts) verdicts.Remove(killer);
        }
    }
}
