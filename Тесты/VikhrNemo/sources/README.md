---
license: apache-2.0
datasets:
- Vikhrmodels/GrandMaster-PRO-MAX
- Vikhrmodels/Grounded-RAG-RU-v2
language:
- en
- ru
base_model:
- mistralai/Mistral-Nemo-Instruct-2407
library_name: transformers
---
[Reame.md in English](Readme_en.md)

## Vikhr-Nemo-12B-Instruct-R-21-09-24

### Описание

**Vikhr-Nemo** - это наша флагманская унимодальная LLM (Large Language Model) представляющая из себя улучшенную версию [mistralai/Mistral-Nemo-Instruct-2407](https://huggingface.co/mistralai/Mistral-Nemo-Instruct-2407) командой **VikhrModels**, адаптированную преимущественно для русского и английского языков. Для ее обучения мы использовали несколько этапов включающих в себя **SFT** и **SMPO** - нашу собственную вариацию DPO, подробнее читайте в секции *"Как эта модель создавалась"*.

Модель оптимизированна для различных вариантов использования, включая ризонинг, суммаризацию, код, roleplay, поддержание диалога. Vikhr-Nemo обладает возможностью многоязычной генерации, и высокопроизводительными возможностями RAG. Модель иммет лучшие оценки среди прочих на наших инструктивных и RAG бенчарках и, поэтому, мы верим, что в некоторых задачах (например, RAG) может быть не хуже gpt-4o-mini от OpenAI.

Весь использованный код для обучения доступен в нашем репозитории [effective_llm_alignment](https://github.com/VikhrModels/effective_llm_alignment/) на GitHub, а основные датасеты доступны в нашем [профиле на HF](https://huggingface.co/Vikhrmodels).

### Особенности
1. Высокое качество генераций на русском и английском языках, а также некоторых других языках, благодаря датасету [Grandmaster-PRO-MAX](https://huggingface.co/datasets/Vikhrmodels/GrandMaster-PRO-MAX) и исходной модели
2. Поддержка системных промптов для регулриования стиля ответов
3. Поддержка до 128k токенов контекста благодаря исходной модели
4. Grounded RAG режим - модель имеет специальную роль documents и специальный режим работы для поиска идентификаторов релевантных вопросу пользователя документов и использования их для ответа на вопрос, вдохновлено аналогичной способностью модели Command-R

### Метрики и оценка качества

Модель оценивалась на нашем русскоязычном open-source SbS бенчмарке [ru-arena-general](https://github.com/VikhrModels/ru_llm_arena) (50 топиков по 10 вопросов), где судьей выступает gpt-4-1106-preview и [бенчмарке](https://colab.research.google.com/drive/16730rWQ4-yGqWoooLs0Ece_16frmOniP?usp=sharing) для RAG на основе тестового сета [Grounded-RAG-v2](https://huggingface.co/datasets/Vikhrmodels/Grounded-RAG-RU-v2), где судей выступа gpt-4o.

#### Результаты на Ru-Arena-General

В качестве референсых ответов, с которыми сравниваются модели выступают ответы от gpt-3.5-turbo-0125, поэтому она имеет винрейт 50%.

Здесь приведена лишь часть лидерборда, подробнее смотрите в репозитории бенчмарка.


180 сэмплов из арены утекло в трейн, спасибо Илье за информацию!

| Model Name                                       | Winrate  | 95% CI             | Average # Tokens |
|--------------------------------------------------|--------|--------------------|------------------|
| gpt-4-1106-preview                               | 90.9   | (-1.3, 1.0)        | 541              |
| gpt-4o-mini                                      | 83.9   | (-1.8, 1.1)        | 448              |
| **vikhr-nemo-12b-instruct-r-21-09-24(180 leaked)**               | **79.8**   | (-2.2, 1.9)        | **627**              |
| gemma-2-9b-it-sppo-iter3                         | 73.6   | (-1.6, 2.2)        | 509              |
| gemma-2-9b-it                                    | 69.2   | (-2.5, 1.9)        | 459              |
| t-lite-instruct-0.1                              | 64.7   | (-2.1, 1.7)        | 810              |
| vikhr-llama3.1-8b-instruct-r-21-09-24            | 63.4   | (-2.1, 2.5)        | 618              |
| suzume-llama-3-8B-multilingual-orpo-borda-half   | 57.1   | (-1.9, 2.2)        | 682              |
| mistral-nemo-instruct-2407                       | 50.5   | (-2.7, 2.6)        | 403              |
| gpt-3.5-turbo-0125                               | 50.0   | (0.0, 0.0)         | 220              |
| c4ai-command-r-v01                               | 49.0   | (-1.7, 2.2)        | 529              |
| meta-llama-3.1-8b-instruct                       | 43.1   | (-2.8, 2.3)        | 628              |

#### Результаты на бенчмарке RAG

Общий размер тестового сета - 200 примеров, 100 для in_domain вопросов и 100 для out_of_domain.

Тут для оценки качества модель-судья gpt-4o была проинструктирована учитывать релеватность и фактологичкскую полноту ответов исходя из документов и реферсного ответа от gpt-4-1106-preview.

Подробности промптов и оценок смотрите в коде бенчмарка на [коллабе](https://colab.research.google.com/drive/16730rWQ4-yGqWoooLs0Ece_16frmOniP?usp=sharing)

in_domain - вопросы которые связаны с содержанием предоставленных документов в той или иной степени \
out_of_domain - вопросы которые специально никак не связаны с содержанием предоставленных документов

<table>
<thead>
  <tr>
    <th rowspan="2">question_type</th>
    <th colspan="3">gpt-4o</th>
  </tr>
  <tr>
    <th>judge_correct_percent</th>
    <th>avg_answer_match_rougeL</th>
    <th>avg_abs_indexes_diff</th>
  </tr>
</thead>
<tbody>
  <tr>
    <td>in_domain</td>
    <td>73%</td>
    <td>0.34</td>
    <td>NaN</td>
  </tr>
  <tr>
    <td>out_of_domain</td>
    <td>81%</td>
    <td>0.20</td>
    <td>NaN</td>
  </tr>
</tbody>
</table>

<table>
<thead>
  <tr>
    <th style="visibility: hidden;" rowspan="2">question_type</th>
    <th colspan="3">Vikhr-Nemo-12B-Instruct-R-21-09-24</th>
  </tr>
  <tr>
    <th style="visibility: hidden;">judge_correct_percent</th>
    <th style="visibility: hidden;">avg_answer_match_rougeL</th>
    <th style="visibility: hidden;">avg_abs_indexes_diff</th>
  </tr>
</thead>
<tbody>
  <tr>
    <td>in_domain</td>
    <td>68%</td>
    <td>0.41</td>
    <td>0</td>
  </tr>
  <tr>
    <td>out_of_domain</td>
    <td>92%</td>
    <td>0.52</td>
    <td>0</td>
  </tr>
</tbody>
</table>

<table>
<thead>
  <tr>
    <th style="visibility: hidden;" rowspan="2">question_type</th>
    <th colspan="3">gpt-4o-mini</th>
  </tr>
  <tr>
    <th style="visibility: hidden;">judge_correct_percent</th>
    <th style="visibility: hidden;">avg_answer_match_rougeL</th>
    <th style="visibility: hidden;">avg_abs_indexes_diff</th>
  </tr>
</thead>
<tbody>
  <tr>
    <td>in_domain</td>
    <td>65%</td>
    <td>0.33</td>
    <td>NaN</td>
  </tr>
  <tr>
    <td>out_of_domain</td>
    <td>73%</td>
    <td>0.18</td>
    <td>NaN</td>
  </tr>
</tbody>
</table>

<table>
<thead>
  <tr>
    <th style="visibility: hidden;" rowspan="2">question_type</th>
    <th colspan="3">gpt-3.5-turbo-0125 </th>
  </tr>
  <tr>
    <th style="visibility: hidden;">judge_correct_percent</th>
    <th style="visibility: hidden;">avg_answer_match_rougeL</th>
    <th style="visibility: hidden;">avg_abs_indexes_diff</th>
  </tr>
</thead>
<tbody>
  <tr>
    <td>in_domain</td>
    <td>49%</td>
    <td>0.28</td>
    <td>NaN</td>
  </tr>
  <tr>
    <td>out_of_domain</td>
    <td>76%</td>
    <td>0.20</td>
    <td>NaN</td>
  </tr>
</tbody>
</table>

### Как эта модель создавалась

#### Инструктивная SFT часть

Для SFT этапа обучения модели мы подготовили большой (150к инструкций) инструктивный синтетический датасет [Vikhrmodels/GrandMaster-PRO-MAX](https://huggingface.co/datasets/Vikhrmodels/GrandMaster-PRO-MAX). Его особенностью является встроеный CoT (Chain-Of-Thought), для сбора которого мы использовали модифицированный промет для gpt-4-turbo, подробности в карточке датасета.

Кроме того, для того чтобы сделать RAG Grounding, мы подготовили другой синтетический датасет - [Vikhrmodels/Grounded-RAG-RU-v2](https://huggingface.co/datasets/Vikhrmodels/Grounded-RAG-RU-v2) (50k диалогов), его пайплайн сборки достаточно сложный для короткого описания и полробнее об этом вы можете прочитать в его карточке.

#### Этап алайнмента с SMPO

Для дальнейшего улучшения качества ответов мы использовали следущий пайплайн:
1) Обучили кастомную Reward модель (она пока не будет выкладываться в открытый доступ)
2) Дедуплицировали и отфилтровали используя RM модель оригинальный датасет Vikhrmodels/GrandMaster-PRO-MAX, получив порядка 10к самых высококачественных и разнообразных диалогов.
3) Сделали Rejection Sampling с SFT чекпоинтом используя полученный датасет и Reward модель. (Генерировали 7 гипотез и брали только 2 самые худшие как rejected)
4) Дообучили SFT чекпоинт с помощью нашего метода SMPO используя полученный датасет из этапа 3. SMPO был спроектирован и выбран как метод для повышения стабильности тренировки преференсов в условиях Rejection Sampling и достижения нужного margin.

Реализацию SMPO, rejection sampling и тд можно найти в нашей библиотеке [effective_llm_alignment](https://github.com/VikhrModels/effective_llm_alignment/) на GitHub

Идея использования именно SMPO, а не другого PO метода, возникла в результате проведения большого количества экспериментов с классическими методами, при необходимости лучшего контроля процесса сходимости. При тщательной настройке других методов (например SimPO), можно добится похожего результата, однако мы постарались стаблизировать этот процесс и объединить лучшие практики из других методов.

### Как работать с RAG

Роль documents представляет из себя список словарей с описанием контента документов, с примнением `json.dumps(array, ensure_ascii=False)` (см. пример ниже). \
Контент документов может быть представлен в **3** различных форматах: **Markdown**, **HTML**, **Plain Text**. Контент каждого документа - может быть чанком текста длиной до 4к символов.

```json
[
  {
    "doc_id": (0..5),
    "title": "(null or str)",
    "content": "(html or markdown or plain text)"
  }
]
```

#### Пример правильного использования с OpenAI-like API

Запуск vLLM сервера: `vllm serve --dtype half --max-model-len 32000 -tp 1 Vikhrmodels/Vikhr-Nemo-12B-Instruct-R-21-09-24 --api-key token-abc123`

```python
GROUNDED_SYSTEM_PROMPT = "Your task is to answer the user's questions using only the information from the provided documents. Give two answers to each question: one with a list of relevant document identifiers and the second with the answer to the question itself, using documents with these identifiers."

documents = [
  {
    "doc_id": 0,
    "title": "Глобальное потепление: ледники",
    "content": "За последние 50 лет объем ледников в мире уменьшился на 30%"
  },
  {
    "doc_id": 1,
    "title": "Глобальное потепление: Уровень моря",
    "content": "Уровень мирового океана повысился на 20 см с 1880 года и продолжает расти на 3,3 мм в год"
  }
]
sample_history = [
    {'role': 'system', 'content': GROUNDED_SYSTEM_PROMPT},
    {'role': 'documents', 'content': json.dumps(documents, ensure_ascii=False)},
    {'role': 'user', 'content': 'Глоабльное потепление'}
]
relevant_indexes = llm_client.chat.completions.create(
    model=llm_model,
    messages=sample_history,
    temperature=0.0,
    max_tokens=2048
).choices[0].message.content

print('Using documents: ' + relevant_indexes + '\n----')
final_answer = llm_client.chat.completions.create(
    model=llm_model,
    messages=sample_history + [{'role': 'assistant', 'content': relevant_indexes}],
    temperature=0.3,
    max_tokens=2048
).choices[0].message.content

print(final_answer)
```

Ответ после выполнения кода будет выглядеть примерно так:

    Using documents: {"relevant_doc_ids": [0, 1]}
    ----

    Глобальное потепление – это долгосрочное повышение средней температуры атмосферы и океанов Земли. Это явление имеет множество последствий для нашей планеты, и среди них можно выделить два ключевых аспекта, основанных на доступных мне данных:

    1. **Уменьшение объема ледников**: За последние 50 лет наблюдается значительное сокращение объема ледников по всему миру. Согласно данным, объем ледников уменьшился на 30%. Это может быть связано с таянием ледников из-за повышения температур, что является одним из признаков глобального потепления.

    2. **Повышение уровня моря**: Уровень мирового океана также увеличивается, что связано с таянием ледников и ледяных покровов, а также с расширением воды при повышении температуры. С 1880 года уровень моря повысился на 20 сантиметров, и этот процесс продолжается, с ежегодным увеличением на 3,3 миллиметра.

    Эти изменения имеют серьезные последствия для экосистем, климата и человеческого общества. Таяние ледников приводит к повышению уровня моря, что может привести к затоплению прибрежных территорий и островов, а также к изменению водных ресурсов и климатических паттернов.

Используя первый ответ модели `relevant_indexes` (JSON), можно понять нашла ли модель информацию в документах или нет, она обучена возврашать пустой массив если ее нет и в таком случае она будет отвечать, что не смогла найти информацию в базе знаний (при генерации второго ответа).

### Нюансы и ограничения
- Модель имеет **низкий уровень безопасности ответов** и нацелена на правильное и полное выполенние инструкций, имейте это ввиду при использовании и тестируйте самостоятельно. Частично это исправляется системными промптами и дополнительными указаниями о важности безопасности в промпте пользователя.
- Системные промпты не предназначены для описание персонажей, мы рекомендуем использовать их для спецификации стиля ответа (вроде "answer only in json format"). Кроме того, желательно, писать их **на английском языке**, так как так было в датасете, от использования английского в системных промтпах не зависит язык ответа.
- RAG режим **требует обязательного** наличия системного промпта `GROUNDED_SYSTEM_PROMPT` описаного в секции *Как работать с RAG*. Так же иногда модель может добавлять общую информацию из своих знаний в ответ к той, что есть в документах.
- Модель лучше использовать с низкой темптературой (0.1-0.5), а таже использовать top_k (30-50), при температуре 1.0 были замечены случайные дефекты генерации.

### Авторы
- Sergei Bratchikov, [NLP Wanderer](https://t.me/nlpwanderer), Vikhr Team
- Konstantin Korolev, Vikhr Team
- Aleksandr Nikolich, Vikhr Team

### Cite
```
@inproceedings{nikolich2024vikhr,
  title={Vikhr: Constructing a State-of-the-art Bilingual Open-Source Instruction-Following Large Language Model for {Russian}},
  author={Aleksandr Nikolich and Konstantin Korolev and Sergei Bratchikov and  Igor Kiselev and Artem Shelmanov },
  booktitle = {Proceedings of the 4rd Workshop on Multilingual Representation Learning (MRL) @ EMNLP-2024}
  year={2024},
  publisher = {Association for Computational Linguistics},
  url={https://arxiv.org/pdf/2405.13929}
}
```
