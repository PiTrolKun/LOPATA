"""Bind the reviewed speech catalog to runtime keys; never modify generated audio."""
import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path


def group_for(event, entry):
    context = event.get("original") or {}
    text = event["id"] + " " + entry["en"] + " " + str(context.get("literal", "")) + " " + str(context.get("context", ""))
    if re.search(r"fail|error|exception|crash|corrupt|invalid|incompat|not.found|missing|unable|could.not|out.of.memory|memory_low|not.enough|insufficient|unsupported|timeout|context_full|lost|denied", text, re.I):
        return 1
    if re.search(r"model\.(cache_|cpu$|gpu$|first_token|prefill|offload|checkpoint|context_compress|token_limit)", event["id"], re.I):
        return 3
    if re.search(r"ready|complete|finish|saved|exported|cancel|stop|paused|waiting|started|loading|preparing|busy|running|connecting|download", text, re.I):
        return 2
    if event["id"].startswith(("model-log:", "worker:")) or "log" in str(context.get("context", "")).lower():
        return 3
    return 2


def build(root, source):
    registry = json.loads((source / "LOPATA_реестр_уведомлений_и_статусов.json").read_text(encoding="utf-8-sig"))
    dictionaries = {lang: json.loads((root / f"Исходники/AIHub/Localization/{lang}.json").read_text(encoding="utf-8-sig")) for lang in ("ru", "en")}
    clips, events, templates = [], [], []
    for entry in registry["entries"]:
        clips.append({"Id": entry["clip_id"], "Texts": {lang: entry[lang] for lang in ("ru", "en")}})
        for event in entry["events"]:
            event_id = event["id"]
            group = group_for(event, entry)
            # Stable event metadata controls policy; translators cannot raise a package's priority.
            events.append({"Id": event_id, "ClipId": entry["clip_id"], "Group": group})
            if event_id.startswith("localization:"):
                key = event_id.split(":", 1)[1]
                for lang, dictionary in dictionaries.items():
                    if key in dictionary:
                        templates.append({"EventId": event_id, "Language": lang, "Text": dictionary[key]})
            literal = (event.get("original") or {}).get("literal")
            if literal and len(literal.strip()) >= 12 and not event_id.startswith("dynamic:"):
                templates.append({"EventId": event_id, "Language": "", "Text": literal})
        primary = next((e["id"] for e in entry["events"] if e["id"].startswith("localization:")), entry["events"][0]["id"])
        for lang in ("ru", "en"):
            templates.append({"EventId": primary, "Language": lang, "Text": entry[lang]})
    unique = {(t["Language"], t["Text"], t["EventId"]): t for t in templates}
    result = {"Schema": 1, "Clips": clips, "Events": events, "Templates": list(unique.values())}
    output = root / "Исходники/AIHub/Assets/VoiceNotifications"
    output.mkdir(parents=True, exist_ok=True)
    (output / "catalog.json").write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    pins = {}
    for lang in ("ru", "en"):
        original = source / f"LOPATA_уведомления_и_статусы_{lang}.zip"
        destination = output / f"{lang}.zip"
        shutil.copyfile(original, destination)
        pins[lang] = hashlib.sha256(destination.read_bytes()).hexdigest()
    (output / "bundles.json").write_text(json.dumps(pins, indent=2), encoding="utf-8")
    print(json.dumps({"clips": len(clips), "events": len(events), "templates": len(unique), "bundles": pins}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    args = parser.parse_args()
    build(args.root, args.source)
