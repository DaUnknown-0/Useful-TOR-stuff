// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * VipDeathScream - a scream when a VIP gets killed.
 *
 * TOR already tells every player about it: its MurderPlayer postfix flashes the screen of every
 * client when the victim carries the VIP modifier (yellow, or team-coloured with "Show Team Color").
 * The scream rides on exactly that moment, on every client, so it reveals nothing the flash does not.
 * Like the flash it only covers kills; a VIP who is voted out or guessed dies through Exiled() and
 * gets neither.
 *
 * Purely local and cosmetic: nothing is sent, and every player can switch it off for themselves
 * ([Sounds] VipDeathScream in the config file).
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using BepInEx.Configuration;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    public static class VipDeathScream {
        public static ConfigEntry<bool> Enabled;

        public static void Bind(ConfigFile config) {
            Enabled = config.Bind("Sounds", "VipDeathScream", true,
                "Play a scream when a player with the VIP modifier gets killed. Everybody already sees "
                + "TOR's VIP flash at that moment, so the sound gives nothing away. Only affects your own game.");
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPlayerPatch {
            public static void Postfix([HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (Enabled != null && !Enabled.Value) return;
                    if (target == null || target.Data == null || !target.Data.IsDead) return;   // failed or blocked kill
                    if (Vip.vip == null || !Vip.vip.Exists(x => x != null && x.PlayerId == target.PlayerId)) return;
                    UTSAssets.PlayVipDeath();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[VipDeathScream] {e.Message}");
                }
            }
        }
    }
}
