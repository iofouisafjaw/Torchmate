# MIT licensing and publication status

On 2026-10-23 the project owner selected the [MIT License](LICENSE) for the
current project's own code, documentation, tests and original artwork, including
`assets/companion-icon.svg` and its generated PNG icons. The copyright notice is
`Copyright (c) 2026 TerrarAI contributors`; it identifies the contributors
collectively and does not transfer ownership or replace third-party copyrights.

Third-party components retain their individual licenses and notices in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `licenses/`. The project MIT
license does not relicense third-party code, model weights, game software or
textures, Wiki content, trademarks, or historical upstream material for which
permission has not been obtained. It grants only rights the contributors hold.

Version 0.10.30 replaces or removes the active implementation inherited from
[SlothfulDreams/terrar.ai](https://github.com/SlothfulDreams/terrar.ai), commit
`3a0f316962332cf3abab922f247ffa589766362c`, and replaces both inherited icons.
See [the replacement record](docs/source-replacement.md) and
[file/hash evidence](release/source-provenance.json).

The pinned upstream tree had no license-named file, GitHub reported no detected
license, and the license endpoint returned 404 on 2026-10-20. No separate
permission was obtained. Those historical facts remain recorded; replacement
does not grant permission for the old code, icons, Git history or private kits.
The implementer had access to that source, so this is not a clean-room claim.
Source comparison and passing tests document the technical replacement; they
are not a legal clearance opinion.

The license choice is complete; publication approval is a separate workflow
decision, not an additional restriction on the MIT grant for covered material.
On 2026-10-23 the owner authorized public GitHub publication of the current
allowlisted, history-free source snapshot. This approval excludes old Git history,
private preparation archives and Windows binary releases. Player gameplay
acceptance and a clean Windows installation remain pending; the binary release
gates remain closed. No Steam Workshop publication is approved.

The inherited Git tree also tracks `.env`. Its contents were not read or
exported by this work. Do not push the existing history as a shortcut: old
source, assets and private files remain there. A future public source export
must be reviewed independently of this private checkout. Allowlisted preparation
archives exclude Git history, `.env`, snapshots, old binaries, local models,
runtime files, personal logs and saves. Ignore rules do not sanitize history.
