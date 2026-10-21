# ЛОПАТА — музыкальные LoRA: найденные адаптеры и применимость

**Дата исследования:** 18 октября 2026 года  
**Статус:** исследовательский справочник для будущих экспериментов; **не техническое задание**.  
**Цель:** сохранить обнаруженные музыкальные LoRA, первоисточники, различия базовых моделей и ограничения их использования в музыкальном сценарии ЛОПАТЫ.  
**Важно:** обнаружение адаптера на Hugging Face не означает, что он проверен на Windows, звучит лучше базы или уже поддерживается пользовательским интерфейсом ЛОПАТЫ.

## 1. Состав музыкального зоопарка ЛОПАТЫ

Состав сверялся с опубликованным кодом [`MusicModelVariants.cs`](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicModelVariants.cs) и каталогами исполнителей. **GitHub описывает опубликованное состояние; локальная DEV-версия может быть новее.**

| Семейство / вариант ЛОПАТЫ | Наличие сторонних LoRA | Степень готовности к использованию в ЛОПАТЕ |
|---|---|---|
| YuE2 3B Q8_0 | Есть жанровые, вокальные и управляющие адаптеры | Нужна проверка возможности подключения к закреплённому движку |
| YuE2 3B BF16 | Есть адаптеры семейства YuE2, потенциально интересные для PyTorch-пути | Нужна проверка совместимости контрольных слоёв, весов и формата |
| YuE2 Studio Q8_0 | Есть внешний рынок YuE2 LoRA; отдельно уже имеется служебная акустическая LoRA | Произвольные пользовательские LoRA в опубликованном запросе ЛОПАТЫ не реализованы |
| ACE-Step 1.5 XL Turbo 4B | Широкая коллекция жанровых LoRA, отдельные XL-кандидаты | В upstream есть инфраструктура LoRA; опубликованный адаптер ЛОПАТЫ не предоставляет пользовательский выбор |
| DiffRhythm 2 | Обнаружена исследовательская LoRA | Пока не выглядит практичной добавкой |
| HeartMuLa 3B happy-new-year | Есть сторонний инструментарий обучения/подключения LoRA | Готовая надёжная библиотека адаптеров не подтверждена |

**Важное различие:** стилевой LoRA для основной генеративной модели, вокальная LoRA, LoRA акустического декодера (NAR), LoHA/LoKr и обычный пресет параметров — разные сущности. Их нельзя свободно подменять друг другом.

## 2. YuE2 — жанры, вокал и управление звучанием

### 2.1. Particle Sliders — набор управляющих адаптеров

- **Страница:** [ntc-ai/yue2-particle-sliders](https://huggingface.co/ntc-ai/yue2-particle-sliders)
- **Описанная задача:** управление направлением звука через LoRA-подобные «ползунки».
- **Заявленные направления:** мужской/женский вокал; pop, hip-hop, R&B, indie rock, pop-punk, metal, country, folk, house, disco/funk, K-pop, reggaeton, afrobeats, lo-fi.
- **База по метаданным:** `m-a-p/YuE2-3B`.
- **Лицензия по карточке:** CC BY-NC 4.0.
- **Оговорка:** отдельные адаптеры — приближения исходных particle-настроек; одинакового результата с первоисточником ожидать не следует. Проверить форматы и загрузчик, прежде чем называть их совместимыми с YuE2 Q8/Studio ЛОПАТЫ.

### 2.2. GRVL — хрипловатый женский рок/соул-вокал

- **Страница:** [becausereasons/yue2-grvl-raspy-rock-soul](https://huggingface.co/becausereasons/yue2-grvl-raspy-rock-soul)
- **Характер:** rock, soul, pop-rock, R&B, power ballad, женский вокал с хрипотцой.
- **В предыдущем обзоре:** упоминались несколько вариантов выраженности и адаптация стадий AR/NAR; детали необходимо сверить по файлам/карточке при подготовке стенда.
- **Обозначенная база:** `Comfy-Org/YuE2`, поэтому прямую совместимость с закреплённой `m-a-p/YuE2-3B` нельзя автоматически гарантировать.
- **Лицензия по метаданным:** CC BY-NC 4.0.

### 2.3. Библиотека Atomtan Studio

- **Страница:** [atomtanstudio/lora-library](https://huggingface.co/atomtanstudio/lora-library)
- **Найденные направления:** dream-pop, old-school hip-hop, industrial / industrial metal / industrial dance.
- **Особенности:** под жанровые адаптеры могут требоваться определённые триггерные фразы; их нужно брать из карточек/примеры использования, а не угадывать.
- **База:** `m-a-p/YuE2-3B`.
- **Лицензия:** CC BY-NC 4.0.

### 2.4. Industrial Rock

- **Страница:** [monsterovich/yue2-industrial-rock-lora](https://huggingface.co/monsterovich/yue2-industrial-rock-lora)
- **Направление:** индустриальный рок.
- **База по метаданным:** `m-a-p/YuE2-3B`.
- **Примечание:** проверить влияние на русский вокал и стабильность композиции — наличие жанровой метки само по себе этого не гарантирует.

### 2.5. MLTNT — Reggae / Dancehall

- **Страница:** [becausereasons/yue2-mltnt-militant-reggae](https://huggingface.co/becausereasons/yue2-mltnt-militant-reggae)
- **Назначение:** мужской вокал, басовая и ритмическая стилистика reggae/dancehall, возможное смешение с речитативом/хип-хопом.
- **Оговорка:** количество вариантов, форматы и реальное качество требуют проверки карточки и демонстраций перед загрузкой.

### 2.6. Отдельная LoRA, уже входящая в YuE2 Studio ЛОПАТЫ

В [`MusicStudioRuntime.cs`](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicStudioRuntime.cs) уже закреплён служебный компонент:

- **Файл:** `nar_lora_joint_v9.safetensors`.
- **Размер:** 140 560 592 байта (~134 МиБ).
- **Источник:** [Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4](https://huggingface.co/Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4).
- **Роль:** `audio_decoder_adapter` — LoRA акустического декодера, а **не пользовательский стилевой/жанровый пресет**.

Наличие этого файла доказывает, что один специализированный LoRA-компонент уже предусмотрен в поставке Studio, но **не доказывает поддержку произвольной загрузки стилистических LoRA через интерфейс ЛОПАТЫ**.

### 2.7. Движок YuE2 и подключение LoRA

- Upstream: [ServeurpersoCom/yue2.cpp](https://github.com/ServeurpersoCom/yue2.cpp).
- В развитии движка имеются механизмы LoRA/нескольких адаптеров и задания силы влияния; **проверять на конкретной закреплённой версии**, а не по возможностям свежего `main`.
- Опубликованный [`MusicStudioWorker.cs`](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicStudioWorker.cs) отправляет параметры музыки и звука; пользовательских `lora_path`/`lora_scale` в этом контракте не видно.
- У BF16 и GGUF могут быть разные маршруты подключения/преобразования адаптера. Не считать LoRA от ComfyUI/PEFT автоматически совместимой с `yue2.cpp`.

## 3. ACE-Step 1.5 — большая подборка стилевых LoRA

**Базовая версия в ЛОПАТЕ:** ACE-Step 1.5 **XL Turbo 4B**, закреплённая в [`MusicAceCatalog.cs`](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicAceCatalog.cs). Исходный музыкальный pipeline — официальный PyTorch API, а не отдельный GGUF-движок.

### 3.1. Коллекция RyanOnTheInside

Найдены реальные репозитории под названиями `acestep1.5-xl-v1`:

| Название | Репозиторий | Заявленный стиль |
|---|---|---|
| Rock | [rock-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/rock-acestep1.5-xl-v1) | Рок |
| Progressive Rock | [progressive_rock-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/progressive_rock-acestep1.5-xl-v1) | Прогрессивный рок |
| Alternative Rock | [alternative_rock-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/alternative_rock-acestep1.5-xl-v1) | Альтернативный рок |
| Metalcore | [metalcore-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/metalcore-acestep1.5-xl-v1) | Металкор |
| Synth Pop | [synth_pop-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/synth_pop-acestep1.5-xl-v1) | Синтезаторная поп-музыка |
| Industrial | [industrial-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/industrial-acestep1.5-xl-v1) | Индастриал |
| Jazz | [jazz-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/jazz-acestep1.5-xl-v1) | Джаз |
| Phonk | [phonk-acestep1.5-xl-v1](https://huggingface.co/ryanontheinside/phonk-acestep1.5-xl-v1) | Фонк |

Также в прошлом обзоре отмечены возможные смежные направления автора — folk, punk, J-pop, hip-hop, R&B, dubstep. **Конкретные репозитории для них в данном каталоге не верифицированы**, поэтому перечислены только как направления дальнейшего поиска.

**Критически важно:** обозначение `xl` в названии — хороший ориентир, но метаданные этих карточек не подтверждают совместимость с **точным чекпойнтом XL Turbo ЛОПАТЫ**. Проверка конфигурации адаптера и пробной загрузки обязательна.

### 3.2. JAY 2 — работа с мужским вокалом

- **Страница:** [str8bored/JAY_2](https://huggingface.co/str8bored/JAY_2).
- **По метаданным действительно обозначена база:** `ACE-Step/acestep-v15-xl-turbo`.
- **Описание применения:** мужской вокал, структура исполнения, ретро-подача, фразировка.
- **Триггер из предыдущего обзора:** `jay_singing_runs` (сверить по актуальной карточке перед запуском).
- **Лицензия карточки:** CC0-1.0.
- **Статус:** особенно интересный кандидат именно для нашего XL Turbo, но реальное улучшение русского вокала не проверено.

### 3.3. Официальная праздничная LoRA

- **Страница:** [ACE-Step/ACE-Step-v1.5-chinese-new-year-LoRA](https://huggingface.co/ACE-Step/ACE-Step-v1.5-chinese-new-year-LoRA).
- **Назначение:** праздничный китайский музыкальный стиль.
- **База по метаданным:** `ACE-Step/Ace-Step1.5` — **не доказана прямая совместимость с XL Turbo**.
- **Лицензия карточки:** CreativeML OpenRAIL-M.

### 3.4. Русский поп и рэп — но другой базовый вариант

| Адаптер | Источник | Обязательная оговорка |
|---|---|---|
| Russian Pop LoRA | [kemendev/russian-pop-lora](https://huggingface.co/kemendev/russian-pop-lora) | Карточка прямо помечает базой `ACE-Step/Ace-Step1.5`, **не конкретный XL Turbo** |
| Russian Rap v3 | [ruslanmusinrusmus/russianrap-v3-lora](https://huggingface.co/ruslanmusinrusmus/russianrap-v3-lora) | LoRA для русского рэпа; точную базу/адаптерный формат дополнительно сверить с файлами |

Это примеры того, что модели для русского музыкального материала существуют, но перенос между вариантами ACE-Step нельзя объявлять безопасным без проверки слоёв и формата.

### 3.5. Pop / Electro LoHA для XL Base

- **Страница:** [Nekochu/ACE-Step-xl-base-pop-electro-lora](https://huggingface.co/Nekochu/ACE-Step-xl-base-pop-electro-lora).
- **База:** `ACE-Step/acestep-v15-xl-base`.
- **Тип:** LoHA/родственный низкоранговый адаптер (не обязательно обычная LoRA).
- **Статус:** не считать совместимым с XL Turbo; проверить, поддерживает ли выбранный загрузчик именно этот тип адаптера.

### 3.6. Есть ли инфраструктура LoRA в текущей ЛОПАТЕ?

В закреплённом дереве upstream ACE-Step присутствуют механизмы LoRA:

- [`acestep/training/lora_utils.py`](https://github.com/PiTrolKun/LOPATA/blob/main/Runtime/MusicAce/ca1e85fe9430179831e6bc6be790c332190a3866/acestep/training/lora_utils.py)
- [`acestep/core/generation/handler/lora_manager.py`](https://github.com/PiTrolKun/LOPATA/blob/main/Runtime/MusicAce/ca1e85fe9430179831e6bc6be790c332190a3866/acestep/core/generation/handler/lora_manager.py)
- [`acestep/api/http/lora_routes.py`](https://github.com/PiTrolKun/LOPATA/blob/main/Runtime/MusicAce/ca1e85fe9430179831e6bc6be790c332190a3866/acestep/api/http/lora_routes.py)

Но собственный [`MusicAceRequest.cs`](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicAceRequest.cs) и [`music_ace_worker.py`](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Tools/music_ace_worker.py) **не показывают готового пользовательского контракта LoRA**. То есть наличие библиотеки в составе runtime не равно готовой функции ЛОПАТЫ.

## 4. DiffRhythm 2

- **Найденный адаптер:** [m-a-a-p/DiffRhythm2-MixGRPO-LoRA](https://huggingface.co/m-a-a-p/DiffRhythm2-MixGRPO-LoRA).
- **База по метаданным:** `ASLP-lab/DiffRhythm2`.
- **Тип:** экспериментальное PEFT/LoRA-обучение для diffusion/flow-matching.
- **Результат из ранее найденного описания авторов:** устойчивого полезного прироста не подтверждено, на отдельных измерениях были ухудшения. Проверить оригинальный отчёт при новой оценке.
- **Вывод:** это исследовательский материал, а не доказанная улучшенная жанровая LoRA. Проводить внедрение только ради неё нецелесообразно.

## 5. HeartMuLa 3B

- **Сторонний инструмент:** [ryangroth5/heart-mula-lora](https://github.com/ryangroth5/heart-mula-lora).
- **Назначение:** механика обучения и применения LoRA к HeartMuLa.
- **Пример из предыдущего обзора:** небольшие адаптеры порядка 4 МБ при rank 8; конкретный объём зависит от набора изменяемых слоёв.
- **Состояние:** готовой проверенной библиотеки полезных публичных жанровых/вокальных LoRA для установленного в ЛОПАТЕ варианта `HeartMuLa-oss-3B-happy-new-year` в ходе поиска не обнаружено.
- **Вывод:** исследовательское направление на будущее, не текущий приоритет.

## 6. Для чего музыкальные LoRA полезны на практике

1. **Жанр и аранжировка:** усиление рок-, metal-, industrial-, jazz-, hip-hop- и иных стилевых признаков.
2. **Вокальная подача:** хрипотца, мужской/женский вокальный характер, артикуляция, интенсивность исполнения.
3. **Характер композиции:** управление ритмикой, звучанием инструментов и типовыми структурными решениями.
4. **Серии песен и альбомы:** более однородный стиль нескольких композиций при фиксированном адаптере и базовой модели.
5. **Экспериментальные параметры:** плавное смешение эффекта с помощью `scale`, если конкретный движок поддерживает такой контракт.

**Не гарантируется:** единая идентичность певца между всеми песнями, безошибочный русский вокал, стабильная структура песни или повышение музыкального качества. Для этих целей нужны отдельные слуховые и технические испытания, иногда специальные voice-адаптеры.

## 7. Возможный исследовательский стенд (не ТЗ)

При возвращении к идее можно проверить сначала два наиболее перспективных случая: 

**A. ACE-Step XL Turbo:** загрузить отдельную LoRA, чья карточка прямо указывает `acestep-v15-xl-turbo` (например, JAY 2); сравнить ту же композицию с тем же промптом и seed без/с адаптером. Проверить фактическую работу LoRA, прирост VRAM/RAM, время, артефакты, русскую дикцию, возможность корректного отключения и отсутствие влияния на следующий запуск.

**B. YuE2:** выбрать один жанровый адаптер (например, Industrial Rock) и один управляющий набор (Particle Sliders), удостовериться в соответствии форматов и ABI используемому `yue2.cpp`/PyTorch. Вести серию с одинаковыми seed/настройками и отдельным сравнением вокала и аранжировки.

Фиксировать: репозиторий и ревизию LoRA, родительские веса и их SHA, тип адаптера (LoRA/LoHA/AR/NAR), prompt/trigger, `scale`, runtime, VRAM/RAM, время, выходные WAV и субъективные результаты прослушивания. Не расширять продукт до получения убедительного преимущества.

### Правила совместимости

- **Не смешивать ACE-Step XL Base, XL Turbo и обычную 1.5:** совпадения семейства недостаточно.
- **Не считать BF16 и GGUF взаимозаменяемыми для LoRA:** форма и правила загрузки могут различаться.
- **Не путать декодерную LoRA Studio с LoRA стиля/вокала.**
- **Не добавлять все LoRA в обязательную поставку:** только отдельные необязательные загрузки после проверки лицензий.
- **Проверять лицензии каждой LoRA отдельно:** CC BY-NC, CC0, Apache 2.0, OpenRAIL и нестандартные варианты имеют разные условия; условия базовой модели тоже действуют.
- **Не делать вывод об улучшении качества только по тегам на Hugging Face.**

## 8. Индекс ссылок на исходники ЛОПАТЫ

- [Основной список музыкальных моделей и их вариантов](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicModelVariants.cs)
- [ACE-Step — закреплённые модели и исполнитель](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicAceCatalog.cs)
- [ACE-Step — контракты запроса](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicAceRequest.cs)
- [ACE-Step — Python worker](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Tools/music_ace_worker.py)
- [YuE2 Studio — закреплённые компоненты](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicStudioRuntime.cs)
- [YuE2 Studio — запросы и стадии](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicStudioWorker.cs)
- [DiffRhythm 2 — каталог](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicDiffRhythmCatalog.cs)
- [HeartMuLa — каталог](https://github.com/PiTrolKun/LOPATA/blob/main/Исходники/AIHub/Services/MusicHeartMuLaCatalog.cs)

## 9. Краткий вывод

**Самые насыщенные семейства — YuE2 и ACE-Step 1.5.** У YuE2 наиболее интересны Particle Sliders и голосовые/жанровые адаптеры, у ACE-Step — жанровые коллекции и LoRA, прямо привязанные к XL Turbo. Для DiffRhythm 2 найденный адаптер пока исследовательский, для HeartMuLa отмечен преимущественно инструментарий обучения.

**Решение по ЛОПАТЕ:** сохранить каталог как задел. В продукте произвольная установка/активация LoRA пока не подтверждена. Если будет принято решение развивать поддержку, начинать с изолированного стенда и одного точно совместимого адаптера, а не с массовой интеграции библиотеки.

---

*Сведения фиксируют находки на 18.10.2026; доступность, версии, лицензии и качество чужих репозиториев могут измениться. Источники выше используются как ссылки на материалы, а не как сертификат работоспособности на конкретной системе.*
