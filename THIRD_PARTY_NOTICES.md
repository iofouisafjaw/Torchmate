# Third-party notices

The project's own code, documentation, tests and original icons use the [MIT License](LICENSE), Copyright (c) 2026 TerrarAI contributors. The components below retain their own copyrights and terms; the root license does not supersede them. Publication preparation remains subject to the workflow status in [LICENSE_STATUS.md](LICENSE_STATUS.md).

| Component | Use and disposition | License / evidence |
|---|---|---|
| SlothfulDreams/terrar.ai, `3a0f316962332cf3abab922f247ffa589766362c` | Historical starting point. Active inherited implementations removed/replaced in 0.10.30; both PNGs replaced by our code-native mark. See `docs/source-replacement.md` and `release/source-provenance.json` | No historical permission granted; [source](https://github.com/SlothfulDreams/terrar.ai/tree/3a0f316962332cf3abab922f247ffa589766362c) |
| Quality of Terraria / ImproveGame, `5f25aad630df6bc0a700771deb333bb47b4c05ef` | Adapted `Helpers/PlaceStyleHelper.cs` into `Content/Systems/Vendor/QotPlaceStyle.cs`; native tile names retained and fallback adapted | MIT, Copyright (c) 2022 局长; full [license](licenses/ImproveGame-MIT.txt), also retained in the source header |
| llama.cpp `b11609` | Downloaded separately from its official release; no executables or DLLs in the preparation kit | [MIT text](licenses/llama.cpp-MIT.txt), [upstream](https://github.com/ggml-org/llama.cpp/blob/b11609/LICENSE) |
| Official Qwen3 14B / 8B GGUF | Separately downloaded weights with commit, size and SHA-256 pinned; no model weights in the preparation kit | [Apache-2.0 text](licenses/Qwen3-Apache-2.0.txt); per-model URLs in `release/dependencies.lock.json` |
| NVIDIA CUDA runtime and graphics driver | Optional external CUDA dependencies; not repackaged or relicensed as MIT | [NVIDIA terms](https://docs.nvidia.com/cuda/eula/index.html); the pinned CUDA asset is fetched from the official llama.cpp release |
| Python standard library | User installs Python 3.11+ x64 separately; no Python runtime bundled | [Python license](https://docs.python.org/3/license.html); scripts require no pip packages |
| Terraria, tModLoader and optional game mods | Installed by the player. Uses game-provided rendering, APIs and live data; no game DLLs or textures copied into the kit | Respective owners' terms; soft integrations are not third-party assembly redistribution |
| Terraria / Calamity Wiki | Optional local corpus. Importers retain page citations, source/version/rights metadata | Not bundled; source-specific license applies. Do not apply the code license to Wiki text or caches |

The prior Terra / TerraGuardians source review was design reference only. It is distinct from the inherited `terrar.ai` code and from the actual ImproveGame vendor adaptation listed above. No TerraGuardians assets or assemblies are included.

The NPC references a game-provided Goblin Scout texture at runtime; no game texture is copied into the kit. The unused inherited player-renderer class was removed. The chat portrait is drawn by code. Both PNG icons now come from `assets/companion-icon.svg`, rendered by `scripts/create_icons.py` without the inherited PNGs as input. The current original SVG and generated PNGs are covered by the root MIT license; historical icons are not. Original and replacement hashes are preserved in `release/source-provenance.json`.

Steam Workshop clearance is separate from a GitHub release. The local model service invokes external software; [current tModLoader rules](https://store.steampowered.com/workshopeula/1281930/) require review before any Workshop distribution. This preparation does not claim an exception.
