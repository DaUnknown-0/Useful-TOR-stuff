// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TricksterBoxCount - configurable "how many Jack-in-the-Boxes convert to a vent network" limit.
 *
 * TOR hardcodes this (JackInTheBox.JackInTheBoxLimit = 3, TheOtherRoles/Objects/JackInTheBox.cs:13)
 * while every other Trickster value (placeBoxCooldown, lightsOutCooldown, lightsOutDuration) is
 * already an option, applied from CustomOptionHolder inside Trickster.clearAndReload()
 * (TheOtherRoles.cs). A fixed 3-box net is very strong in a 6-player lobby and nearly meaningless
 * in a 15-player Airship lobby, so this fills the gap with a 1-5 selection, applied the same way
 * TOR applies its own three Trickster values: a postfix on Trickster.clearAndReload() writes the
 * effective count into JackInTheBox.JackInTheBoxLimit right after TOR has (re)set the others.
 *
 * EVERYONE NEEDS THE MOD
 * hasJackInTheBoxLimitReached() (JackInTheBox.cs:113-115) is evaluated LOCALLY on every client,
 * purely against the static JackInTheBoxLimit field - unlike AllJackInTheBoxes.Count, which grows
 * in lockstep everywhere via RPC. A client without this mod keeps TOR's hardcoded 3 while a client
 * with, say, 5 configured is still waiting for two more boxes: the boxes convert to vents (and the
 * Lights-Out button unlocks, Buttons.cs:1136/1153-1154) for some players and not for others - a
 * visible desync. So the configured value is only applied when EVERY player has the mod (same rule,
 * same pattern as MultiJester.EffectiveQuantity(), MultiJester.cs:129-130); otherwise this stands
 * down completely and TOR's own hardcoded 3 is written back, unchanged from vanilla.
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class TricksterBoxCount {
        public static CustomOption Option;   // 1303

        public static void CreateOptions() {
            try {
                // NOT CustomOption.Create(string[]...): that convenience overload hardcodes its
                // defaultValue to "" (CustomOptions.cs:79-81), which resolves to selection index 0
                // ("1") no matter what - fine for a quantity that should default low (MultiJester's
                // Jester Quantity), wrong here, where the default has to reproduce TOR's own
                // hardcoded 3. The underlying constructor is public and takes an explicit
                // defaultValue, so it is called directly instead.
                Option = new CustomOption(
                    1303, Types.Impostor, "Trickster Box Count",
                    new string[] { "1", "2", "3", "4", "5" }, "3",
                    CustomOptionHolder.tricksterSpawnRate, false);
                UTSLocalization.BindOptionTitle(Option, "uts.tricksterboxcount.option_name");

                var opts = CustomOption.options;
                opts.Remove(Option);
                // Right after "Trickster Box Cooldown" - the option this one is thematically tied to.
                int idx = opts.IndexOf(CustomOptionHolder.tricksterPlaceBoxCooldown);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[TricksterBoxCount] Option created under Trickster.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterBoxCount] CreateOptions failed: {e}");
            }
        }

        // What the host set. The selections are "1".."5" (index+1), so this reproduces TOR's own
        // hardcoded default (3) whenever the selection sits on "3" - including a closed UTSGate
        // (host missing the mod), which falls back to defaultSelection there. Only the lobby warning
        // uses this directly; everything else must go through EffectiveValue below.
        public static int ConfiguredValue => Option != null ? UTSGate.Qty(Option) : 3;

        // The value as it is ACTUALLY applied - see the "EVERYONE NEEDS THE MOD" block comment above.
        public static int EffectiveValue() =>
            UsefulVersionHandshake.EveryoneHasMod() ? ConfiguredValue : 3;

        // Runs after TOR's own clearAndReload has set placeBoxCooldown/lightsOutCooldown/
        // lightsOutDuration from CustomOptionHolder (same method, same moment) - just for the one
        // Trickster value TOR left hardcoded.
        [HarmonyPatch(typeof(Trickster), nameof(Trickster.clearAndReload))]
        private static class ClearAndReloadPatch {
            public static void Postfix() {
                try {
                    JackInTheBox.JackInTheBoxLimit = EffectiveValue();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TricksterBoxCount] apply failed: {e}");
                }
            }
        }

        // Host-only heads-up, once per round, mirroring MultiJester's "Jester Quantity" lobby
        // warning in spirit: tell the host his setting is not in effect rather than letting him find
        // out only by comparing screens mid-round. Posted as a local chat line at round start rather
        // than into TOR's shared lobby GameStartText - that surface is owned and laid out by
        // UsefulVersionHandshake.GameStartManagerUpdatePatch, which this file does not touch.
        private static bool warningChatShown;

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        private static class ResetPatch {
            public static void Postfix() => warningChatShown = false;
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        private static class IntroEndChatPatch {
            public static void Postfix() {
                try {
                    if (warningChatShown) return;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (Option == null || ConfiguredValue == 3) return;
                    if (UsefulVersionHandshake.EveryoneHasMod()) return;

                    var hud = HudManager.Instance;
                    if (hud == null || hud.Chat == null || PlayerControl.LocalPlayer == null) return;
                    warningChatShown = true;

                    hud.Chat.AddChat(PlayerControl.LocalPlayer,
                        UTSLocalization.Tr("uts.tricksterboxcount.mod_warning"));
                } catch { }
            }
        }
    }
}
