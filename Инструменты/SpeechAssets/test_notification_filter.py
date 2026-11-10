import unittest
from notification_filter import localized_reason, shorten, has_words
from build_notification_lists import markdown_literal, normalized_template


class NotificationFilterTests(unittest.TestCase):
    def test_documentation_and_controls_are_not_events_even_with_model_words(self):
        for key, ru, en in [
            ("Models.DownloadHint", "Можно загрузить модель для генерации текста.", "You can download a model for text generation."),
            ("Arcade.Preparation.Title", "Подготовка модели", "Model preparation"),
            ("Batch.ResumeHint", "Сохранённые разборы будут использованы повторно.", "Saved analyses will be reused."),
            ("Music.Rhyme.Settings.program.saved_candidates", "Сохраняемых кандидатов", "Saved candidates"),
            ("VideoPrep.ShotsSavedOptions", "Сохранённые варианты", "Saved alternatives"),
            ("VideoPrep.ShotsRemoveMissing", "Убрать недоступную связь", "Remove the unavailable link"),
        ]:
            with self.subTest(key=key):
                self.assertIsNotNone(localized_reason(key, ru, en, True))

    def test_real_failures_progress_and_model_states_remain(self):
        for key, ru, en in [
            ("Reader.OpenFailed", "Не удалось открыть документ.", "Failed to open the document."),
            ("Runtime.Downloading", "Скачиваем модель.", "Downloading the model."),
            ("Music.Generation.Completed", "Музыкальный файл сохранён.", "The music file has been saved."),
            ("Model.Error", "Недостаточно памяти.", "Out of memory."),
            ("SpeechAssets.Stopped", "Генерация остановлена. Готовые записи сохранены.", "Generation stopped. Completed recordings are saved."),
        ]:
            with self.subTest(key=key):
                self.assertIsNone(localized_reason(key, ru, en, True))

    def test_placeholder_only_and_incomplete_labels_cannot_be_spoken(self):
        for text in [".", "(`\\.`).", "{0}", "{0} / {1}", "https://example.org"]:
            with self.subTest(text=text):
                self.assertFalse(has_words(text))
                self.assertIsNotNone(localized_reason("Status.Value", text, text, True))
        self.assertIsNotNone(localized_reason("ProgressRemaining", "Осталось примерно.", "Approximately remaining.", True))

    def test_speech_keeps_event_without_dialog_instructions(self):
        text = "Статус: Модель загружена. Вы можете выбрать её по умолчанию в настройках."
        self.assertEqual("Модель загружена.", shorten(text))
        self.assertEqual("Не удалось открыть документ.", shorten("Не удалось открыть документ:"))
        self.assertEqual("", shorten("Состояние поиска и памяти:"))
        self.assertEqual("", shorten("Состояние."))
        self.assertEqual("Готово.", markdown_literal("Готово."))
        self.assertEqual("2\\. Завершено.", markdown_literal("2. Завершено."))
        self.assertIn("\\[", markdown_literal("Файл [готов]."))

    def test_dynamic_progress_is_not_a_period_and_model_is_not_mode(self):
        for key, ru, en in [
            ("AudioShell.Summary", "Готово: {0}. Не удалось: {1}.", "Completed: {0}. Failed: {1}."),
            ("AudioShell.Processing", "Обработка {0} из {1}…", "Processing {0} of {1}…"),
        ]:
            self.assertTrue(all(has_words(t) for t in normalized_template(key, ru, en)))
        self.assertEqual("This model is not supported.", shorten("This model is not supported."))
        self.assertEqual("Could not prepare components.", shorten("Could not prepare components:"))


if __name__ == "__main__":
    unittest.main()
