"""Host-independent acoustic topology and unsupported-language regressions."""
import sys
import unittest
from pathlib import Path
import numpy as np

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Исходники/AIHub/Tools'))
from music_lyrics_ctc import refine, choose_path, normalized


class CtcTests(unittest.TestCase):
    def test_target_repeated_letters_require_separate_acoustic_occurrences(self):
        # Silence, 'а', blank, repeated 'а', then silence. A one-letter island
        # cannot be used twice to satisfy the same repeated source character.
        probs=np.full((10,2),-12.,dtype=np.float32)
        probs[:,1]=0
        probs[3,0]=0; probs[3,1]=-12
        probs[6,0]=0; probs[6,1]=-12
        bounds=refine(probs,[0,0],1,.1,20)
        self.assertAlmostEqual(bounds[0],20.3)
        self.assertAlmostEqual(bounds[1],20.7)

    def test_chronology_chooses_distinct_repeated_choruses(self):
        def node(start,end,cost=0):return {'start':start,'end':end,'cost':cost}
        rows=[{'index':0,'candidates':[node(5,7),node(15,17)]},
              {'index':1,'candidates':[node(8,10)]},
              {'index':2,'candidates':[node(5,7),node(15,17)]}]
        result=choose_path(rows)
        self.assertEqual([r['start'] for r in result],[5,8,15])

    def test_missing_row_does_not_receive_an_interpolated_interval(self):
        rows=[{'index':0,'candidates':[{'start':2,'end':3,'cost':0}]},
              {'index':1,'candidates':[]},
              {'index':2,'candidates':[{'start':8,'end':9,'cost':0}]}]
        result=choose_path(rows)
        self.assertEqual([r['index'] for r in result],[0,2])

    def test_foreign_letters_survive_normalization_for_explicit_unsupported_detection(self):
        self.assertEqual(normalized('Кот пое́т — Cat SINGS!'),'кот поет cat sings')


if __name__=='__main__':unittest.main()
