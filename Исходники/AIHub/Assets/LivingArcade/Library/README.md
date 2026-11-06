# LOPATA — Living Arcade asset library

Prepared PNGs are 256 × 256 transparent RGBA. Originals are byte-for-byte copies; do not overwrite them to add provenance.

`catalog.json` records source authors, licenses, URLs, source SHA-256 and per-frame SHA-256. `files.json` is the integrity manifest for every bundled file except itself.

Six source packs are CC0-1.0. Original license files and saved source evidence are in `licenses/`. The Glitch CC0 release excludes its logos and trademarks.

The `research` pack contains selected original non-franchise Gemma outputs; it is not third-party CC0 art. Actual model revision and purpose are embedded in PNG metadata and catalog records.

Kenney atomic entries are parts, not full enemies. Twenty additional characters are deterministic assemblies of original parts, with no model authorship claimed.

Jamie run/climb/rope and Blue Bird jump/walk/eat cycles replay author-drawn poses. Other static art remains static; bob/sway are optional runtime transforms and must not be described as genuine walk cycles.

The library supplements live generation. No franchise imitations, ripped game assets, scripts, or failed model attempts are used by the runtime. Hidden SVG editor reference layers are removed only from transient developer render copies; original author files remain unchanged.

Rebuild with the developer tool `Инструменты/LivingArcade/build_asset_library.py`, an existing Pillow environment and the pinned resvg-0.47.0 executable. No Pillow/resvg dependency is added to the game runtime.
