# Pinned official ACE-Step source

`ca1e85fe9430179831e6bc6be790c332190a3866` contains the unmodified `acestep/`
tree, `LICENSE`, and `pyproject.toml` from the same official Git commit:
https://github.com/ace-step/ACE-Step-1.5/tree/ca1e85fe9430179831e6bc6be790c332190a3866.
Files were extracted as original Git blob bytes. `manifest.json` records the
revision and SHA256 of every source file; MusicAceSource additionally pins
the digest of this manifest. No weights, wheels or generated caches are here.

LOPATA's separate `Tools/music_ace_worker.py` adapter supplies verified managed
paths, offline download policy, the official API request, progress and receipts.
It wraps the official text tokenizer to report its conditioning limits instead
of silently truncating words. It does not replace any model inference logic.

The optional training/UI source remains part of the original source bundle;
their optional libraries and resources are not automatically installed.
The 88 wheel artifacts and exact weight files are pinned separately in
`Исходники/AIHub/Tools/ace-xl-models.json`. All their downloads are handled by
LOPATA's managed library and license gate. The API uses the existing managed
PyTorch hardware runtime with an isolated library overlay.

See `Документы_проекта/ACE_XL_Turbo_4B_паспорт.md` for component identities,
hardware boundaries, user request mapping and verification status.

Delivery: run Инструменты/MusicAce/pack-source.ps1 once to build source.zip.
The archive is pinned by size and SHA256, retains all original source bytes,
and is extracted/verified in AppData/Music/AceSource/<revision>. Its protected
models package is never exposed as a loose updater installation path.
