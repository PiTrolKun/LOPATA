import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import download


DATA = bytes(range(256)) * 1024


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        start, end = map(int, self.headers['Range'][6:].split('-'))
        self.send_response(206)
        self.send_header('Content-Range', f'bytes {start}-{end}/{len(DATA)}')
        self.end_headers()
        self.wfile.write(DATA[start:end + 1])

    def log_message(self, *args):
        pass


class DownloadTests(unittest.TestCase):
    def test_ranges_resume_hash_and_existing_file(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        config = dict(filename='test.gguf', size=len(DATA), workers=4,
                      chunk_bytes=32768, sha256=hashlib.sha256(DATA).hexdigest())
        try:
            with tempfile.TemporaryDirectory() as folder:
                directory = Path(folder)
                parts = directory / 'test.gguf.parts'
                parts.mkdir()
                (parts / '00000.part').write_bytes(DATA[:777])
                url = f'http://127.0.0.1:{server.server_port}/model'
                target = download.download(config, directory, url)
                self.assertEqual(target.read_bytes(), DATA)
                self.assertFalse(parts.exists())
                self.assertEqual(download.download(config, directory, url), target)
                target.write_bytes(b'broken')
                with self.assertRaises(ValueError):
                    download.download(config, directory, url)
        finally:
            server.shutdown()
            server.server_close()
            worker.join()

    def test_ignored_range_never_written(self):
        class Response:
            status = 200
            headers = {}
            def __enter__(self): return self
            def __exit__(self, *args): pass
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'part'
            with patch('download.urllib.request.urlopen', return_value=Response()), \
                    patch('download.time.sleep'):
                with self.assertRaises(RuntimeError):
                    download.fetch_part('http://localhost/test', path, 0, 9, 10)
            self.assertFalse(path.exists())


if __name__ == '__main__':
    unittest.main()
