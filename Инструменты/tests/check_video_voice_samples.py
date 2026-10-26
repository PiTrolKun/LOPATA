"""Independent RIFF/ID3 reader for the bundled narrator examples."""
import argparse
import array
import hashlib
import json
from pathlib import Path
import struct
import sys
import wave


def syncsafe(data):
    return sum(value << (7 * (3 - i)) for i, value in enumerate(data))


def provenance(raw):
    assert raw[:4] == b"RIFF" and raw[8:12] == b"WAVE"
    assert struct.unpack_from("<I", raw, 4)[0] + 8 == len(raw)
    pos = 12
    while pos + 8 <= len(raw):
        key, size = raw[pos:pos + 4], struct.unpack_from("<I", raw, pos + 4)[0]
        body = raw[pos + 8:pos + 8 + size]
        pos += 8 + size + (size & 1)
        if key != b"id3 ":
            continue
        assert body[:4] == b"ID3\x04"
        frame = 10
        while frame + 10 <= len(body):
            key, size = body[frame:frame + 4], syncsafe(body[frame + 4:frame + 8])
            data = body[frame + 10:frame + 10 + size]
            frame += 10 + size
            if key == b"TXXX" and data[0] == 3:
                field, value = data[1:].decode("utf-8").split("\0", 1)
                if field == "LOPATA_PROVENANCE":
                    return json.loads(value.rstrip("\0"))
    raise AssertionError("Missing embedded provenance")


def main():
    args = argparse.ArgumentParser()
    args.add_argument("directory", type=Path)
    args.add_argument("--report", type=Path, required=True)
    options = args.parse_args()
    entries = json.loads((options.directory / "samples.json").read_text(encoding="utf-8-sig"))
    unavailable = json.loads((options.directory / "unavailable.json").read_text(encoding="utf-8-sig"))
    assert len(entries) + len(unavailable) == 20
    successful_cases = {(e["id"], e["language"]) for e in entries}
    failed_cases = {(e["Id"], e["Language"]) for e in unavailable}
    assert len(successful_cases) == len(entries) and len(failed_cases) == len(unavailable)
    assert not (successful_cases & failed_cases)
    assert len(successful_cases | failed_cases) == 20
    assert ("vosk010", "ru") in successful_cases and ("vosk010", "en") in failed_cases
    assert len({e["id"] for e in entries} | {e["Id"] for e in unavailable}) == 10
    ids = {"kokoro-sveta", "kokoro-heart", "kitten2", "vosk010", "supertonic3", "qwen3-17b", "qwen3-06b", "orpheus3b", "silero56", "cosyvoice300"}
    assert successful_cases | failed_cases == {(model, language) for model in ids for language in ("ru", "en")}
    native = {"kokoro-sveta": "ru", "kokoro-heart": "en", "vosk010": "ru", "orpheus3b": "en", "silero56": "ru", "cosyvoice300": "en"}
    results = []
    for entry in entries:
        assert entry["language"] in ("ru", "en")
        assert entry["file"] == entry["id"] + "-" + entry["language"] + ".wav"
        if entry["id"] == "vosk010" and entry["language"] == "ru":
            assert entry["text"] == "Лопата для вас приготовила тестовую запись диктора. Лопата умеет говорить во многих сценариях."
            assert not any(c.isascii() and c.isalpha() for c in entry["text"])
        else:
            assert "Лопата" in entry["text"] and "LOPATA" in entry["text"]
        experimental = entry["id"] in native and entry["language"] != native[entry["id"]]
        assert entry["experimental"] == experimental
        assert entry["synthesisLanguage"] == (native[entry["id"]] if experimental else entry["language"])
        path = options.directory / entry["file"]
        raw = path.read_bytes()
        facts = provenance(raw)
        expected_version = "0.6.31-dev" if entry["id"] == "vosk010" else "0.6.30-dev"
        assert facts["Program"] == "LOPATA" and facts["ProgramVersion"] == expected_version
        assert facts["Scenario"] == "video-editor/narration"
        assert facts["Models"] == entry["models"]
        assert all(m["Revision"] for m in facts["Models"])
        expected_model = {"kokoro-sveta": "model-kokoro-ru-sveta", "kokoro-heart": "model-kokoro-82m-en-af-heart"}.get(entry["id"], "video-narrator-" + entry["id"])
        assert facts["Models"][0]["Id"] == expected_model, (entry["id"], facts["Models"][0]["Id"])
        with wave.open(str(path)) as sound:
            assert sound.getsampwidth() == 2 and sound.getcomptype() == "NONE"
            samples = array.array("h", sound.readframes(sound.getnframes()))
            if sys.byteorder != "little":
                samples.byteswap()
            peak = max(abs(v) for v in samples) / 32768
            rms = (sum((v / 32768) ** 2 for v in samples) / len(samples)) ** 0.5
            seconds = sound.getnframes() / sound.getframerate()
            assert peak > 0.001 and rms > 0.001 and 0.1 < seconds < 120
        results.append(dict(id=entry["id"], language=entry["language"], speaker=entry["speaker"],
                            seconds=seconds, peak=peak, rms=rms, sha256=hashlib.sha256(raw).hexdigest(), provenance=facts))
    options.report.write_text(json.dumps(dict(version="0.6.31-dev", samples=results, unavailable=unavailable), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"PASS: {len(results)} PCM16 examples, {len(unavailable)} explicit synthesis failures, 10 model cards; {sum(e['seconds'] for e in results):.1f} seconds total")


if __name__ == "__main__":
    main()
