import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

TOOLS=Path(__file__).resolve().parents[2]/'Исходники/AIHub/Tools'
sys.path.insert(0,str(TOOLS))
import song_clip_geek as worker
from song_clip_geek_state import GeekState
from song_clip_geek_native import NativeGeek


class GeekTests(unittest.TestCase):
    def test_prior_adapters_preserve_only_matching_checkpoints(self):
        current = {'request': 'original', 'adapters': {
            'song_clip_geek.py': 'current-worker',
            'song_clip_geek_native.py': 'current-native',
            'song_clip_geek_state.py': 'current-state'}}
        prior = {**current, 'adapters': {**current['adapters'], **worker.PRIOR_ADAPTERS}}
        self.assertEqual(current, worker.checkpoint_identity(current, current))
        self.assertEqual(prior, worker.checkpoint_identity(prior, current))
        with self.assertRaises(ValueError):
            worker.checkpoint_identity({**prior, 'request': 'changed'}, current)
        with self.assertRaises(ValueError):
            worker.checkpoint_identity({**prior, 'adapters': {
                **prior['adapters'], 'song_clip_geek_state.py': 'changed'}}, current)

    def test_changed_input_cannot_adopt_saved_cards(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'a.png').write_bytes(b'accepted')
            state=GeekState(root,{'input':'original'});state.commit('cards',0,'prompt',['a.png'])
            with self.assertRaises(ValueError):GeekState(root,{'input':'changed'})

    def test_resume_checks_artifact_bytes_and_prompt(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'a.png').write_bytes(b'accepted')
            state=GeekState(root,{'input':'original'});state.commit('cards',0,'prompt',['a.png'])
            saved=GeekState(root,{'input':'original'})
            self.assertIsNotNone(saved.accepted('cards',0,'prompt'))
            with self.assertRaises(ValueError):saved.accepted('cards',0,'different-prompt')
            (root/'a.png').write_bytes(b'changed')
            with self.assertRaises(ValueError):saved.accepted('cards',0,'prompt')

    def test_orphan_output_is_not_a_commit_and_order_is_required(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'partial.png').write_bytes(b'orphan')
            state=GeekState(root,{})
            self.assertIsNone(state.accepted('cards',0,'p'))
            with self.assertRaises(ValueError):state.commit('cards',1,'p',['partial.png'])
            with self.assertRaises(ValueError):state.commit('cards',0,'p',['../escaped.png'])

    def test_extra_artifacts_from_another_song_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'a').write_bytes(b'a')
            state=GeekState(root,{});state.commit('clips',0,'p',['a'])
            with self.assertRaises(ValueError):state.validate_counts(1,0)

    def test_card_uses_only_the_reference_mentioned_in_that_scene(self):
        refs=[dict(file='performer.png',kind='performer',description='singer'),dict(file='world.png',kind='world',description='room')]
        plan=dict(globalStyle='Natural light with <Subject 1>.',scenes=[dict(prompt='<Subject 2> contains a cat.')],
            referenceRoles=[dict(index=1,kind='performer',description='the singer'),dict(index=2,kind='world',description='the room')])
        prompt,ref=worker.card_spec({'references':refs},plan,0)
        self.assertEqual('world.png',ref['file']);self.assertNotIn('<Subject',prompt)
        plan['scenes'][0]['prompt']='A cat walks through fog.'
        self.assertIsNone(worker.card_spec({'references':refs},plan,0)[1])
        self.assertIn('completed final action',worker.card_spec({'references':refs},plan,1)[0])

    def test_unapproved_upstream_graph_nodes_cannot_execute(self):
        native=NativeGeek.__new__(NativeGeek);native.cache={};native.graph={'x':{'class_type':'LoadRemoteCode','inputs':{}}}
        with self.assertRaisesRegex(ValueError,'Unapproved'):native.evaluate('x')

    def test_graph_accepts_native_new_and_legacy_node_interfaces(self):
        class Modern:
            @classmethod
            def execute(cls, text): return (text + '! ',)
        class Legacy:
            FUNCTION='invoke'
            def invoke(self, text): return (text + '?',)
        native=NativeGeek.__new__(NativeGeek);native.cache={}
        native.classes={'CLIPTextEncode':Modern,'ImageScale':Legacy}
        native.graph={'a':{'class_type':'CLIPTextEncode','inputs':{'text':'value'}},
                      'b':{'class_type':'ImageScale','inputs':{'text':['a',0]}}}
        self.assertEqual(('value! ?',),native.evaluate('b'))

    def test_reference_role_and_partial_scene_plan_are_mandatory(self):
        analysis={'durationSeconds':10,'slots':[dict(index=1,startSeconds=0,endSeconds=10)]}
        request={'references':[],'visualPlan':dict(version=1,globalStyle='Film.',assistants=[{}],scenes=[dict(index=1,startSeconds=0,endSeconds=10,prompt='A cat.')])}
        self.assertIsNotNone(worker.validate_plan(request,analysis))
        request['visualPlan']['scenes']=[]
        with self.assertRaises(ValueError):worker.validate_plan(request,analysis)


if __name__=='__main__':unittest.main()
