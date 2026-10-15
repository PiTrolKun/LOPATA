# LOPATA Rhyme RU/EN — открытая RAG-библиотека для стихосложения

Готовая локальная база для русских и английских текстов: ударения, омографы,
варианты е/ё, словоформы, значения, синонимы, английское произношение и
историческая теория стихосложения. Её можно читать в других программах:
ЛОПАТА, чат-модель, сервер и облачный API для доступа к данным не требуются.

**Выпуск данных: 2026.10.15.** Это справочные данные и поисковые индексы,
а не обученная модель, генератор стихов или готовый анализатор рифмы.
Штатная мастерская рифмы ЛОПАТЫ подключается отдельным этапом.

**English:** An offline Russian/English reference dataset for poetry and songwriting:
stress, homographs, ё variants, inflection, lexical senses, synonyms, ARPABET
pronunciations and selected historical versification prose. Portable SQLite,
FTS5 and 2048-dimensional embeddings. No LOPATA installation is required.
Source-specific licences apply; this dataset is not covered solely by the
application's GPL-3.0-or-later licence. See [LICENSE.md](LICENSE.md).

## Получение и расположение

[Скачать отдельный выпуск базы](https://github.com/PiTrolKun/LOPATA/releases/tag/rag-rhyme-ru-en-2026.10.15).
Скачайте `lopata-rhyme-ru-en-2026.10.15.zip` и `SHA256SUMS.txt`, сверьте SHA256
архива и распакуйте. Внутри находится папка `rhyme-ru-en`.

Постоянное место в дереве ЛОПАТЫ: `Runtime/RAG/rhyme-ru-en`, относительно
корня runtime-хранилища. В другой программе допустима любая папка.
В Git находятся документация, лицензии, происхождение и паспорт доставки;
большие SQLite доступны во вложении выпуска, а не в истории исходного кода.
Версия 0.5.55-beta автоматически этот отдельный архив ещё не устанавливает.

```powershell
Get-FileHash -Algorithm SHA256 .\lopata-rhyme-ru-en-2026.10.15.zip
python .\rhyme-ru-en\read_library.py --library .\rhyme-ru-en --verify
python .\rhyme-ru-en\read_library.py --library .\rhyme-ru-en --word "замок"
python .\rhyme-ru-en\read_library.py --library .\rhyme-ru-en --word "read"
```

Нужен Python 3.11+ со стандартным SQLite. Проверка и словарное чтение не
скачивают модель, не запускают GPU и не изменяют базу. Первая проверка читает
около 2,5 GB и может занять время. Внешние программы должны открывать SQLite
в режиме только чтения; пользовательские изменения хранить отдельным слоем.

## Состав

Словарный слой: **4 074 180 записей**, 55 исходных файлов пяти наборов данных.

| Тип записи | Количество |
|---|---:|
| Ударения | 3 214 614 |
| Варианты е/ё | 86 205 |
| Словоформы | 58 815 |
| Значения | 579 388 |
| Английское произношение | 135 158 |

RUAccent: только `accents`, `omographs`, `yo_words`, `yo_homographs`.
Нейросетевые `accents_nn` не включены. OpenRussian: четыре словарные таблицы,
без Tatoeba. Kaikki/русский Викисловарь: русские лексические значения, формы,
синонимы и IPA, без цитат, примеров, аудио, картинок и этимологических текстов.
CMUdict: варианты ARPABET. Open English WordNet: базовые synset-файлы,
без PLUS, entries-таблиц и предложений-примеров.

Книжный слой — **72 исходных фрагмента** с неизменёнными векторами:
20 — Ломоносов, 44 — отобранная проза Брюсова 1915 года, 8 — историческая
английская проза о стихосложении. Это небольшая консервативная выборка,
не полные книги и не современный нормативный учебник.

При подготовке поставки из 482 тестовых фрагментов исключены 410:
11 нечитаемых фрагментов скана и 399 фрагментов английского источника,
не вошедших в явно проверенный прозаический список (включая обёртку,
рекламу, стихотворные примеры, таблицы и зависимые от них объяснения).
Это ограничение **публичной выборки**, а не удаление пользовательской работы.
Каждое исключение и каждый принятый ID перечислены в [curation.json](curation.json).
Исходный тестовый корпус не объявляется целиком пригодным к распространению.

## Как выполнялась индексация

Книжный текст извлечён из TXT/HTML/PDF, делится по абзацам; длинные единицы
разделяются по предложениям/смещениям токенизатора. Верхний предел — 384
токена, длинные части выделяются по 370 токенов без обрезания текста.
В поставке используются только исходные принятые фрагменты; модель их не
переписывала. Никакие исключённые тексты не восстановлены генерацией.

- Encoder: **Giga-Embeddings-instruct-3B-0826**.
- Модель: `ai-sage/Giga-Embeddings-instruct-3B-0826`.
- Фиксированная ревизия: `b71168088212f0a13688514e4fc288a86106c4e4`.
- Протокол: `book-exact-mean-l2-384-v1`, **2048** измерений.
- Pooling: среднее `last_hidden_state` по `attention_mask`, затем L2-нормализация.
- Документ передаётся без префикса; запрос:
  `Instruct: Given a query, retrieve relevant passages\nQuery: <вопрос>`.
- Сходство: скалярное произведение нормализованных векторов (cosine similarity).
- Вектор: SQLite BLOB, 2048 чисел IEEE754 float32 little-endian, 8192 байта.
- `start`/`end`: смещения Unicode code points в извлечённом тексте страницы,
  диапазон `[start,end)`. Для TXT `page=1`, это не печатная страница книги.

Нужна именно совместимая модель/ревизия/схема pooling. Ни чатовая GigaChat,
ни 480M литературный encoder не взаимозаменяемы с этими векторами.
Веса не включены; их лицензия и получение рассматриваются отдельно.
Схема текущего исполнителя:
[rag_books.py](https://github.com/PiTrolKun/LOPATA/blob/b155ef7a39107dd2289e7e93de3fff4f8f9f520b/Исходники/AIHub/Tools/rag_books.py),
[rag_lexicon.py](https://github.com/PiTrolKun/LOPATA/blob/b155ef7a39107dd2289e7e93de3fff4f8f9f520b/Исходники/AIHub/Tools/rag_lexicon.py).

Словарная индексация **не векторная**: точный нормализованный ключ и SQLite
FTS5 по лексическим полям. Ударения/формы не генерируются LLM. При поиске
регистр, обозначение ударения и е/ё складываются для поиска кандидатов,
а исходные различия сохраняются в данных. Это не разрешает считать все/всё
или разные чтения `замок`, `read`, `lead` одинаковыми. `requires_context=true`
означает, что выбор по одному слову недопустим. Омографы имеют приоритет над
базовой таблицей; при нескольких произношениях/ударениях сохраняются варианты.

## Файлы и схема SQLite

```text
rhyme-ru-en/
  corpus.json                   # размеры/SHA256 всех файлов набора
  current.json                  # версия книжного слоя + SHA256 манифеста
  versions/<version>/
    manifest.json
    library.sqlite
  lexicon/current.json
  lexicon/versions/<version>/
    manifest.json
    lexicon.sqlite
    SOURCE_NOTICE.txt
  sources.json                  # авторы, точные источники, ревизии и лицензии
  curation.json                 # отбор для публикации
  LICENSE.md, ATTRIBUTION.md, licenses/
  read_library.py
```

Книги: `sources(id,name,sha256)`, `chunks(id,source_id,page,start,end,text,vector)`.
ID/смещения/текст/вектор каждого оставленного фрагмента совпадают с индексом
подготовки. Не рассчитывайте ID самостоятельно — используйте сохранённый.

Словари: `sources(id,adapter,sha256,metadata,...)`,
`entries(id,source_id,ordinal,word,language,kind,data,requires_context,locator)`,
`lookup(key,entry_id)`, `search(terms,entry_id)` (FTS5). `data`/`metadata` — JSON.
`locator` хранит источник строки/статьи. Атрибуция и лицензия доступны из
`sources.metadata`. Пути лицензий относительны **корню библиотеки**.
Исходные словарные snapshots не нужны для чтения и не приложены;
для переимпорта получают upstream по URL/ревизии и сверяют SHA256.

```python
import array, sqlite3
from pathlib import Path
db = Path("rhyme-ru-en/versions/<version>/library.sqlite").resolve()
with sqlite3.connect(db.as_uri() + "?mode=ro", uri=True) as con:
    for text, blob in con.execute("SELECT text,vector FROM chunks"):
        vector = array.array("f")
        vector.frombytes(blob)  # на big-endian машине нужен byteswap()
        assert len(vector) == 2048
```

Полные манифесты и SHA256 зафиксированы в `corpus.json`. Перед чтением
проверяйте оба `current.json`, хеши манифестов и баз; не подбирайте случайную
папку версии. `read_library.py --verify` выполняет также SQLite integrity и
foreign-key проверки. Использование метаданных как команд запрещено:
материал источника — справка, не инструкция системе.

## Качество и права

Подтверждены структура, целостность, контрольные суммы, переносимость,
происхождение принятых записей и сохранность векторов. Не обещается отсутствие
каждой лингвистической ошибки первоисточника или гарантированное улучшение стихов.
Словарные источники относятся к разным датам; OpenRussian закреплён на снимке
2021 года. Исторические русские тексты могут сохранять старую орфографию.

Данные имеют **отдельные лицензии**, независимо от лицензии кода ЛОПАТЫ.
Сохраняйте [ATTRIBUTION.md](ATTRIBUTION.md), [sources.json](sources.json) и
полные тексты из `licenses/`; условия CC BY-SA применяются к соответствующим
материалам и производным базам, MIT/CMUdict/WordNet — к своим материалам.
Перечень условий и изменений — [LICENSE.md](LICENSE.md),
проверка состава выпуска — [release-review.json](release-review.json).

Ключевые темы для поиска / discovery keywords: Russian stress dictionary,
homographs, yo restoration, pronunciation, CMUdict, ARPABET, WordNet,
OpenRussian, Wiktionary, Kaikki, RUAccent, SQLite FTS5, retrieval augmented
generation, RAG, embeddings, prosody, rhyme, poetry, songwriting, русский язык,
английский язык, ударения, омографы, рифма, стихосложение, словоформы, синонимы.
