# Встроенный FFmpeg для апскейлера, шортсов и конвертера видео

Ревизия LGPL-сборки `8.1.3-video-upscale-5` лежит в каталоге
`Runtime/VideoUpscale/ffmpeg-8.1.3-upscale-5`. Бинарники входят в основную
поставку приложения и проверяются при запуске конвертера.

Используется существующая исходная база Capture Minimal-8.1 и её закреплённые
библиотеки/инструментарий UCRT64. `sources.json` и исходные лицензии берутся
из уже подготовленного Capture/VideoEditor runtime. Конфигурация `build.sh`
включает FFV1, fd/pipe, PNG и фильтры апскейла. Для конвертера подключены
SVT-AV1, libaom, Kvazaar, VP9 и семейство NVENC. AV1 декодируется программно через libaom. Фильтр `av1_metadata` переносит положение цветовых отсчётов, допустимое в заголовке AV1.
Конфигурация FFmpeg остаётся LGPL 2.1+.

Для сборки на машине разработчика подготовить закреплённую основу Capture
Minimal-8.1 и четыре архива из [codec-sources.json](codec-sources.json). Скрипт
проверяет SHA-256 архивов и собирает библиотеки из локальных исходников.
Для этого нужны GCC, CMake, Ninja и NASM в MSYS2 UCRT64.
Запускать с `PATH=/ucrt64/bin:/usr/bin`:

```bash
bash /h/AI_HUB/Инструменты/VideoUpscaleRuntime/build.sh /h/AI_HUB /h/AI_HUB/_tmp/video-codec-sources
```

Затем из PowerShell выполнить `package.ps1`. Скрипт откажется перезаписывать
уже существующую ревизию. При изменении сборки задать новую ревизию во всех
ссылках и заново получить manifest; не менять файлы установленного комплекта.
Packaging сохраняет конфигурацию/источники/лицензии и SHA-256 каждого файла в
`Исходники/AIHub/Tools/VideoUpscale/ffmpeg-files.json`. Этот manifest встраивается
в assembly и проверяется общим RuntimeFileAudit перед исполнением.
Перед упаковкой скрипт проверяет фильтры апскейла, список кодировщиков и
программный декодер AV1. Папка `VideoUpscaleRuntime` вместе с лицензиями
попадает в publish и в основной файловый комплект установщика и обновления.

Python/Flash/VE в этот комплект не входят. Их оригинальные pinned архивы
определены в `Tools/VideoUpscale/runtime.json`, `weights.json` и UpscaleComponents.
Они загружаются штатным managed downloader в выбранное хранилище после
optional license gate. Системные окружения, драйвер и старый FFmpeg не меняются.
Visual Enhancer нельзя включать/зеркалировать в наш дистрибутив без отдельных
прав: загрузка происходит с официального release автора.

Проверки: VideoUpscaleTests, VideoUpscaleUiTests; реальные GPU-проверки
VideoUpscaleNativeTests выполняются только с явно заданным
LOPATA_UPSCALE_NATIVE_ROOT. Протокол первой интеграции:
Тесты/VideoUpscaleStand/REPORT_2026-11-04_INTEGRATION.md.
