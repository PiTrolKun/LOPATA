"""Strict song/mode/plan-bound MP4 commits for the Any Song H3 adapter.

No tensor runtime is imported here. A completed receipt is the commit point;
an orphan MP4 from a terminated process is never accepted as completed work.
"""
import hashlib
import json
import math
from pathlib import Path
import secrets
import os

from song_clip_motion_state import atomic_json, canonical, contained, file_hash, reject_links


FORMAT = "LOPATA.SongClip.Montage/1"
FPS = 24
MAX_DOCUMENT = 16 * 1024**2
MAX_CLIP = 8 * 1024**3


def object_hash(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def read_document(path):
    reject_links(path)
    if not 0 < Path(path).stat().st_size <= MAX_DOCUMENT:
        raise ValueError("Invalid montage checkpoint document size.")
    return json.loads(Path(path).read_text(encoding="utf-8"))


def finite_number(value, minimum=0):
    return (not isinstance(value, bool) and isinstance(value, (int, float))
            and math.isfinite(value) and value >= minimum)


def validate_plan(plan, duration, references):
    if (not isinstance(plan, dict) or type(plan.get("version")) is not int or plan["version"] != 1
            or plan.get("framesPerSecond") != FPS
            or not isinstance(plan.get("inputHash"), str) or len(plan["inputHash"]) != 64
            or not isinstance(plan.get("globalStyle"), str) or not plan["globalStyle"].strip()
            or len(plan["globalStyle"]) > 12000
            or not isinstance(plan.get("shots"), list) or not 1 <= len(plan["shots"]) <= 1024):
        raise ValueError("Invalid montage plan header.")
    end = 0.0
    for index, shot in enumerate(plan["shots"], 1):
        if (not isinstance(shot, dict) or type(shot.get("index")) is not int or shot["index"] != index
                or not finite_number(shot.get("startSeconds"))
                or not finite_number(shot.get("durationSeconds"), .000001)
                or shot["durationSeconds"] > 15.001
                or abs(shot["startSeconds"] - end) > .000001
                or not isinstance(shot.get("prompt"), str) or not shot["prompt"].strip()
                or len(shot["prompt"]) > 12000
                or type(shot.get("performerVisible")) is not bool
                or shot.get("transition") not in ("fresh", "match", "continue")
                or not isinstance(shot.get("referenceIndices"), list)
                or any(type(item) is not int or not 1 <= item <= len(references) for item in shot["referenceIndices"])
                or len(set(shot["referenceIndices"])) != len(shot["referenceIndices"])):
            raise ValueError("Invalid montage shot or reference binding.")
        if index == 1 and shot["transition"] != "fresh":
            raise ValueError("The first montage shot cannot refer to a previous frame.")
        if not shot["performerVisible"] and any(references[item - 1]["kind"] == "performer" for item in shot["referenceIndices"]):
            raise ValueError("Performer references must not leak into B-roll.")
        if not isinstance(shot.get("shotSize", "medium"), str) or not isinstance(shot.get("cameraAngle", "eye_level"), str):
            raise ValueError("Invalid montage framing description.")
        if abs(shot["startSeconds"] * FPS - round(shot["startSeconds"] * FPS)) > .00001:
            raise ValueError("Montage cut boundaries must use the actual 24 fps grid.")
        end = shot["startSeconds"] + shot["durationSeconds"]
        if index < len(plan["shots"]) and abs(end * FPS - round(end * FPS)) > .00001:
            raise ValueError("Montage cut boundaries must use the actual 24 fps grid.")
    if abs(end - duration) > .00001:
        raise ValueError("The montage plan must cover the complete original song.")
    return plan["shots"]


def shot_frames(shot):
    start = round(shot["startSeconds"] * FPS)
    end = math.ceil((shot["startSeconds"] + shot["durationSeconds"]) * FPS - 1e-7)
    count = end - start
    raw = max(22, 5 + 17 * max(0, math.ceil((count - 5) / 17)))
    if count < 1:
        raise ValueError("A montage shot has no output frames.")
    return {"index": shot["index"], "startFrame": start, "outputFrames": count, "rawFrames": raw}


class MontageState:
    def __init__(self, job, identity, requested_seed, read_only=False):
        if type(requested_seed) is not int or not -1 <= requested_seed < 2**53:
            raise ValueError("Invalid montage seed.")
        self.root = Path(job).absolute()
        reject_links(self.root)
        chunks = contained(self.root, "montage-clips")
        if read_only:
            if not self.root.is_dir() or not chunks.is_dir() or not contained(self.root, "montage-state.json").is_file():
                raise ValueError("An existing montage state is required for read-only verification.")
        else:
            self.root.mkdir(parents=True, exist_ok=True)
            chunks.mkdir(exist_ok=True)
        self.request_hash = object_hash(identity)
        path = contained(self.root, "montage-state.json")
        if path.exists():
            facts = read_document(path)
            if (facts.get("format") != FORMAT or facts.get("requestHash") != self.request_hash
                    or facts.get("identity") != identity or type(facts.get("seed")) is not int
                    or not 0 <= facts["seed"] < 2**53):
                raise ValueError("Montage inputs, mode, component pins or plan changed.")
            self.seed = facts["seed"]
            if requested_seed >= 0 and requested_seed != self.seed:
                raise ValueError("Montage checkpoint seed changed.")
        else:
            self.seed = secrets.randbits(53) if requested_seed == -1 else requested_seed
            facts = {"format": FORMAT, "requestHash": self.request_hash, "identity": identity, "seed": self.seed}
        self.input_hash = object_hash({"requestHash": self.request_hash, "seed": self.seed})
        if path.exists() and facts.get("inputHash") != self.input_hash:
            raise ValueError("Montage checkpoint identity is invalid.")
        if not path.exists():
            if read_only:
                raise ValueError("Read-only montage verification cannot create a checkpoint.")
            atomic_json(path, {**facts, "inputHash": self.input_hash})

    def _paths(self, index):
        if type(index) is not int or not 1 <= index <= 1024:
            raise ValueError("Invalid montage shot index.")
        return (contained(self.root, f"montage-clips/{index:06d}.mp4"),
                contained(self.root, f"montage-clips/{index:06d}.json"))

    def shot_identity(self, shot, previous_hash):
        if not isinstance(previous_hash, str) or len(previous_hash) != 64:
            raise ValueError("Invalid preceding montage checkpoint hash.")
        return {"format": FORMAT, "inputHash": self.input_hash, **shot_frames(shot),
                "shotSha256": object_hash(shot), "seed": (self.seed + shot["index"] - 1) % 2**53,
                "previousHash": previous_hash}

    def load(self, shot, previous_hash, verify):
        data, receipt = self._paths(shot["index"])
        if not receipt.exists():
            return None
        facts = read_document(receipt)
        if (set(facts) != {"identity", "data", "media", "route"}
                or facts["identity"] != self.shot_identity(shot, previous_hash)
                or not isinstance(facts["data"], dict)
                or facts["data"].get("file") != data.relative_to(self.root).as_posix()
                or type(facts["data"].get("size")) is not int
                or not 0 < facts["data"]["size"] <= MAX_CLIP or not data.is_file()
                or data.stat().st_size != facts["data"]["size"]
                or file_hash(data) != facts["data"].get("sha256")
                or verify(data, facts["identity"]) != facts["media"]):
            raise ValueError("A completed montage MP4 is corrupt or belongs to different inputs.")
        return facts

    def commit(self, shot, previous_hash, write, verify, route):
        data, receipt = self._paths(shot["index"])
        if receipt.exists():
            raise ValueError("A completed montage shot must never be overwritten.")
        identity = self.shot_identity(shot, previous_hash)
        staging = data.with_name(data.stem + "." + secrets.token_hex(12) + ".partial.mp4")
        try:
            write(staging)
            reject_links(staging)
            if not 0 < staging.stat().st_size <= MAX_CLIP:
                raise ValueError("Montage MP4 exceeds its file budget.")
            with staging.open("rb+") as stream:
                os.fsync(stream.fileno())
            facts = {"identity": identity, "data": {"file": data.relative_to(self.root).as_posix(),
                     "size": staging.stat().st_size, "sha256": file_hash(staging)},
                     "media": verify(staging, identity), "route": route}
            reject_links(data)
            os.replace(staging, data)
            atomic_json(receipt, facts)
            return facts
        finally:
            if staging.exists():
                staging.unlink()

    def data_path(self, facts):
        return contained(self.root, facts["data"]["file"])

    def files(self, facts):
        return [{"path": item.relative_to(self.root).as_posix(), "size": item.stat().st_size,
                 "sha256": file_hash(item)} for item in self._paths(facts["identity"]["index"])]
