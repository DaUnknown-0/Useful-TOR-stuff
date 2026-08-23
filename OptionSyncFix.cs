// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * OptionSyncFix - a mod option must never cost a plain-TOR client the settings that follow it.
 *
 * THE BUG (in TOR, triggered by every mod that adds options)
 * The host shares the lobby settings in blocks of up to 200 options
 * (CustomOption.ShareOptionSelections). The receiver walks such a block with
 *
 *     CustomOption option = CustomOption.options.First(option => option.id == (int)optionId);
 *     option.updateSelection(...)                                        // RPC.cs:203-211
 *
 * `First` THROWS when no option with that id exists, and the try/catch sits OUTSIDE the loop. So the
 * first id a client doesn't know aborts the entire rest of the block. On a client that lacks one of
 * the mods the host is running, that is not just "the mod's options are missing" (which would be
 * fine and unavoidable): every TOR option that happened to come after it in the same block is
 * dropped too, and that client silently plays the round with its own locally stored values for
 * them. Our options are inserted next to the TOR options they belong to (right below the Medic, the
 * Sheriff, ...), i.e. in the middle of the very first block, so the loss is real and large.
 *
 * THE FIX, host side (this is the half that helps clients WITHOUT the mod)
 * Send the same data, in the same wire format, but never mix owners inside one block: first every
 * option TOR itself owns, then one group of blocks per mod assembly. An abort can then only ever
 * kill the tail of a block that belongs to a mod the receiver doesn't have anyway - and the options
 * it does know have all arrived before that. Nothing about the receiving code has to change, which
 * is the point: the clients that need this fix are exactly the ones that don't run our code.
 *
 * WHO OWNS WHAT
 * Resolved once, by reflection, and deliberately conservative:
 *   - TOR's own options: every CustomOption reachable from a static field in TheOtherRoles. These
 *     always count as core, even if a mod also happens to reference one.
 *   - A mod's options: every CustomOption reachable from a static field (also inside arrays, lists
 *     and dictionaries) of an assembly that references TheOtherRoles, minus TOR's own. Our own ids
 *     come from UTSGate, which knows them exactly rather than by reflection.
 *   - Anything left over stays in the core group, which is the safe direction: a wrongly grouped
 *     mod option only means this fix does nothing for that one option, while a wrongly grouped TOR
 *     option would recreate the very bug we are fixing.
 * If any of that fails to resolve, the prefix bails out and TOR's original send runs unchanged.
 *
 * THE FIX, receiving side (this half helps clients WITH the mod)
 * The same loop, with FirstOrDefault and an unknown id simply skipped instead of aborting. That
 * covers the mirror case: a future mod that adds options without grouping them can no longer cost
 * US the rest of a block.
 */

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
        // CustomRPC.ShareOptions. The enum is internal to TOR, so it is resolved by name; 101 is the
        // documented fallback (RPC.cs: ResetVaribles = 100, ShareOptions next).
        private const byte ShareOptionsFallbackId = 101;
        private static byte shareOptionsRpcId;
        private static bool rpcIdResolved;

        private const int BlockSize = 200;   // same as TOR

        // Owner grouping, resolved on first use.
        private static bool ownersResolved;
        private static HashSet<int> torIds;                       // TOR's own option ids
        private static List<KeyValuePair<string, HashSet<int>>> modGroups;  // one entry per mod assembly

        // ── Send side ─────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.ShareOptionSelections))]
        private static class SharePatch {
            public static bool Prefix() {
                try {
                    // TOR's own guards, kept identical.
                    if (PlayerControl.AllPlayerControls.Count <= 1) return false;
                    if (AmongUsClient.Instance == null) return true;
                    if (!AmongUsClient.Instance.AmHost && PlayerControl.LocalPlayer == null) return false;

                    if (!ResolveRpcId() || !ResolveOwners()) return true;   // fall back to TOR's send

                    var all = new List<CustomOption>(CustomOption.options);
                    var core = new List<CustomOption>();
                    var perGroup = new List<List<CustomOption>>();
                    for (int i = 0; i < modGroups.Count; i++) perGroup.Add(new List<CustomOption>());

                    foreach (var option in all) {
                        if (option == null) continue;
                        int group = GroupOf(option.id);
                        if (group < 0) core.Add(option);
                        else perGroup[group].Add(option);
                    }

                    SendBlocks(core);
                    for (int i = 0; i < perGroup.Count; i++) SendBlocks(perGroup[i]);
                    return false;
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[OptionSync] grouped send failed: {ex}");
                    return true;   // never leave the lobby without its settings
                }
            }
        }

        // -1 = core (TOR / unknown), otherwise the index into modGroups.
        private static int GroupOf(int optionId) {
            if (torIds.Contains(optionId)) return -1;
            for (int i = 0; i < modGroups.Count; i++)
                if (modGroups[i].Value.Contains(optionId)) return i;
            return -1;
        }

        private static void SendBlocks(List<CustomOption> options) {
            int sent = 0;
            while (sent < options.Count) {
                int amount = Math.Min(options.Count - sent, BlockSize);
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, shareOptionsRpcId, SendOption.Reliable, -1);
                writer.Write((byte)amount);
                for (int i = 0; i < amount; i++) {
                    CustomOption option = options[sent + i];
                    writer.WritePacked((uint)option.id);
                    writer.WritePacked(Convert.ToUInt32(option.selection));
                }
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                sent += amount;
            }
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
        [HarmonyPriority(Priority.First)]
        private static class SenderCapturePatch {
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
                    for (int i = 0; i < numberOfOptions; i++) {
                        uint optionId = reader.ReadPackedUInt32();
                        uint selection = reader.ReadPackedUInt32();
                        // The one line that differs from TOR: an id we don't know is skipped instead
                        // of throwing, so the remaining options in this block still get applied.
                        CustomOption option = CustomOption.options.FirstOrDefault(x => x != null && x.id == (int)optionId);
                        if (option == null) continue;
                        option.updateSelection((int)selection, i == numberOfOptions - 1);
                    }
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[OptionSync] receive failed: {ex.Message}");
                }
                return false;
            }
        }

        // ── Resolution ────────────────────────────────────────────────────────────────────────

        private static bool ResolveRpcId() {
            if (rpcIdResolved) return shareOptionsRpcId != 0;
            rpcIdResolved = true;
            shareOptionsRpcId = ShareOptionsFallbackId;
            try {
                var e = typeof(CustomOption).Assembly.GetType("TheOtherRoles.CustomRPC");
                if (e != null && Enum.IsDefined(e, "ShareOptions"))
                    shareOptionsRpcId = Convert.ToByte(Enum.Parse(e, "ShareOptions"));
                else
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[OptionSync] CustomRPC.ShareOptions not found - using {ShareOptionsFallbackId}.");
            } catch { }
            return shareOptionsRpcId != 0;
        }

        private static bool ResolveOwners() {
            if (ownersResolved) return torIds != null;
            ownersResolved = true;
            try {
                Assembly tor = typeof(CustomOption).Assembly;
                torIds = CollectOptionIds(tor);
                if (torIds.Count == 0) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[OptionSync] no TOR options found by reflection - grouped option sync disabled.");
                    torIds = null;
                    return false;
                }

                modGroups = new List<KeyValuePair<string, HashSet<int>>>();
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    if (asm == tor) continue;
                    // Only assemblies that actually build against TOR can hold CustomOptions. This
                    // also keeps the scan away from the huge Il2Cpp interop assemblies.
                    bool referencesTor;
                    try {
                        referencesTor = asm.GetReferencedAssemblies().Any(a => a.Name == "TheOtherRoles");
                    } catch { continue; }
                    if (!referencesTor) continue;

                    var ids = CollectOptionIds(asm);
                    if (asm == typeof(OptionSyncFix).Assembly)
                        foreach (int id in UTSGate.OwnOptionIds) ids.Add(id);   // exact, not reflected
                    ids.ExceptWith(torIds);
                    if (ids.Count > 0)
                        modGroups.Add(new KeyValuePair<string, HashSet<int>>(asm.GetName().Name, ids));
                }

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[OptionSync] option sync grouped: {torIds.Count} TOR option(s), "
                    + string.Join(", ", modGroups.Select(g => $"{g.Key}={g.Value.Count}")));
                return true;
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[OptionSync] owner resolution failed: {ex}");
                torIds = null;
                return false;
            }
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
            if (value == null || depth > 2) return;
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
