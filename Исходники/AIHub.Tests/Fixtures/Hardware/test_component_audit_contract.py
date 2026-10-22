"""No models/imports: prove explicit prepared handoff skips only component hashes."""
import hashlib
from pathlib import Path
import runpy
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(sys.argv.pop(1)).resolve()
LORA = runpy.run_path(str(TOOLS / 'music_lora_runtime.py'))
GIGA = runpy.run_path(str(TOOLS / 'music_lyrics_gigaam.py'))

class PreparedComponentContracts(unittest.TestCase):
    def test_giga_prepared_requires_file_and_standalone_checks_digest(self):
        with tempfile.TemporaryDirectory() as folder:
            checkpoint = Path(folder) / 'v3_ctc.ckpt'
            request = {'weights': str(checkpoint), 'component_audit_prepared': True}
            with self.assertRaises(ValueError):
                GIGA['verify_checkpoint'](request)
            checkpoint.write_bytes(b'corrupt checkpoint')
            with patch.object(hashlib, 'file_digest', side_effect=AssertionError('Hash was opened in prepared mode')):
                self.assertEqual(checkpoint, GIGA['verify_checkpoint'](request))
            del request['component_audit_prepared']
            with self.assertRaisesRegex(ValueError, 'integrity'):
                GIGA['verify_checkpoint'](request)
            request['component_audit_prepared'] = 'true'
            with self.assertRaisesRegex(ValueError, 'integrity'):
                GIGA['verify_checkpoint'](request)

    def test_lora_prepared_requires_every_file_and_standalone_checks_digest(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            use = {'audit_prepared': True, 'files': [{'name': 'weights.safetensors', 'sha256': '0'*64}]}
            with self.assertRaisesRegex(RuntimeError, 'missing'):
                LORA['verify_component_files'](root, use)
            (root / 'weights.safetensors').write_bytes(b'corrupt adapter')
            with patch.object(hashlib, 'file_digest', side_effect=AssertionError('Hash was opened in prepared mode')):
                LORA['verify_component_files'](root, use)
            del use['audit_prepared']
            with self.assertRaisesRegex(RuntimeError, 'hash mismatch'):
                LORA['verify_component_files'](root, use)
            use['audit_prepared'] = True
            use['files'][0]['name'] = '../weights.safetensors'
            with self.assertRaisesRegex(RuntimeError, 'Invalid'):
                LORA['verify_component_files'](root, use)

if __name__ == '__main__':
    unittest.main()
