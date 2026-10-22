#![allow(dead_code)]
mod upstream;
use serde_json::{json, Value};
use std::io::{self, Read};
use upstream::{abc, edits, notation};

fn execute(input: Value) -> Result<Value, String> {
    let raw = input["abc"].as_str().ok_or("Missing ABC")?;
    if raw.len() > notation::LONGEST { return Err("Music.Score.TooLarge".into()); }
    let edit = edits::read(raw);
    let mut text = edit.score;
    match input["operation"].as_str().unwrap_or("inspect") {
        "schedule" => {
            let lyrics=input["lyrics"].as_str().unwrap_or("").replace("\r\n","\n");
            let timed=upstream::schedule::schedule(&text,&lyrics).unwrap_or_default();
            return Ok(json!({"sections":timed.iter().map(|s|json!({"start_sec":s.start_sec,"lyric":[s.lyric.0,s.lyric.1]})).collect::<Vec<_>>()}));
        },
        "mark" => return Ok(json!({"abc": edits::attach(&text, Some(&edits::mark(
            input["style"].as_str().unwrap_or(""), input["lyrics"].as_str().unwrap_or(""),
            input["cot"].as_str().unwrap_or("full"))), input["keep"].as_bool().unwrap_or(false))})),
        "mirror" => {
            let sheet = notation::read(&text)?;
            let source = input["source"].as_u64().ok_or("Music.Score.SectionMismatch")? as usize;
            let targets = input["targets"].as_array().ok_or("Music.Score.SectionMismatch")?;
            let from = sheet.sections.get(source).ok_or("Music.Score.SectionMismatch")?;
            if from.bars == 0 { return Err("Music.Score.SectionMismatch".into()); }
            let source_start = sheet.bars[from.bar].start;
            let source_end = if from.bar + from.bars < sheet.bars.len() { sheet.bars[from.bar + from.bars].start } else { sheet.total };
            let chords: Vec<_> = sheet.chords.iter().filter(|c| c.start >= source_start && c.start < source_end).cloned().collect();
            if chords.is_empty() { return Err("Music.Score.NoChords".into()); }
            let mut wanted = serde_json::to_value(&sheet).map_err(|e| e.to_string())?;
            let mut changed = sheet.chords.clone();
            for target in targets {
                let at = target.as_u64().ok_or("Music.Score.SectionMismatch")? as usize;
                if at == source { return Err("Music.Score.SectionMismatch".into()); }
                let to = sheet.sections.get(at).ok_or("Music.Score.SectionMismatch")?;
                if to.bars != from.bars { return Err("Music.Score.SectionMismatch".into()); }
                for offset in 0..from.bars {
                    let a = &sheet.bars[from.bar + offset]; let b = &sheet.bars[to.bar + offset];
                    if a.length != b.length || a.meter != b.meter || a.key != b.key || !a.editable || !b.editable {
                        return Err("Music.Score.SectionMismatch".into());
                    }
                }
                let start = sheet.bars[to.bar].start;
                let end = start + source_end - source_start;
                changed.retain(|c| c.start < start || c.start >= end);
                changed.extend(chords.iter().map(|c| notation::SheetChord {start: start + c.start - source_start, name: c.name.clone()}));
            }
            changed.sort_by_key(|c| c.start);
            wanted["chords"] = serde_json::to_value(changed).map_err(|e| e.to_string())?;
            text = notation::write(&text, &wanted)?.abc;
            let check = notation::read(&text)?;
            if check.notes != sheet.notes || check.bars.iter().map(|b| (b.start,b.length,&b.key,&b.meter)).collect::<Vec<_>>() != sheet.bars.iter().map(|b| (b.start,b.length,&b.key,&b.meter)).collect::<Vec<_>>() {
                return Err("Music.Score.SectionMismatch".into());
            }
        },
        "inspect" => (),
        _ => return Err("Unsupported score operation".into())
    }
    // The original ABC is retained. No MIDI round trip and no model/runtime load.
    let sheet = notation::read(&text)?;
    abc::parse(&text)?;
    if sheet.seconds > 360.0 { return Err("Music.Score.TooLong".into()); }
    Ok(json!({"abc": edits::attach(&text, edit.words.as_deref(), edit.keep), "sheet": sheet}))
}
fn main() {
    let mut bytes = Vec::new();
    let answer = match io::stdin().take(1_000_001).read_to_end(&mut bytes) {
        Ok(_) if bytes.len() <= 1_000_000 => serde_json::from_slice::<Value>(&bytes).map_err(|e| e.to_string()).and_then(execute),
        _ => Err("Music.Score.TooLarge".into())
    };
    println!("{}", match answer { Ok(value) => json!({"ok":true,"result":value}), Err(error) => json!({"ok":false,"error":error}) });
}
