"""Adapter contract checks with fake upstream APIs: no weights, native runtimes or synthesis."""
import importlib.util
import pathlib
import sys
import tempfile
import types
import unittest
import json
from unittest.mock import patch
sys.dont_write_bytecode = True

SOURCE = pathlib.Path(__file__).parents[2] / 'Исходники/AIHub/Tools/VideoNarrators/primary_engines.py'
sys.path.insert(0, str(SOURCE.parent))


class AdapterContract(unittest.TestCase):
    def run_adapter(self, engine, style=''):
        calls = {}
        class Wave:
            def reshape(self, shape): return [0.0, 0.1]
        class TTS:
            sample_rate = 24000
            voice_style_names = ['M1']
            def __init__(self, **kwargs): calls['load'] = kwargs
            def get_voice_style(self, speaker): return speaker
            def synthesize(self, text, **kwargs): calls['generate'] = dict(text=text, **kwargs); return Wave(), [1]
        class Qwen:
            @staticmethod
            def from_pretrained(model, **kwargs): calls['load'] = dict(model=model, **kwargs); result=Qwen();result.model=types.SimpleNamespace(device=kwargs['device_map'], talker=types.SimpleNamespace(generate=lambda: None));return result
            def generate_custom_voice(self, **kwargs): calls['generate'] = kwargs; return [[0.0, 0.1]], 24000
        class Kitten:
            sample_rate=24000
            available_voices=['Bruno']
            last_report = {'chunks': [{'terminated': True}]}
            def __init__(self, model, **kwargs):calls['load']=dict(model=model,**kwargs);self.device=kwargs['device']
            def generate(self,text,**kwargs):calls['generate']=dict(text=text,**kwargs);return [0.0,0.1]
            def close(self):calls['closed']=True
        modules = {'soundfile': types.SimpleNamespace(write=lambda *args, **kw: calls.update(write=(args, kw))),
                   'supertonic': types.SimpleNamespace(TTS=TTS),
                   'qwen_tts': types.SimpleNamespace(Qwen3TTSModel=Qwen),
                   'kitten_tts_cpp':types.SimpleNamespace(KittenTTS=Kitten),
                   'torch': types.SimpleNamespace(float32='f32',set_num_threads=lambda n:None)}
        loader = types.ModuleType('supertonic.loader'); loader.DEFAULT_ONNX_PROVIDERS=['CPUExecutionProvider']
        supertonic = types.ModuleType('supertonic'); supertonic.__path__=[]; supertonic.TTS=TTS; supertonic.loader=loader
        modules['supertonic']=supertonic; modules['supertonic.loader']=loader
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory); request=root/'request.json'; output=root/'output.wav'
            request.write_text(json.dumps(dict(engine=engine,model=str(root/'model'),text='Тест',language='ru',speaker='Bruno' if engine=='kitten2' else 'M1' if engine=='supertonic3' else 'Ryan',rate=100,steps=8,style=style,preset='expressive')),encoding='utf-8')
            spec=importlib.util.spec_from_file_location('voice_worker', SOURCE); worker=importlib.util.module_from_spec(spec);spec.loader.exec_module(worker)
            class Report:
                actual = {}
                def loaded(self, **facts): calls['actual']=facts
            with patch.dict(sys.modules, modules):
                synthesize=worker.load(json.loads(request.read_text(encoding='utf-8')), 'cpu', Report())
                try: synthesize(str(output))
                finally:
                    if hasattr(synthesize,'close'): synthesize.close()
        return calls

    def test_supertonic_uses_local_bundle_explicit_language_and_native_controls(self):
        calls=self.run_adapter('supertonic3')
        self.assertFalse(calls['load']['auto_download'])
        self.assertEqual('supertonic-3',calls['load']['model'])
        self.assertEqual(('ru',1.0,8,'M1'),tuple(calls['generate'][k] for k in ('lang','speed','total_steps','voice_style')))
        self.assertEqual('PCM_16',calls['write'][1]['subtype'])

    def test_qwen_large_uses_customvoice_and_style_without_remote_model_resolution(self):
        calls=self.run_adapter('qwen3-17b','calm')
        self.assertTrue(calls['load']['local_files_only'])
        self.assertEqual(('Russian','Ryan','calm'),tuple(calls['generate'][k] for k in ('language','speaker','instruct')))

    def test_qwen_small_does_not_pretend_to_support_instructions(self):
        calls=self.run_adapter('qwen3-06b')
        self.assertNotIn('instruct',calls['generate'])

    def test_kitten_uses_official_native_cpu_bundle_offline_and_closes_it(self):
        calls=self.run_adapter('kitten2')
        self.assertTrue(calls['load']['offline']);self.assertEqual('cpu',calls['load']['device'])
        self.assertEqual('student_w4',calls['load']['decoder']);self.assertEqual('expressive',calls['generate']['preset'])
        self.assertTrue(calls['closed'])


if __name__ == '__main__': unittest.main()
