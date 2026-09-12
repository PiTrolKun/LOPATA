# Запуск малого стенда

Из корня `H:\AI_HUB`, через Python `Runtime/Python/giga-embeddings/py312-torch210-transformers530/python.exe`:

1. `Тесты/JellyModels/edits/probe.py nuextract`
2. `Тесты/JellyModels/edits/prepare.py`
3. `Тесты/JellyModels/edits/probe.py gliner`
4. `Тесты/JellyModels/edits/probe.py runeweaver`
5. `Тесты/JellyModels/edits/audit.py`

Первые четыре шага уже выполнены. Они намеренно отказываются перезаписывать существующие каталоги/базу. Для новой серии создать отдельную копию кода и `cases.json` в другой папке рядом с этой, без `runs`, `.lopata`, `packets.json`, `mechanics.json`. Относительный импорт использует родительские адаптеры JellyModels. Последний шаг можно повторять: он только читает данные и проверяет синтаксис.

Используются уже установленные модели и зависимости, без скачивания. GPU-модели запускать последовательно. Процесс Runeweaver создаётся скрытым локальным сервером и завершается своим адаптером. Данные этого стенда синтетические. Отчёт: `REPORT.md`.
