// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

// Audit 16.11. UTS Teil 2 / TOR-026: Optionsregister sind keine Besitznachweise.
// Kernpakete enthalten nur TOR-Definitionsfelder. Nicht zugeordnete IDs werden einzeln gesendet.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;

namespace UsefulTORStuff {
    public static class OptionSyncFix {
        private static HashSet<int> torIds;
        private static List<KeyValuePair<string, HashSet<int>>> modGroups;
        private static readonly Dictionary<int, int> unknownGroups = new();
        private static int resolvedCount = -1;

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.ShareOptionSelections))]
        private static class SharePatch {
            public static bool Prefix() {
                // Audit 16.11. UTS Teil 2: Auch Presets und Clipboard benutzen die gepacte Queue.
                PacedOptionSync.QueueFullSync();
                return false;
            }
        }

        internal static int OwnerGroup(CustomOption option) {
            if (option == null) return -1;
            ResolveOwners();
            if (torIds.Contains(option.id)) return -1;
            for (int i = 0; i < modGroups.Count; i++)
                if (modGroups[i].Value.Contains(option.id)) return i;
            // Ein unbekannter Besitzer darf keine andere, beim Gast bekannte Option abschneiden.
            if (!unknownGroups.TryGetValue(option.id, out int group)) {
                group = modGroups.Count + unknownGroups.Count;
                unknownGroups.Add(option.id, group);
            }
            return group;
        }

        // ── Receiving side ────────────────────────────────────────────────────────────────────

        // WHO SENT THIS (AUDIT TOR-2026-08-23, H-3)
        //
        // TOR applies an incoming options block without ever asking who sent it: HandleShareOptions
        // is reached from RPCHandlerPatch with no sender in scope, and CustomOption.updateSelection
        // additionally runs switchPreset() for id 0. So ANY guest could rewrite the whole lobby's
        // settings on every other client - including the host's preset - and nothing would log it.
        //
        // The sender is only known one level up, in PlayerControl.HandleRpc, so it is captured there
        // and read back here. Same "remember the sender, check it in the handler" shape UCRpc uses
        // for its own channel, which is where this pattern is already proven.
        private static byte lastSenderClientId;
        private static bool lastSenderKnown;

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        private static class SenderCapturePatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(PlayerControl __instance) {
                lastSenderKnown = false;
                try {
                    if (__instance == null) return;
                    lastSenderClientId = (byte)__instance.PlayerId;
                    lastSenderKnown = true;
                } catch { }
            }
        }

        // True when the message currently being dispatched came from the host (or from ourselves,
        // which is the host applying its own broadcast locally). Fails OPEN when the sender could
        // not be determined: a settings sync that stops working is worse than one that stays
        // spoofable, and every other layer of this file is unaffected either way.
        private static bool SenderIsHostOrUnknown() {
            try {
                if (!lastSenderKnown) return true;
                var client = AmongUsClient.Instance;
                if (client == null) return true;
                var sender = Helpers.playerById(lastSenderClientId);
                if (sender == null) return true;
                if (sender.AmOwner) return true;                      // our own local apply
                return sender.OwnerId == client.HostId;
            } catch { return true; }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.HandleShareOptions))]
        private static class ReceivePatch {
            public static bool Prefix(byte numberOfOptions, MessageReader reader) {
                if (!SenderIsHostOrUnknown()) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[OptionSync] refused a settings block from player {lastSenderClientId}, who is not "
                        + "the host - TOR itself applies these unconditionally (AUDIT TOR-H3).");
                    return false;
                }
                try {
                    // Audit 16.11. TOR-026 / UTS Teil 3: Erst vollständig lesen, dann bekannte
                    // Werte anwenden. Eine unbekannte ID oder defekte UI beendet keinen Sync-Block.
                    var received = new List<(CustomOption Option, int Selection)>();
                    for (int i = 0; i < numberOfOptions; i++) {
                        uint optionId = reader.ReadPackedUInt32();
                        uint selection = reader.ReadPackedUInt32();
                        CustomOption option = CustomOption.options.FirstOrDefault(x => x != null && (uint)x.id == optionId);
                        if (option == null || selection >= (uint)(option.selections?.Length ?? 0)) continue;
                        received.Add((option, (int)selection));
                    }
                    for (int index = 0; index < received.Count; index++) {
                        var item = received[index];
                        try {
                            // Wie TOR: nur der letzte Eintrag eines Blocks benachrichtigt (Änderungsmeldung
                            // und Neuzeichnen der offenen Lobby-Einstellungen). TOR meldet nur echte Wertwechsel.
                            item.Option.updateSelection(item.Selection, index == received.Count - 1);
                            // Scheitert die Meldung (der Finalizer B8 in TorAuditFixes schluckt das), ist der Wert
                            // noch nicht gesetzt: dann still nachziehen.
                            if (item.Option.selection != item.Selection) item.Option.updateSelection(item.Selection, false);
                            UTSGate.RecordHostOption(item.Option.id);
                        } catch (Exception ex) {
                            UsefulTORStuffPlugin.Logger?.LogWarning($"[OptionSync] Option {item.Option.id}: {ex.Message}");
                        }
                    }
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[OptionSync] receive failed: {ex.Message}");
                }
                return false;
            }
        }

        // ── Resolution ────────────────────────────────────────────────────────────────────────

        private static void ResolveOwners() {
            if (resolvedCount == CustomOption.options.Count) return;
            // Nur direkte Definitionsfelder von CustomOptionHolder sind TOR-Eigentum.
            // Insbesondere CustomOption.options und fremde Aliase darauf nie auswerten.
            torIds = new HashSet<int>();
            foreach (var field in typeof(CustomOptionHolder).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.FieldType == typeof(CustomOption) && field.GetValue(null) is CustomOption option)
                    torIds.Add(option.id);
            modGroups = new List<KeyValuePair<string, HashSet<int>>>();
            unknownGroups.Clear();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName)) {
                if (asm == typeof(CustomOption).Assembly) continue;
                try {
                    if (!asm.GetReferencedAssemblies().Any(a => a.Name == "TheOtherRoles")) continue;
                    // Die eigene Queue enthält absichtlich ALLE Optionen. Ihre Kopie ist ebenso
                    // wenig ein Besitznachweis wie TORs Register. UTS kennt seine IDs exakt.
                    var ids = asm == typeof(OptionSyncFix).Assembly
                        ? new HashSet<int>(UTSGate.OwnOptionIds) : CollectOptionIds(asm);
                    ids.ExceptWith(torIds);
                    if (ids.Count > 0) modGroups.Add(new(asm.GetName().Name, ids));
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[OptionSync] Besitzersuche {asm.GetName().Name}: {ex.Message}");
                }
            }
            resolvedCount = CustomOption.options.Count;
        }

        // Every CustomOption reachable from a static field of the assembly - directly, or inside a
        // static array / list / dictionary (SabotageTuning keeps its per-sabotage options in arrays,
        // and other mods will do the same).
        private static HashSet<int> CollectOptionIds(Assembly asm) {
            var ids = new HashSet<int>();
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            catch { return ids; }

            foreach (Type type in types) {
                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { continue; }

                foreach (FieldInfo field in fields) {
                    // Only read fields that can hold options at all. Reading a static field runs its
                    // type's static constructor, and there is no reason to trigger those in every
                    // type of every mod just to look for options.
                    if (!CanHoldOptions(field.FieldType)) continue;
                    object value;
                    try { value = field.GetValue(null); }
                    catch { continue; }
                    Harvest(value, ids, 0);
                }
            }
            return ids;
        }

        private static bool CanHoldOptions(Type t) {
            if (t == typeof(CustomOption)) return true;
            if (t.IsArray) return t.GetElementType() == typeof(CustomOption);
            if (t.IsGenericType) {
                foreach (Type arg in t.GetGenericArguments())
                    if (arg == typeof(CustomOption)) return true;
            }
            return false;
        }

        private static void Harvest(object value, HashSet<int> ids, int depth) {
            if (value == null || depth > 2 || ReferenceEquals(value, CustomOption.options)) return;
            if (value is CustomOption option) { ids.Add(option.id); return; }
            // Strings are IEnumerable too and would recurse per character.
            if (value is string) return;
            if (value is IDictionary dict) {
                foreach (object v in dict.Values) Harvest(v, ids, depth + 1);
                return;
            }
            if (value is IEnumerable list) {
                try {
                    foreach (object v in list) Harvest(v, ids, depth + 1);
                } catch { }
            }
        }
    }
}
