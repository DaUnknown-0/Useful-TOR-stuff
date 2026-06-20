# TOR - Forgotten Fixes

A companion plugin for [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (TOR)
4.8.0 that bundles several quality-of-life fixes and a cross-mod Mod Manager. It resolves TOR types
via reflection, so every patch degrades to a no-op (with a log warning) rather than crashing if
TOR's internals change.

This mod is not affiliated with Among Us or Innersloth LLC, and the content contained therein is not
endorsed or otherwise sponsored by Innersloth LLC. Portions of the materials contained herein are
property of Innersloth LLC. © Innersloth LLC.

## Features

- **Bloody lag throttle** — skips spawning a new Bloody-modifier blood trail object until the player
  has moved a minimum distance, cutting the object count (and lag) on long trails. The per-player
  last-drop map is cleared each round (1.0.1).
- **Bloody killer-map fix** — makes a Bloody trail track the *latest* victim instead of pinning to
  the first one for the whole game.
- **Client-side Snitch reveal** — a reimplementation of the Snitch reveal that runs on every client,
  gated on *all players having this mod*. When active, the host-only TOR - Hostfix re-broadcast stands
  down. Surfaced via lobby messages and a one-time chat confirmation.
- **Sheriff "prevents killer parity win"** — host-enforced option that can stop the impostors from
  winning on parity while a Sheriff is alive. Always applies when enabled (host-authoritative); the
  host is warned in the lobby if not everyone has the mod.
- **Meeting duration override** — host-authoritative "TOR Settings" option that overrides the
  meeting timer from the alive/dead counts at meeting start. Separate formulas for the discussion and
  voting phases (`Base + alive × per-alive − dead × per-dead`, min 0s); the host syncs the result so
  it applies to everyone, even clients without the mod. The host's configured times are restored when
  the game ends.
- **Mod Manager** — an in-game UI listing the installed companion mods, their versions, update
  status, and per-mod enable/disable toggles. Asset-cached so repeatedly opening it / toggling no
  longer leaks textures, sprites, or materials (1.0.1). Since 1.1.0 it also has an **Update All**
  header button (sequential, with a summary line) and shows each updatable mod's **release notes**
  in its entry — both from the already-fetched GitHub JSON (no extra API calls), and both degrade
  gracefully for older installed updaters that lack the new hooks.
- **Combined lobby Mod-Check** — when the Chance mod is also installed, the lobby shows a single
  per-player version overview (green ok / red mismatch / gray missing) that this mod renders,
  instead of two separate warning lists. TOR - Hostfix is excluded by design (host-only). Host-side by
  default; the RPC wire format is unchanged.
- **Auto-update** — checks this repo's GitHub releases on the main menu and offers an in-game update
  button.
- **Version display** — shows `TOR - Forgotten Fixes vX.Y.Z` in the top-corner version readout.

## Download & Install

1. Install The Other Roles into your Among Us BepInEx setup.
2. Download the latest `UsefulTORStuff.dll` from the [Releases page](https://github.com/DaUnknown-0/Useful-TOR-stuff/releases/latest).
3. Copy `UsefulTORStuff.dll` into `<Among Us>/BepInEx/plugins/`.
4. Start the game.

After the first install, the in-game auto-updater checks this repo's GitHub releases on the main menu and offers an update button — manual downloads are only needed for the initial setup.

## License

This project is licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).

It is a derivative work of [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles), which
is also GPL-3.0. As required by the GPL, the full source of this modification is available in this
repository, and any redistribution or modified version must remain under GPL-3.0.
