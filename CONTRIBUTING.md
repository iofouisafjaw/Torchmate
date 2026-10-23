# Contributing

TerrarAI is an experimental local companion for single-player Terraria. Current
source publication is being prepared. Its own code, documentation, tests and original
artwork use the [MIT License](LICENSE); see [LICENSE_STATUS.md](LICENSE_STATUS.md)
for scope and historical upstream limitations.

Unless explicitly agreed otherwise, contributions intentionally submitted for
inclusion are offered under the same MIT license. Submit only material you have
the right to contribute. Identify any third-party material, preserve its original
copyright/license, and update [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Contributing does not transfer copyright to the project.

## Development

- .NET 8 SDK and Python 3.11+ are sufficient for the core and simulated adapter tests.
- Building the actual mod also requires an existing compatible tModLoader installation;
  game assemblies, the inference runtime, models and Wiki databases are not in this repository.
- Run `python scripts/prepare_github.py --check` for source integrity, export exclusions
  and documentation links. Run the commands in [the architecture guide](docs/architecture.md)
  or `.github/workflows/ci.yml` for automatic verification.
- To build without installing or starting the game, use
  `python scripts/build_windows.py --tml "<tModLoader installation>" --no-install`.

Keep model decisions separate from native mutations. A new tool must retain task
ownership, cancellation, resource receipts, permission checks and fresh confirmation
for material spending. Schema constraints do not replace runtime validation.

Add tests that demonstrate the failure or verify observable behavior. Label simulated
game APIs and model responses explicitly. Do not present fixture results as gameplay
acceptance, or rerun a model benchmark and overwrite its previous evidence directory.

## Issues and pull requests

Report the version, instruction, reproduction steps and actual task receipt. Use
short, redacted logs. Do not attach saves, full conversations, `.env`, API keys,
model weights, Wiki dumps or game binaries. Explain what changed and how it was
verified. Follow the bundled terms for any third-party code or art.

The repository's source-hash gate intentionally detects edits. After reviewing an
intentional change, maintainers run `python scripts/check_provenance.py --record`
in the private audit checkout; this compares against the pinned upstream and does
not grant a license or approve publication. An exported checkout can run verification
without the old Git history. CI must not depend on obtaining that private history.
Git line-ending conversion is disabled by `.gitattributes` so those exact-byte
hashes survive a checkout on Windows or Linux. Avoid automatic whole-tree
line-ending conversion when editing a small feature.
