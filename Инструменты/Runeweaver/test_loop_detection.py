"""Regression checks on real failed streams and successful quotation/story controls."""
import unittest
from loop_lab import OUT, analyze


class LoopDetectionTests(unittest.TestCase):
    def test_observed_runeweaver_repetitions(self):
        for seed in [20,24,26,29,32,34]:
            with self.subTest(seed=seed):
                text = (OUT / f'rune_single_uncached_more/seed_{seed}.txt').read_text(encoding='utf-8')
                self.assertTrue(analyze(text)['literal_loop'])

    def test_changing_clock_values_are_not_an_escape(self):
        text = (OUT / 'alpha_reference/raw_thinking_seed_0.txt').read_text(encoding='utf-8')
        stats = analyze(text)
        self.assertFalse(stats['literal_loop'])
        self.assertTrue(stats['numeric_loop'])

    def test_successful_stories_and_deliberate_short_refrain_are_allowed(self):
        for path in (OUT / 'heldout_repeat110').glob('*.txt'):
            with self.subTest(path=path.name):
                self.assertFalse(analyze(path.read_text(encoding='utf-8'))['loop'])

    def test_degenerate_word_list_is_a_separate_signal(self):
        text = (OUT / 'rune_single_uncached/seed_0.txt').read_text(encoding='utf-8')
        stats = analyze(text)
        self.assertFalse(stats['loop'])
        self.assertGreater(stats['short_quoted_list_run'], 24)


if __name__ == '__main__':
    unittest.main()
