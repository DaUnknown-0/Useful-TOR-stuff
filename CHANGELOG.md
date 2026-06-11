# Changelog — Useful TOR Stuff

## Unreleased

### Features
- **Meeting Duration Override (TOR Settings).** New host-authoritative option that overrides the
  meeting timer from the alive/dead player counts at meeting start. A master toggle plus two
  independent formulas — one for the discussion phase, one for the voting phase — each computed as
  `Base + aliveCount × PerAlivePlayer − deadCount × ReductionPerDeadPlayer`, clamped to a hard
  minimum of 0s. On the host, `MeetingHud.Start` writes the results into the vanilla
  `DiscussionTime`/`VotingTime` and `SyncOptions()`s them, so the override applies to every client
  regardless of who has the mod (not mod-gated). The host's configured discussion/voting times are
  snapshotted once per game and restored on `AmongUsClient.OnGameEnd` so the lobby settings don't
  drift. New file `MeetingDurationOverride.cs`; options 1210–1216.

## 1.1.0

Adds the two approved features (F1, F2) on top of the 1.0.1 fix base. No wire-format changes
(RPC 253 handshake unchanged); mixed lobbies keep working.

### Features
- **F1 — Consolidated lobby version handshake (presentation only).** When the Chance mod is also
  loaded, Useful TOR Stuff now owns and renders a single combined **Mod-Check** per-player overview
  (green ok / red mismatch / gray missing), collapsing to "all players match ✓" when nothing is
  wrong, instead of two separate red lists. Each mod publishes its snapshot over the documented
  `TORMods.Handshake.*` AppDomain keys (plain strings / `Dictionary<int,string>` only — no shared
  types); Chance suppresses its own block while Useful is present and falls back to its standalone
  block when alone. HostFix is intentionally excluded (host-only). Host-side by default
  (`ShowToAllPlayers` switch). Wire format (RPCs 251/253) is untouched.
- **F2 — Mod Manager "Update All" + release notes.** A header **UPDATE ALL** button (enabled when
  ≥1 mod has an update) downloads every updatable mod's release **sequentially** — the updaters are
  single-`_busy` state machines — then shows one summary line ("N updated — restart required"),
  resilient to a mod's check/download having failed. Expanded mod entries with an available update
  now render the newest release's notes (crude markdown strip, first ~10 lines / ~600 chars, "…" on
  truncation) from the already-fetched JSON — no extra API calls. Both features probe the new
  reflection hooks and hide gracefully for older installed updaters that lack them.

## 1.0.1

Patch bump. No wire-format changes (RPC 253 handshake unchanged); mixed lobbies keep working.

### P0 — Crash / correctness
- **P0.1** — `CoShowAnnouncement`: `yield return null;` → `yield break;` and a null-guard on
  `MainMenuManager` before `StartCoroutine(...)` (announcement coroutine + `OnSceneLoaded`),
  preventing `Instantiate(null)` and a NRE.
- **P0.2** — `CoCheckForUpdate`: GitHub deserialize + sort wrapped in try/catch/finally so a
  rate-limit/malformed response can no longer kill the coroutine and wedge `_busy`/`_checkCompleted`
  for the session. `Releases == null` treated as "no update"; all exits reset the flags.

### P1 — Functional / leaks
- **P1.1** — `BloodyThrottlePatch._lastDropPos` is cleared each round via a new postfix on TOR's
  `RPCProcedure.resetVariables` (reflection-resolved, degrades to a no-op with a warning if absent),
  so the per-player last-drop map no longer leaks across games / onto reused player ids.
- **P1.2** — `ModManagerUI` no longer leaks `Texture2D`/`Sprite`/`Material` assets. A static
  `GetSolidSprite(Color)` cache (process-wide, `DontDestroyOnLoad`) is reused everywhere and the
  overlay `Material` is created once, instead of fresh assets on every `Show()`/toggle.
- **P1.5** — `playerVersions` cleared on `AmongUsClient.OnGameJoined`, so the handshake cache only
  reflects the current lobby.

### P2 — QoL / hygiene
- **P2.3** — PingTracker version line guarded by its `LinkId` marker against per-frame stacking.
- **P2.4** — Removed dead `ModManagerUI.GenerateModListText()` (never called).
- **P2.5** — `DisableBackgroundUI`: documented the name-substring heuristic for future maintainers
  and added an explicit "never touch anything under `_popup`" guard in the child loop.
- **P2.8** — Updater sends a `User-Agent` header on the GitHub API request.
