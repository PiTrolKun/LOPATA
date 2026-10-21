"""Small regression contract for the single-string PEFT wrapper / multi-adapter tuner boundary."""
from pathlib import Path
import runpy
from types import SimpleNamespace
import unittest

runtime = runpy.run_path(str(Path(__file__).resolve().parents[2] / 'Исходники/AIHub/Tools/music_lora_runtime.py'))


class Decoder:
    def __init__(self):
        self.active_adapters = []
        self.base_model = SimpleNamespace(set_adapter=self.activate)
        self.peft_config = {}
        self.layers = []

    def set_adapter(self, name):
        if not isinstance(name, str):
            raise TypeError('PeftModel.set_adapter only accepts one name')

    def activate(self, names, inference_mode):
        self.active_adapters = names

    def enable_adapter_layers(self):
        pass

    def modules(self):
        return self.layers


class Handler:
    def __init__(self):
        self.model = SimpleNamespace(decoder=Decoder())
        self._active_loras = {}
        self.use_lora = False

    def add_lora(self, path, adapter_name):
        self._active_loras[adapter_name] = 1
        d = self.model.decoder
        d.peft_config[adapter_name] = SimpleNamespace(use_rslora=False)
        d.layers.append(SimpleNamespace(lora_A={adapter_name: object()}, lora_alpha={adapter_name: 64},
                                       r={adapter_name: 32}, scaling={adapter_name: 2}, disable_adapters=False))

    def set_use_lora(self, enable):
        self.use_lora = enable

    def set_lora_scale(self, name, strength):
        for m in self.model.decoder.layers:
            if name in m.scaling:
                m.scaling[name] = 2 * strength


class Contract(unittest.TestCase):
    def test_multiple_and_independent_strengths(self):
        uses = [dict(id='first', revision='test', strength=.4, directory=str(Path(__file__).parent), files=[]),
                dict(id='second', revision='test', strength=.8, directory=str(Path(__file__).parent), files=[])]
        handler = Handler()
        public = runtime['apply_ace_loras'](handler, uses)
        self.assertEqual(['first', 'second'], handler.model.decoder.active_adapters)
        self.assertNotIn('directory', public[0])
        handler.model.decoder.layers[0].scaling['first'] = 2
        with self.assertRaises(RuntimeError):
            runtime['verify_ace_loras'](handler, uses)

    def test_bypass_rejects_stale_activation(self):
        handler = Handler()
        self.assertEqual([], runtime['apply_ace_loras'](handler, []))
        handler.use_lora = True
        with self.assertRaises(RuntimeError):
            runtime['verify_ace_loras'](handler, [])


if __name__ == '__main__':
    unittest.main()
