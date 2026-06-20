// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DrunkRename - renames the Invert modifier to "Drunk" with a matching description.
 *
 * TOR stores modifier names in two places: the CustomOption.name field (shown in the
 * settings panel) and RoleInfo fields (shown in the intro and end-game screens). Both
 * are plain public strings that can be mutated at runtime without patching any methods.
 *
 * The option's onChange callback fires whenever the host toggles the option in settings,
 * so the rename stays live without a restart. The same ApplyRename() call at the end of
 * CreateOptions() handles configs where the option was saved as ON from a previous session.
 */

using System;
using TheOtherRoles;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class DrunkRename {
        public static CustomOption Option;

        private const string OriginalName       = "Invert";
        private const string OriginalIntroDesc  = "Your movement is inverted";
        private const string DrunkName          = "Drunk";
        private const string DrunkIntroDesc     = "You are Drunk";

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1310, Types.Modifier, "Rename to Drunk",
                    false, CustomOptionHolder.modifierInvert,
                    onChange: () => ApplyRename(Option.getBool()));

                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.modifierInvertDuration);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                // Apply immediately if already saved as ON in the config
                if (Option.getBool()) ApplyRename(true);

                UsefulTORStuffPlugin.Logger?.LogInfo("[DrunkRename] Option created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DrunkRename] CreateOptions failed: {e}");
            }
        }

        private static void ApplyRename(bool enable) {
            try {
                string n    = enable ? DrunkName      : OriginalName;
                string desc = enable ? DrunkIntroDesc : OriginalIntroDesc;

                CustomOptionHolder.modifierInvert.name         = Helpers.cs(Color.yellow, n);
                CustomOptionHolder.modifierInvertQuantity.name = $"- {n} Quantity";
                CustomOptionHolder.modifierInvertDuration.name =
                    enable ? "- Number Of Meetings Drunk" : "- Number Of Meetings Inverted";

                RoleInfo.invert.name             = n;
                RoleInfo.invert.introDescription = desc;
                RoleInfo.invert.shortDescription = desc;

                UsefulTORStuffPlugin.Logger?.LogInfo($"[DrunkRename] Rename {(enable ? "applied" : "reverted")}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DrunkRename] ApplyRename failed: {e}");
            }
        }
    }
}
