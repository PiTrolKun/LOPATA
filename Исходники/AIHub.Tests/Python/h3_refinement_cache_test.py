"""CPU-only cache integrity and interrupted-publication regressions."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

try:
    import torch
    import safetensors
except ImportError:
    torch = None

spec = importlib.util.spec_from_file_location("h3_refinement_cache",
    Path(__file__).parents[2] / "AIHub" / "Tools" / "h3_refinement_cache.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


@unittest.skipIf(torch is None, "Use the installed managed Python with its pinned safetensors library.")
class RefinementCheckpoint(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.cache = module.RefinementCache(self.temp.name, "a" * 64, 123)
        self.video = torch.arange(12, dtype=torch.float32).reshape(1, 1, 3, 2, 2)
        self.audio = torch.arange(8, dtype=torch.float32).reshape(1, 2, 4)

    def test_roundtrip_cpu_and_committed_segments_are_immutable(self):
        self.assertIsNone(self.cache.load(0))
        self.cache.store(0, self.video, self.audio)
        restored = self.cache.load(0)
        self.assertTrue(torch.equal(restored["video"], self.video))
        self.assertTrue(torch.equal(restored["audio"], self.audio))
        self.assertEqual(str(restored["video"].device), "cpu")
        files = self.cache.files(0)
        self.assertEqual(len(files), 2)
        self.assertTrue(all(f["RelativePath"].startswith("refinement-cache/") and "\\" not in f["RelativePath"]
                            and f["Size"] > 0 and len(f["Sha256"]) == 64 for f in files))
        with self.assertRaisesRegex(ValueError, "H3InvalidResult"):
            self.cache.store(0, self.video + 1, self.audio)
        self.assertTrue(torch.equal(self.cache.load(0)["video"], self.video))

    def test_tampered_data_never_loads(self):
        self.cache.store(0, self.video, self.audio)
        with (self.cache.root / "00000.safetensors").open("ab") as file:
            file.write(b"corrupted")
        with self.assertRaisesRegex(ValueError, "H3InvalidResult"):
            self.cache.load(0)

    def test_identity_and_seed_must_match(self):
        self.cache.store(0, self.video, self.audio)
        for identity, seed in (("b" * 64, 123), ("a" * 64, 124)):
            with self.assertRaisesRegex(ValueError, "H3InvalidResult"):
                module.RefinementCache(self.temp.name, identity, seed).load(0)

    def test_interrupted_receipt_commit_can_be_repeated(self):
        replace = module.os.replace
        def fail_receipt(source, target):
            if str(target).endswith(".json"):
                raise OSError("simulated power failure before receipt commit")
            replace(source, target)
        with patch.object(module.os, "replace", fail_receipt):
            with self.assertRaises(OSError):
                self.cache.store(0, self.video, self.audio)
        self.assertIsNone(self.cache.load(0))
        self.assertEqual(list(self.cache.root.glob("*.tmp")), [])
        self.cache.store(0, self.video, self.audio)
        self.assertTrue(torch.equal(self.cache.load(0)["video"], self.video))

    def test_bad_or_missing_receipt_data_is_not_accepted(self):
        self.cache.store(0, self.video, self.audio)
        (self.cache.root / "00000.safetensors").unlink()
        with self.assertRaisesRegex(ValueError, "H3InvalidResult"):
            self.cache.load(0)
        (self.cache.root / "00000.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "H3InvalidResult"):
            self.cache.load(0)

    def test_shared_cpu_storage_is_saved_without_aliases(self):
        self.cache.store(0, self.video, self.video)
        restored = self.cache.load(0)
        self.assertTrue(torch.equal(restored["video"], restored["audio"]))

    def test_invalid_index_and_hash_never_construct_paths(self):
        for index in (-1, 20000, "../other", True):
            with self.assertRaisesRegex(ValueError, "H3InvalidResult"):
                self.cache.load(index)
        with self.assertRaisesRegex(ValueError, "H3InvalidResult"):
            module.RefinementCache(self.temp.name, "not-a-hash", 123)


if __name__ == "__main__":
    unittest.main()
