"""Real codec/PNG checks, opt-in via the installed audited media libraries."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Исходники/AIHub/Tools'))
import song_clip_geek as worker

@unittest.skipUnless(all(importlib.util.find_spec(name) for name in ('av','torch','PIL','numpy')), 'Installed media runtime required.')
class GeekMediaTests(unittest.TestCase):
    def test_handoff_comes_from_last_surviving_mp4_frame_and_provenance_is_embedded(self):
        import av
        import numpy as np
        import torch
        from PIL import Image
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'clip.mp4'
            tensor=torch.zeros((9,64,64,3));tensor[:5,:,:,0]=.3;tensor[5:,:,:,2]=1
            facts={'program':'LOPATA','programVersion':'test-build','scenario':'song-to-clip/geekatplay','operation':'codec-test','models':{'SDXL':'pin'}}
            worker.encode_shot(tensor,path,5,64,64,facts)
            last=worker.last_encoded_frame(path,5,torch)
            with av.open(str(path)) as video:
                frames=list(video.decode(video=0))
                self.assertEqual(5,len(frames));expected=frames[-1].to_ndarray(format='rgb24')
                self.assertEqual(facts,json.loads(video.metadata['LOPATA_VIDEO']))
            png=Path(directory)/'handoff.png';worker.save_png(last,png,facts)
            with Image.open(png) as image:
                self.assertEqual(facts,json.loads(image.info['LOPATA']))
                self.assertLessEqual(np.abs(np.array(image).astype(int)-expected.astype(int)).max(),1)
                self.assertLess(np.array(image)[:,:,2].mean(),10,'The trimmed raw blue tail must not leak into the handoff.')
            with self.assertRaises(ValueError):worker.last_encoded_frame(path,9,torch)

    def test_nonfinite_frames_cannot_be_committed_as_video_or_png(self):
        import torch
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);bad=torch.full((1,64,64,3),float('nan'))
            with self.assertRaises(ValueError):worker.encode_shot(bad,root/'clip.mp4',1,64,64,{})
            with self.assertRaises(ValueError):worker.save_png(bad,root/'card.png',{})
            self.assertFalse((root/'clip.mp4').exists());self.assertFalse((root/'card.png').exists())
