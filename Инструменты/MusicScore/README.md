# LOPATA score helper

CPU-only stdin/stdout JSON adapter around the pinned Studio ABC parser and
notation writer. No model, network, shell execution or MIDI round trip.
Build: `cargo build --release --offline --locked`.

Upstream files abc.rs, notation.rs, edits.rs, sections.rs and schedule.rs are unmodified copies from
YuE2-Studio c3e214f3a65f3f56c18e107127ebd2ebad5150ad, score module.
mod.rs retains its Q implementation and changes only the module declarations.
Studio copyright/license is preserved in LICENSE-Studio.txt. The upstream
score modules credit pytraveler/YuE2-ComfyUI (Apache-2.0); include Apache-2.0
and dependency notices with the deployed helper. Own adapter: LOPATA GPL-3.0.
Mirror edits use the upstream writer and verify unchanged sounding notes/grid.

Deployed package: Runtime/MusicScore/score-c3e214f3-3 (immutable).
Built offline with Rust 1.95.0 (59807616e 2026-04-14), committed Cargo.lock.
The package includes its source snapshot, upstream licenses and notices for
25 dependency crates. The application checks the executable SHA256 before use.
Operations: inspect, mark (compatible Words/Keep stamp), mirror (chords only),
schedule (upstream lyric scheduling eligibility). No model code is modified.
