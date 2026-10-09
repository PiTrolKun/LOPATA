# DiffRhythm 2 — воспроизводимость и проверки

Приложение вызывает закреплённый API авторов, без переустановки глобального Python.
Архив `Runtime/MusicDiffRhythm/source.zip` содержит исходники Space, MuQ/Jieba,
оригинальные notices и `notices/DEPENDENCIES.json` с полным pip report:
версии, URL и SHA256 исходных wheels/sdists. Torch из этого report не поставляется
overlay: он используется из штатного аппаратного комплекта 2.10.

Продуктовый состав загрузки — `Исходники/AIHub/Tools/diffrhythm2-models.json`;
каталог извлечённых файлов — `diffrhythm2-python-files.json`, встроенный ресурс
`AIHub.DiffRhythmPythonManifest`. Общий генератор каталога:
`Инструменты/prepare-ace-file-manifest.py --help` (формат применяется и к DiffRhythm).
Нельзя повторно разрешать зависимости на текущую дату и считать их прежним комплектом.

Для воспроизведения исходного архива на Windows/Python 3.12:

```powershell
python Инструменты/MusicDiffRhythm/rebuild-source.py --stage _tmp/diff2-rebuild
```

Рецепт скачивает только небольшие закреплённые исходники, MuQ/Jieba sdists,
проверяет каждый файл и итоговый SHA256 архива. Модельные веса не скачиваются.
Файлы продукта не перезаписываются. Notices берутся из проверенного поставляемого
архива. Итоговый SHA256 должен быть
`1a5ed6b66b125a7a69ac7867391fc619b30be29ffa07c78d1fb8e884c1bcacf8`.

`verify-frontends.py --stage <developer-stage>` использует `source/`, `overlay/`
и `models.json` подготовленного технического стенда. При отсутствии китайского
G2P ONNX он явно скачивает **только этот вспомогательный файл**, сверяя SHA256.
Веса генератора песни не загружаются. Тест проверяет 20 русских строк, совпадение
EN/ZH с авторским frontend, запрет неизвестных фонем, отключение RU и маркеры.
Это проверка обработки текста, а не качества получившегося вокала.

Python-проверки запускаются через управляемый Python 3.12/Torch 2.10 с `-I -B`.
`PHONEMIZER_ESPEAK_LIBRARY` указывает на штатную eSpeak DLL,
`ESPEAK_DATA_PATH` — на её `espeak-ng-data`. Без правильного data path Windows
eSpeak способен аварийно завершиться; пользователь не настраивает эти пути вручную.

Windows bootstrap отложенно открывает неиспользуемые голоса: upstream eagerly
создаёт Japanese/MBROLA даже для EN/ZH. Русский eSpeak adapter находится отдельно
в `Tools/music_diffrhythm_frontend.py`. Генерация и BigVGAN не переписаны.
При изменении адаптера обновлять его происхождение в метаданных и паспорт.

Три необязательные лицензии добавляются без изменения прежних terms:

```powershell
./Инструменты/prepare-license-catalog.ps1 -AppendEntries Исходники/AIHub/Licenses/music-diffrhythm-entries.json
```

Повторное добавление существующих ID отклоняется. Команда не пересоздаёт лицензии
установщика. Уведомления всех пакетов сохранены в `Licenses/texts/diff2-*`.
MuQ/MuLan weights — **CC BY-NC 4.0**; оригинальный LangSegment fork недоступен,
его BSD/py3langid provenance указан отдельно, без заявления о завершённом аудите.

Практические ограничения и результаты: [паспорт](../../Документы_проекта/DiffRhythm2_паспорт.md).
