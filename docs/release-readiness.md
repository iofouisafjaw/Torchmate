# Windows GitHub Alpha readiness

The owner authorized a history-free GitHub source Alpha on 2026-10-23. Windows
binary releases remain in preparation. Build and packaging scripts do not upload or push.

For the current 0.10.34 public-source preparation, use
[the history-free export workflow](github-preparation.md). The owner selected [MIT](../LICENSE) on 2026-10-23 for the project's own
work; third-party terms and historical upstream limitations remain separate. Local installation and automatic checks have
passed, but the requested gameplay retest and recording were cancelled; neither
native acceptance nor clean-Windows acceptance has been marked complete.

## Authorization audit (2026-10-20)

1. The historical starting point is `SlothfulDreams/terrar.ai` at `3a0f316962332cf3abab922f247ffa589766362c`, with no recorded license/permission. Version 0.10.30 accounts for all 29 inherited C# files: 8 removed and 21 implementation replacements or compatibility declarations. The previously deleted ModelRouter and obsolete plan stay absent. Current local Agent/planner integrations are preserved after source comparison. This is not a clean-room claim.
2. Both inherited icons are replaced by the new editable pixel lantern mark. SVG and deterministic PNG generation are included in source exports; dimensions remain 80×80 and 30×30. Check dispositions, source review scope and hashes in `release/source-provenance.json`, with `python scripts/check_provenance.py`. `--compare-upstream` additionally scans the pinned Git C# in a private checkout; it never reads `.env`.
3. `Content/Systems/Vendor/QotPlaceStyle.cs` is an actual MIT-licensed adaptation from ImproveGame. This corrects the overly broad impression that all previously reviewed external code was only a design reference. Its full license accompanies the source, the kit and the `.tmod` packaging inputs.
4. llama.cpp and official Qwen3 models have identified MIT/Apache-2.0 terms. Binaries, CUDA DLLs, Python and model weights are not included in preparation archives. Installation downloads pinned artifacts separately. NVIDIA terms are separate from llama.cpp's MIT license.
5. Wiki databases, cached pages, player/world saves, raw local experiment logs and `.env` are excluded. Wiki importers preserve their own source-specific metadata; no database is silently installed by Alpha setup.
6. The inherited repository tracks `.env`. No contents were printed or exported during this audit. A direct push of that Git history is blocked pending review; `.gitignore` alone does not remove it. Do not use `git archive HEAD` as an Alpha export.

Machine-readable evidence and links: [`release/license-audit.json`](../release/license-audit.json). An unsent permission request draft is available in [`authorization-request-draft.md`](authorization-request-draft.md). The project MIT license covers only rights held by its contributors; third-party terms remain in force. Technical replacement does not authorize historical versions, Git history or previous private packages.

## Local preparation

```
python scripts/build_windows.py --tml .runtime/tModLoader --no-install
python tests/check_package.py
python scripts/check_provenance.py
python scripts/release_alpha.py --check
python scripts/release_alpha.py --prepare-only --kind windows
python scripts/release_alpha.py --prepare-only --kind source
```

For Windows archives, `--check` intentionally exits 2 while acceptance is pending;
omitting `--prepare-only` refuses archive creation. Private preparations remain
under `dist/alpha-preparation/`, marked `PRIVATE-PREP`. For the approved source-only
scope, `python scripts/release_alpha.py --check --kind source` verifies separate
gates and `python scripts/release_alpha.py --kind source` creates the clean public
source ZIP under `dist/alpha/`. Its manifest identifies exact files and hashes;
approval never includes the inherited Git history or private preparation markers.

Archive creation does not push Git, change remotes, create releases or contact Steam. It does not infer authorization from source changes, a successful build or passing tests.

## Remaining acceptance

`release/acceptance.json` deliberately leaves both native gameplay and clean Windows installation pending. Automated tests use temporary profiles, synthetic small artifacts and mock processes/HTTP responses; they do not establish GPU-driver compatibility, actual model startup speed, in-game FPS or mouse/IME behavior.

Before a public Alpha, review the replacement evidence and license scope, exclude inherited history, then have the player complete:

- Installation from an extracted kit in a path with spaces/Chinese characters on a clean Windows x64 environment with Python 3.11+ x64 and Steam tModLoader.
- NVIDIA driver/CUDA 13.4 compatibility, or CPU runtime selection. Measure game performance while 14B is running; do not infer minimum VRAM from weight size alone.
- Connection checks, endpoint conflicts, model startup and restart after updating the brain. Foreign processes must stay untouched.
- Gathering/crafting, housing and Eye cooperative tasks, stopping, rejection and failure recovery in a disposable test world. Keep the feature-specific manual checklist in `docs/playtest.md`.
- Save/reload, upgrades, optional-mod combinations and both Chinese/English game UI. No automatic resume of old permissions or cooperative execution after leaving the world.

8B downloads are experimental; the prior 14B success figures do not apply to 8B. No new local-model benchmark is claimed here.

GitHub source publication and Steam Workshop have distinct requirements. The latter's [rules on external software](https://store.steampowered.com/workshopeula/1281930/) remain uncleared for this local-service architecture; no Workshop upload is prepared.
