"""Verified CPU tensor checkpoints for completed H3 refinement segments."""
import hashlib
import json
import os
from pathlib import Path
import re
import secrets


class RefinementCache:
    LIMIT = 1024**3
    FORMAT = "LOPATA.H3.RefinementCache/1"

    def __init__(self, output, input_hash, seed):
        if not isinstance(input_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", input_hash):
            raise ValueError("VideoPrep.H3InvalidResult")
        self.root = Path(output).absolute() / "refinement-cache"
        self._links(self.root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.identity = {"Format": self.FORMAT, "InputHash": input_hash.lower(), "Seed": int(seed)}

    @staticmethod
    def _links(path):
        for part in (path, *path.parents):
            if part.is_symlink() or (part.exists() and getattr(part.lstat(), "st_file_attributes", 0) & 0x400):
                raise ValueError("VideoPrep.H3InvalidResult")

    def _paths(self, index):
        if not isinstance(index, int) or isinstance(index, bool) or not 0 <= index < 20000:
            raise ValueError("VideoPrep.H3InvalidResult")
        data = self.root / (f"{index:05d}.safetensors")
        receipt = self.root / (f"{index:05d}.json")
        self._links(data)
        self._links(receipt)
        return data, receipt, dict(self.identity, Index=index)

    @staticmethod
    def _hash(path):
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()

    def load(self, index):
        from safetensors import safe_open, SafetensorError
        data, receipt, identity = self._paths(index)
        if not receipt.exists():
            return None
        try:
            if receipt.stat().st_size > 4096:
                raise ValueError()
            facts = json.loads(receipt.read_text(encoding="utf-8"))
            if (set(facts) != {"Identity", "Size", "Sha256"} or facts["Identity"] != identity
                    or not isinstance(facts["Size"], int) or not 0 < facts["Size"] <= self.LIMIT
                    or data.stat().st_size != facts["Size"] or self._hash(data) != facts["Sha256"]):
                raise ValueError()
            with safe_open(str(data), framework="pt", device="cpu") as source:
                if set(source.keys()) != {"video", "audio"} or json.loads(source.metadata()["LOPATA_H3_REFINEMENT"]) != identity:
                    raise ValueError()
                return {key: source.get_tensor(key) for key in ("video", "audio")}
        except (OSError, ValueError, TypeError, KeyError, SafetensorError) as error:
            raise ValueError("VideoPrep.H3InvalidResult") from error

    def store(self, index, video, audio):
        from safetensors.torch import save_file
        data, receipt, identity = self._paths(index)
        if receipt.exists():
            raise ValueError("VideoPrep.H3InvalidResult")
        tensors = {"video": video.detach().cpu().contiguous().clone(), "audio": audio.detach().cpu().contiguous().clone()}
        if sum(t.numel() * t.element_size() for t in tensors.values()) + 4096 > self.LIMIT:
            raise ValueError("VideoPrep.H3InvalidResult")
        nonce = secrets.token_hex(12)
        staging = self.root / (f"{index:05d}.{nonce}.tmp")
        manifest = self.root / (f"{index:05d}.{nonce}.json.tmp")
        try:
            save_file(tensors, str(staging), metadata={"LOPATA_H3_REFINEMENT": json.dumps(identity)})
            with staging.open("rb+") as stream:
                os.fsync(stream.fileno())
            facts = {"Identity": identity, "Size": staging.stat().st_size, "Sha256": self._hash(staging)}
            with manifest.open("x", encoding="utf-8") as stream:
                json.dump(facts, stream)
                stream.flush()
                os.fsync(stream.fileno())
            self._links(data)
            self._links(receipt)
            os.replace(staging, data)
            os.replace(manifest, receipt)
        finally:
            for path in (staging, manifest):
                if path.exists():
                    path.unlink()

    def files(self, index):
        data, receipt, _ = self._paths(index)
        return [{"RelativePath": path.relative_to(self.root.parent).as_posix(),
                 "Size": path.stat().st_size, "Sha256": self._hash(path)} for path in (data, receipt)]
