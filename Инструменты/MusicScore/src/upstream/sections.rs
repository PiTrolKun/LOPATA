//! The sections a score names in its `% label` comments, and the lyrics
//! skeleton they give: each section mapped to the tag the lyrics use, and a
//! section in which the voice sings nothing left without a tag.

use super::abc;
use super::Q;

/// SheetSage2's section labels to lyrics tags; anything else is a verse.
pub const TAGS: [(&str, &str); 23] = [
    ("intro", "Intro"),
    ("verse", "Verse"),
    ("pre-chorus", "Pre-Chorus"),
    ("chorus", "Chorus"),
    ("post-chorus", "Chorus"),
    ("bridge", "Bridge"),
    ("outro", "Outro"),
    ("rap", "Verse"),
    ("interlude", "Interlude"),
    ("instrumental", "Instrumental"),
    ("solo", "Instrumental"),
    ("fade-out", "Outro"),
    ("pre-outro", "Bridge"),
    ("loop", "Chorus"),
    ("intro and verse", "Verse"),
    ("pre-chorus and chorus", "Chorus"),
    ("verse and pre-chorus", "Verse"),
    ("theme", "Verse"),
    ("development", "Bridge"),
    ("variation", "Verse"),
    ("irregular", "Verse"),
    ("preshot", "Intro"),
    ("silence", "Intro"),
];

pub fn tag_of(label: &str) -> &'static str {
    let lower = label.to_lowercase();
    TAGS.iter().find(|(name, _)| *name == lower).map(|(_, tag)| *tag).unwrap_or("Verse")
}

#[derive(Clone, Debug, PartialEq)]
pub struct Section {
    pub label: String,
    pub tag: &'static str,
    /// In quarter notes from the start.
    pub start: Q,
    /// The voice notes that begin inside it.
    pub notes: usize,
}

fn bar_count(line: &str) -> usize {
    let body = line.trim_end();
    let body = body.strip_suffix('|').unwrap_or(body);
    body.split('|')
        .map(|bar| {
            let bar = bar.trim();
            match bar {
                "Z" => 1,
                "Z2" => 2,
                "Z3" => 3,
                "Z4" => 4,
                _ => 1,
            }
        })
        .sum()
}

/// Each section in order; music before the first comment is a section labelled ''.
pub fn sections(text: &str) -> Result<Vec<Section>, String> {
    let score = abc::parse(text)?;
    let vocal = &score.voices[abc::VOCAL];
    let mut found: Vec<Section> = Vec::new();
    let mut pending: Vec<String> = Vec::new();
    let mut bar_index = 0usize;
    for (index, line) in abc::split_lines(text).into_iter().enumerate() {
        if let Some(label) = line.strip_prefix("% ") {
            pending.push(label.trim().to_string());
            continue;
        }
        if score.music_lines.get(&index) != Some(&abc::VOCAL) {
            continue;
        }
        if bar_index < vocal.bars.len() && (!pending.is_empty() || found.is_empty()) {
            let label = pending.last().cloned().unwrap_or_default();
            found.push(Section { tag: tag_of(&label), label, start: vocal.bars[bar_index].start, notes: 0 });
        }
        pending.clear();
        bar_index += bar_count(line);
    }
    for position in 0..found.len() {
        let start = found[position].start;
        let end = found.get(position + 1).map(|next| next.start);
        found[position].notes = vocal.notes.iter().filter(|note| note.start >= start && end.is_none_or(|end| note.start < end)).count();
    }
    Ok(found)
}

/// Lyrics with only the tags: one per section the voice sings in.
pub fn skeleton(found: &[Section]) -> String {
    found
        .iter()
        .filter(|section| section.notes > 0)
        .map(|section| format!("[{}]\n", section.tag))
        .collect::<Vec<_>>()
        .join("\n")
        .trim_end_matches('\n')
        .to_string()
}
