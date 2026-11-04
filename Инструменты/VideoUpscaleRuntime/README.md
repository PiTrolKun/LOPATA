# Встроенный runtime «Апскейла видео»

Это отдельная LGPL-сборка FFmpeg, а не замена runtime видеоредактора.
Ревизия: `8.1.3-video-upscale-2`; каталог
`Runtime/VideoUpscale/ffmpeg-8.1.3-upscale-2` (бинарники не в Git).

Используется существующая исходная база Capture Minimal-8.1 и её закреплённые
библиотеки/инструментарий UCRT64. `sources.json` и исходные лицензии берутся
из уже подготовленного Capture/VideoEditor runtime. Конфигурация `build.sh`
не включает GPL/nonfree/version3; добавлены FFV1, fd/pipe, PNG и фильтры.

Из MSYS2 UCRT64 с PATH=/ucrt64/bin:/usr/bin:

```bash
bash /h/AI_HUB/Инструменты/VideoUpscaleRuntime/build.sh /h/AI_HUB
```

Затем из PowerShell выполнить `package.ps1`. Скрипт откажется перезаписывать
уже существующую ревизию. При изменении сборки задать новую ревизию во всех
ссылках и заново получить manifest; не менять файлы установленного комплекта.
Packaging сохраняет конфигурацию/источники/лицензии и SHA-256 каждого файла в
`Исходники/AIHub/Tools/VideoUpscale/ffmpeg-files.json`. Этот manifest встраивается
в assembly и проверяется общим RuntimeFileAudit перед исполнением.

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
