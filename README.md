# Useful TOR Stuff

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
  gated on *all players having this mod*. When active, the host-only Host Fix re-broadcast stands
  down. Surfaced via lobby messages and a one-time chat confirmation.
- **Sheriff "prevents killer parity win"** — host-enforced option that can stop the impostors from
  winning on parity while a Sheriff is alive. Always applies when enabled (host-authoritative); the
  host is warned in the lobby if not everyone has the mod.
- **Mod Manager** — an in-game UI listing the installed companion mods, their versions, update
  status, and per-mod enable/disable toggles. Asset-cached so repeatedly opening it / toggling no
  longer leaks textures, sprites, or materials (1.0.1). Since 1.1.0 it also has an **Update All**
  header button (sequential, with a summary line) and shows each updatable mod's **release notes**
  in its entry — both from the already-fetched GitHub JSON (no extra API calls), and both degrade
  gracefully for older installed updaters that lack the new hooks. *(F2)*
- **Combined lobby Mod-Check** — when the Chance mod is also installed, the lobby shows a single
  per-player version overview (green ok / red mismatch / gray missing) that this mod renders,
  instead of two separate warning lists. HostFix is excluded by design (host-only). Host-side by
  default; the RPC wire format is unchanged. *(F1)*
- **Auto-update** — checks this repo's GitHub releases on the main menu and offers an in-game update
  button.
- **Version display** — shows `Useful TOR Stuff vX.Y.Z` in the top-corner version readout.

## Options

Sheriff options appear in TOR's role-options UI under Sheriff:

| Option | Default | Notes |
|---|---|---|
| Sheriff Prevents Killer Parity Win | Off | Host-enforced; applies to everyone when on |
| ↳ Scope | At Exact Parity Only | or "Always While Sheriff Alive" |

### Configuration (BepInEx config)

`com.tormod.usefultorstuff.cfg`:
- `[General] Enabled` — load the mod (default `true`).
- `[ModManager] Enabled` — show the Mod Manager button/UI (default `true`).
- `[ModManager] ButtonPositionX` / `ButtonPositionY` — main-menu button position (default `0.8` / `0.21`).
- `[Bloody] MinDropDistance` — world-unit distance before a new blood drop spawns (default `0.35`;
  `0` disables throttling).

## Mod version handshake (RPC 253)

Each client broadcasts its version + assembly GUID at lobby time so every client can tell whether
all players share the same build (the precondition for the client-side Snitch fix). The handshake
cache is cleared on joining a lobby so it only reflects the current lobby. Wire format is unchanged
across 1.0.x, so mixed lobbies keep working.

## Requirements

- [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) 4.8.0 (hard dependency)
- BepInEx IL2CPP 6.0.0-be.697
- Among Us (Steam build matching your TOR version)

## Building

```
dotnet build -c Release
```

The output `UsefulTORStuff.dll` lands in `bin/Release/net6.0/`. Set the `AmongUsLatest` environment
variable to your Among Us folder to auto-copy on build.

## Installation

1. Install The Other Roles into your Among Us BepInEx setup.
2. Copy `UsefulTORStuff.dll` into `<Among Us>/BepInEx/plugins/`.
3. Start the game.

## Compatibility

| Useful TOR Stuff | The Other Roles | Among Us |
|---|---|---|
| 1.1.0 | 4.8.0 | Steam build matching TOR 4.8.0 |

## License

This project is licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).

It is a derivative work of [The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles), which
is also GPL-3.0. As required by the GPL, the full source of this modification is available in this
repository, and any redistribution or modified version must remain under GPL-3.0.
