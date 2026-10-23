# Upstream implementation replacement — 0.10.30

The inherited `SlothfulDreams/terrar.ai` commit is
`3a0f316962332cf3abab922f247ffa589766362c`. No separate permission has been
recorded. This change replaces or removes its active implementation and two
icons; it does not retroactively license old checkouts or private archives.

## Method and limits

The implementer had access to the inherited source. This is **not a clean-room
claim**. Replacements were written against current behavior requirements,
existing local core contracts and the installed tModLoader API. They are not
renames or formatting of the old method bodies. Review includes dependencies,
comparison against the pinned inherited C# across files, and execution of the
actual replacement adapters in a simulated native API. Token comparison is a
screening aid, not proof of copyright clearance.

`release/source-provenance.json` records dispositions, retained local modules,
compatibility declarations, original and replacement hashes. The original
source itself is not copied into that record. `release/license-audit.json`
preserves the original permission observations alongside replacement results.
The owner selected MIT for the current project's own work on 2026-10-23; see
[LICENSE](../LICENSE) and [license scope](../LICENSE_STATUS.md). This does not
relicense historical upstream material. The owner authorized history-free GitHub
source publication on 2026-10-23; binary release and Workshop approval remain separate.

## Removed

- Actions: `ActionRegistry.cs`, `BuildAction.cs`.
- Systems: `ActionParameterReader.cs`, `ActionValidator.cs`, `WorldContext.cs`,
  `MultiAgentCoordinator.cs` (replaced by local `TaskTargetClaims`).
- NPCs: unused `AIAgentRenderer.cs`.
- Players: `SharedLifePlayer.cs`; the legacy life cap is intentionally gone.
- Previously deleted `ModelRouter.cs` stays absent; obsolete `plan.md` is removed.

## Reimplemented native surface

- `AgentAction`, `TileTargetAction`, `FailureAction`, `SayAction`, `CompleteAction`:
  sticky terminal result, bounded ticks, cancellation cleanup, mutation receipts.
- `MoveAction`: bounded call into the existing local `TerrainRoute`.
- `MineAction`, `ChopAction`: new shared `TileWork`; re-read tools and target,
  retain 3×3/default and guarded single-tile variants, check native protection,
  record actual changes, and stop when tools or target type change.
- `HellevatorAction`: only the expedition's guarded single-tile constructor is
  retained. The unused unlimited shaft/cheat registration is removed. Natural
  language excavation continues through the confirmed expedition planner.
- `MovementHelper`, `TreeHelper`, `ToolSelector`, `ServerAuthority`, `ActionParser`:
  replacement primitives and explicit typed dispatch. Model actions cannot
  enable legacy building or an unbounded shaft. Approved coordinates are not
  silently normalized to a different tree.
- `AIAgentNPC`: new lifecycle, state dispatch, follow and action pump. Retains
  post-inheritance local goal selection, approval tickets, episode logging,
  task reporting and recovery hooks. No inherited environment-description
  builders, randomized traits, phrase rotation or multi-agent scheduler remain.
- `TerrarAI`, commands, `ChatCoordinator` lifecycle and `XAIClient` facade:
  rebuilt entry points; existing local command routes and model/RAG protocols
  are retained after source comparison.
- `TerrarAI_Config`: re-declared from the frozen 18-field configuration contract.
  Type, names, default values, ranges and server-side scope are preserved.
  Declaration/API similarities are intentional compatibility data, not claims
  of a novel API. Project scaffold and launch metadata are regenerated.

## Retained local implementations

The current companion core (Agent loop, protocol/schema constraints, crafting,
blueprints, policies, task recovery and collaboration), terrain route search,
workshops, UI, RAG service, tests and Windows tooling remain. Cross-file source
screening includes these files to catch inherited fragments in newly named
files. Small common API declarations are reviewed separately. This review does
not claim that a file's creation date alone establishes its provenance.

ImproveGame's `Vendor/QotPlaceStyle.cs` remains an identified MIT adaptation;
its copyright and full MIT license remain in source and binary notices. Other
dependency licenses retain their separate scope.

## Artwork

`assets/companion-icon.svg` is a new rectangular pixel lantern/companion mark,
created for this change without taking the inherited icons as image input.
`python scripts/create_icons.py` deterministically renders it to `icon.png`
(80×80 RGBA) and `icon_small.png` (30×30 RGBA) using the standard library.
Neither the original images nor game textures are embedded in this artwork.
The NPC still references a game-provided texture at runtime; no texture is
copied into the package.

## Compatibility and deliberate behavior changes

Mod/config identities and saved memory paths stay unchanged. `/ai`, `/action`,
`/testqwen`, `/testxai`, the `qwen3-4b` service alias, opt-outs and creative flag
remain. Building, crafting, garden, combat, map, reminders, healing, cheats and
collaboration still use their existing services. Approval must still match the
same world, player, task, decision, rules and day/night; execution rechecks rules.

The legacy shared-life cap and unreachable unlimited shaft branch are removed.
General pickup now uses the recipe gatherer's remainder-preserving native
transfer, with the original 64px general and 96px recipe pickup radii. Full or
partly full inventory must not erase ground items. Gathering no longer replays
a completed action on a second tick or destroys a replacement tile type.

## Verification boundary

`tests/native_replacement` compiles actual production NPC/actions, configuration,
transport facade and helpers against simulated native APIs. It tests authority,
approval/context changes, cancel/timeout/partial failure, pickup conservation,
configuration compatibility and constrained model requests. It is not a Terraria
physics or real-game rendering test. Existing terrain/planner, recovery and
collaboration suites and production assembly/package checks are also rerun.

No real-game acceptance, personal Mods install, Git push, Release, Workshop upload
or author contact is performed. Final automated verification uses simulated model
requests and fixture validation. An accidental, interrupted model startup while
checking the old eval runner help is recorded in the verification report; its
outputs are not used as evaluation evidence. Help/validation now exit before
model startup. Old private snapshots and Git history
are not sanitized or authorized by this work; any future public repository must
use a reviewed source export. Player acceptance and a clean Windows install
remain pending.
