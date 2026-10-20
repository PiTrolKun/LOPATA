"""Contract checks without loading or replacing any model."""
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('ocr_worker', ROOT / 'Инструменты' / 'BookSmart' / 'ocr_worker.py')
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
FIXTURE = ROOT / 'Тесты' / 'BookSmartStand' / 'russian-two-columns.png'


class Contract(unittest.TestCase):
    def test_remote_server_and_credentials_are_rejected(self):
        for url in ('https://example.com/v1', 'http://example.com/v1', 'http://u:p@127.0.0.1/v1'):
            with self.assertRaises(ValueError):
                worker.validate({'sourcePath': str(FIXTURE), 'choice': 'ovis', 'serverUrl': url})

    def test_existing_image_and_local_server_are_accepted(self):
        source, url, page = worker.validate({'sourcePath': str(FIXTURE), 'choice': 'paddle', 'serverUrl': 'http://127.0.0.1:1234/v1/'})
        self.assertEqual(source, FIXTURE)
        self.assertEqual(url, 'http://127.0.0.1:1234/v1')
        self.assertEqual(page, 1)

    def test_upstream_visual_boxes_produce_only_original_crops(self):
        from PIL import Image
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'result.json'
            result = {'blocks': [{'id': 0, 'kind': 'image', 'bbox': [0, 0, 120, 160]}],
                      'markdown': '<img src="images/bbox_100_100_300_300.jpg" />', 'issues': []}
            worker.preserve_visual_regions(result, FIXTURE, 7, output)
            self.assertEqual(len(result['assets']), 2)
            self.assertEqual(result['assets'][1]['bbox'], [120, 160, 360, 480])
            with Image.open(result['assets'][0]['path']) as crop:
                self.assertEqual(crop.size, (120, 160))
            with Image.open(result['assets'][1]['path']) as crop:
                self.assertEqual(crop.size, (240, 320))
            self.assertEqual(result['assets'][0]['page'], 7)

    def test_truncation_stays_questionable_and_does_not_remove_footnotes(self):
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                data = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                self.server.last_request = data
                body = json.dumps({'choices': [{'message': {'content': 'Text\n\n[^1]: Footnote'}, 'finish_reason': 'length'}]}).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(body)
            def log_message(self, *args):
                pass
        with HTTPServer(('127.0.0.1', 0), Handler) as server:
            thread = threading.Thread(target=server.handle_request)
            thread.start()
            result = worker.ovis({}, FIXTURE, 'http://127.0.0.1:%s/v1' % server.server_port, 1, Path('.'))
            thread.join(5)
            self.assertTrue(result['blocks'][0]['questionable'])
            self.assertIn('Footnote', result['markdown'])
            self.assertFalse(result['confidenceAvailable'])
            self.assertEqual(server.last_request['temperature'], 0)
            self.assertEqual(server.last_request['messages'][0]['content'][1]['text'], worker.OVIS_PROMPT)


if __name__ == '__main__':
    unittest.main()
