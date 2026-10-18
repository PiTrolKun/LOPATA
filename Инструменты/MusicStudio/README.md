# Закреплённый headless YuE2 Studio

## Наборы 001 и 002 — 18.10.2026

Набор 001 сохраняет прежний пакет `v3.4.0-lopata-paths-1` без изменения файлов.
Набор 002 использует `v3.6.0-lopata-paths-1`, Studio source
`c3e214f3a65f3f56c18e107127ebd2ebad5150ad`, engine
`c8516dbf44d660cca40d3f420c67063e37ec8258`.
Исходный portable: выпуск v3.6.0, SHA256
`04eac6e480152cc3ab5467f6fc51a60ae8445046df940ade87b9a1417c37a0f5`.

`prepare-3.6.ps1 -Source <checkout> -Portable <archive>` воспроизводит новый
пакет в `Runtime/MusicStudio/v3.6.0-lopata-paths-1`. Требуются MSVC/CMake/Rust;
генератор CMake — Visual Studio 17 2022, сборка Cargo locked, 4 задания.
Можно повторно использовать уже проверенный сервер через `-BuiltServer`
и обязательный `-BuiltServerSha256`. Стендовая сборка имеет SHA256
`46334484a4b48a484b374aaffdfc8adb7cd60ca0105ea933c52c2be58bfdbcec`.
Patch `lopata-paths-3.6.patch` сохраняет прежние явные пути проверенных весов
и readiness; другие native алгоритмы не менялись. Подготовка не перезаписывает
готовый runtime и не меняет старый. Общая упаковка копирует их соответственно
в `EngineSets/001/studio/native` и `EngineSets/002/studio/native`.

Новый пакет содержит точные Cargo.lock, Studio-source.zip, patch, исходники
registry-зависимостей и полные лицензии. Новые компоненты libFLAC:
flac-bound 0.5.0 (MIT), libflac-sys 0.3.4 (BSD-3-Clause). Symphonia 0.6.1
имеет MPL-2.0; LAME-обвязки остаются LGPL-3.0 с источниками для перелинковки.
Полный файл индивидуальных лицензий DiceBear styles сохранён в CargoLicenses;
пустое поле SPDX в Cargo не означает отсутствие лицензии (используется license-file).

### Совместимость запросов и громкости

- 001 принимает прежний `wav16` и `peak_clip` внутри генерации.
- 002 получает `wav32`, `companion_scale=1`, `lyric_timing=false`,
  `solver=midpoint` по умолчанию. Так не включаются новые значения Auto и
  распределение слов по разделам без решения пользователя.
- Затем официальный Studio processing API выполняет только
  `normalize: {peak_clip: <значение пользователя>}` над исходным float-файлом.
  `peak_clip` не отправляется как игнорируемое поле генерации. Нормализация
  применяется до промежуточного FLAC, чтобы не потерять float-пики >1.
- Стандартный FLAC24 preview переводится прежним FFmpeg в PCM16 stereo 48 kHz.
  Проверяются receipt песни/операции, отсутствие других обработок, кодек,
  частота, каналы и сохранение количества сэмплов. Простая смена формата
  без этой обработки не является сохранением прежней громкости.
- AB2 доступен как отдельный экспертный выбор; старые JSON без StudioSolver
  получают midpoint. Набор 001 отвергает AB2 до загрузки модели.
- Новые задания сохраняют runtime revision/pack. Возобновление выбирает этот
  контракт, а не текущую рекомендацию. Метаданные берут версии из задания.

Ниже сохранён рецепт предыдущего пакета для воспроизводимости и отката.

Исполнитель ЛОПАТЫ `v3.4.0-lopata-paths-1`. Сборка и подготовка выполняются
разработчиком; пользователь выбирает Studio Q8 и компоненты в обычном интерфейсе.
Полный React/Tauri интерфейс, Python и дополнительные модели Studio не устанавливаются.

## Воспроизведение комплекта

Нужны Rust/Cargo и MSVC build tools. Они нужны только разработчику, не пользователю.
Исходники: https://github.com/timoncool/YuE2-Studio, tag v3.4.0,
commit `9125be3cf9ba720ac439a81cd09ff8bcc2a00368`.
Поместить checkout в `_tmp/studio-q8/source` либо передать `-Source`.

Официальный portable-архив из выпуска v3.4.0:
`_tmp/studio-q8/YuE2-Studio-3.4.0-portable-windows-x64.zip`.
SHA256 `3aa7859346605a63f859edf56db8f6d1047588edd9d0c40fe39cb746f0bdfb71`.
Из него берутся только native engine и его DLL; не чужой интерфейс.
Ревизия engine: `1141479c725b803a3649c900fd3e6fcb27f6f595`.

NVIDIA redistributable archives, прямые официальные URL:

- https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.5.1.27-archive.zip
  сохранить как `_tmp/studio-q8/cublas-13.5.1.27-pinned.zip`;
  SHA256 `c946e1c825e05895747a95ed4fee18030b08052c09783b9b7b19818fd2e31f58`.
- https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.1.4-archive.zip
  сохранить как `_tmp/studio-q8/cublas-12.9.1.4.zip`;
  SHA256 `d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed`.

Запустить `prepare.ps1`: он проверяет исходную ревизию и архивы, применяет
`lopata-paths.patch`, собирает `cargo build --locked --release -p music-server`,
готовит Runtime/MusicStudio/v3.4.0-lopata-paths-1 и manifest размер/SHA каждого файла.
Существующий runtime не перезаписывается; сначала сохранить его отдельным резервом.
Сборка приложения требует наличия этого manifest.

Patch добавляет только явно заданные пути LOPATA_YUE_BACKBONE/VAE/COMPANION
и проверку готовности этих файлов. Модели остаются в управляемом хранилище.
ЛОПАТА предварительно проверяет их SHA256; DLL CUDA уже есть в комплекте,
поэтому генерация не вызывает скрытое скачивание библиотек NVIDIA.
Штатный загрузчик скачивает только отсутствующие веса/companion с лицензиями.

## Исходники, лицензии и перелинковка

Комплект содержит Studio-source.zip, Cargo.lock, patch, CargoSources/*.crate,
CargoLicenses, LICENSE-LAME, MIT native/ggml/yyjson, условия NVIDIA и Microsoft.
CargoSources сохраняет точные исходники registry-зависимостей, включая LAME 3.100
и MPL-компоненты. Можно восстановить Cargo vendor directory из этих архивов,
изменить покрытую библиотеку и повторить locked build серверного пакета.
Изменение и обратная разработка для исправления LGPL-библиотеки не запрещаются.
Ресурсы ASR, разделения дорожек, обучение и soundfont не используются.
Встроенные версии и лицензии не заменяют отдельные CC BY-NC условия весов.

## Проверка

Обычные Studio/Music тесты не запускают модель. Реальная проверка включается
явно переменными LOPATA_STUDIO_TEST_MODELS (установленное хранилище) и
LOPATA_STUDIO_TEST_OUTPUT (новая папка технических результатов).
Тест выполняет короткие план/синтез, Opus 320 с проверкой тегов и отмену.
Такой opt-in разрешён только в рамках озвученного пользователю технического теста.
Качество полного русского текста оценивается отдельно пользователем.
