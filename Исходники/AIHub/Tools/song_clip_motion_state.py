"""Atomic, input-bound completed-chunk checkpoints for H3 music videos.

This module deliberately imports no model/tensor runtime. A receipt is the commit
point: an orphaned tensor file from an interrupted write is never a checkpoint.
"""
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import secrets


FORMAT = "LOPATA.SongClip.Motion/1"
MAX_JSON_BYTES = 1024 * 1024
MAX_LATENT_BYTES = 4 * 1024**3


def reject_links(path):
    for part in (Path(path).absolute(), *Path(path).absolute().parents):
        if part.is_symlink() or (part.exists() and getattr(part.lstat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Motion checkpoint paths must not contain links or reparse points.")


def contained(root, relative):
    root = Path(root).absolute()
    candidate = root / relative
    reject_links(candidate)
    candidate = candidate.resolve()
    if candidate == root.resolve() or not candidate.is_relative_to(root.resolve()):
        raise ValueError("Motion checkpoint path escapes its operation directory.")
    return candidate


def file_hash(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)


def object_hash(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def read_json(path):
    reject_links(path)
    if not 0 < Path(path).stat().st_size <= MAX_JSON_BYTES:
        raise ValueError("Invalid motion checkpoint document size.")
    return json.loads(Path(path).read_text(encoding="utf-8"))


def atomic_json(path, value):
    path = Path(path).absolute()
    reject_links(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_name(path.name + "." + secrets.token_hex(12) + ".tmp")
    try:
        with staging.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(canonical(value))
            stream.flush()
            os.fsync(stream.fileno())
        reject_links(path)
        os.replace(staging, path)
    finally:
        if staging.exists():
            staging.unlink()


def plan_chunks(total_frames, raw_frames=243, context_frames=39):
    for value in (total_frames, raw_frames, context_frames):
        if isinstance(value, bool) or not isinstance(value, int):
            raise ValueError("Motion frame counts must be integers.")
    if total_frames < 1 or raw_frames < 5 or (raw_frames - 5) % 17:
        raise ValueError("Motion raw frames must follow the H3 5 + 17*k grid.")
    if context_frames < 5 or (context_frames - 5) % 17 or context_frames >= raw_frames:
        raise ValueError("Motion context must be a shorter H3 5 + 17*k run.")
    advance = raw_frames - context_frames
    count = 1 + max(0, (total_frames - raw_frames + advance - 1) // advance)
    plans = [{"index": index + 1, "startFrame": index * advance, "rawFrames": raw_frames,
              "contextFrames": context_frames if index else 0} for index in range(count)]
    final = plans[-1]
    minimum = max(total_frames - final["startFrame"], final["contextFrames"] + 1, min(124, raw_frames))
    final["rawFrames"] = min(raw_frames, 5 + 17 * max(0, (minimum - 5 + 16) // 17))
    return plans


@contextmanager
def operation_lock(job):
    """OS-held lock is released by process death; no stale PID-file recovery."""
    path = contained(job, "motion.lock")
    with path.open("a+b") as stream:
        stream.seek(0, os.SEEK_END)
        if not stream.tell():
            stream.write(b"\0")
            stream.flush()
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


class MotionState:
    def __init__(self, job, identity, requested_seed):
        if (isinstance(requested_seed, bool) or not isinstance(requested_seed, int)
                or not -1 <= requested_seed < 2**53):
            raise ValueError("Invalid motion seed.")
        self.root = Path(job).absolute()
        reject_links(self.root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.chunks = contained(self.root, "motion-chunks")
        self.chunks.mkdir(exist_ok=True)
        self.request_hash = object_hash(identity)
        path = contained(self.root, "motion-state.json")
        if path.exists():
            facts = read_json(path)
            if (facts.get("format") != FORMAT or facts.get("requestHash") != self.request_hash
                    or facts.get("identity") != identity or not self._seed(facts.get("seed"))):
                raise ValueError("Motion inputs changed or the operation checkpoint is invalid.")
            self.seed = facts["seed"]
            if requested_seed >= 0 and self.seed != requested_seed:
                raise ValueError("Motion checkpoint seed does not match the requested seed.")
        else:
            self.seed = secrets.randbits(53) if requested_seed == -1 else requested_seed
            if not self._seed(self.seed):
                raise ValueError("Invalid motion seed.")
            facts = {"format": FORMAT, "requestHash": self.request_hash, "identity": identity, "seed": self.seed}
        self.input_hash = object_hash({"requestHash": self.request_hash, "seed": self.seed})
        if path.exists() and facts.get("inputHash") != self.input_hash:
            raise ValueError("Motion operation identity is invalid.")
        if not path.exists():
            atomic_json(path, {**facts, "inputHash": self.input_hash})

    @staticmethod
    def _seed(value):
        return isinstance(value, int) and not isinstance(value, bool) and 0 <= value < 2**53

    def chunk_identity(self, plan, previous_hash):
        if not re.fullmatch(r"[0-9a-f]{64}", previous_hash):
            raise ValueError("Invalid previous motion checkpoint hash.")
        return {"format": FORMAT, "inputHash": self.input_hash, **plan,
                "seed": (self.seed + plan["index"] - 1) % 2**53, "previousHash": previous_hash}

    def _paths(self, index):
        if not isinstance(index, int) or isinstance(index, bool) or index < 1:
            raise ValueError("Invalid motion chunk index.")
        name = f"motion-chunks/{index:06d}"
        return contained(self.root, name + ".safetensors"), contained(self.root, name + ".json")

    def load(self, plan, previous_hash):
        data, receipt = self._paths(plan["index"])
        if not receipt.exists():
            return None
        facts = read_json(receipt)
        expected = self.chunk_identity(plan, previous_hash)
        if (facts.get("identity") != expected or set(facts) != {"identity", "data", "tensors"}
                or not isinstance(facts["data"], dict)
                or set(facts["data"]) != {"file", "size", "sha256"}
                or facts["data"]["file"] != data.relative_to(self.root).as_posix()
                or not isinstance(facts["data"]["size"], int)
                or not 0 < facts["data"]["size"] <= MAX_LATENT_BYTES
                or not data.is_file() or data.stat().st_size != facts["data"]["size"]
                or file_hash(data) != facts["data"]["sha256"]):
            raise ValueError("A completed motion chunk is corrupt or belongs to different inputs.")
        return facts

    def commit(self, plan, previous_hash, write_tensors, verify_tensors):
        """Persist tensors, verify a fresh read, then atomically commit the receipt."""
        data, receipt = self._paths(plan["index"])
        if receipt.exists():
            raise ValueError("A completed motion checkpoint must not be overwritten.")
        identity = self.chunk_identity(plan, previous_hash)
        staging = data.with_name(data.name + "." + secrets.token_hex(12) + ".tmp")
        try:
            write_tensors(staging, identity)
            reject_links(staging)
            size = staging.stat().st_size
            if not 0 < size <= MAX_LATENT_BYTES:
                raise ValueError("Motion latent checkpoint exceeds its file budget.")
            with staging.open("rb+") as stream:
                os.fsync(stream.fileno())
            descriptors = verify_tensors(staging, identity)
            facts = {"identity": identity, "data": {"file": data.relative_to(self.root).as_posix(),
                     "size": size, "sha256": file_hash(staging)}, "tensors": descriptors}
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
        data, receipt = self._paths(facts["identity"]["index"])
        return [{"path": path.relative_to(self.root).as_posix(), "size": path.stat().st_size,
                 "sha256": file_hash(path)} for path in (data, receipt)]
