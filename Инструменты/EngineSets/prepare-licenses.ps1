$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskRuntime = Join-Path $taskRoot 'Runtime\MusicStudio\v3.6.0-lopata-paths-1'
$taskLicenses = Join-Path $taskRoot 'Исходники\AIHub\Licenses'
$taskText = [Text.StringBuilder]::new()
[void]$taskText.AppendLine('LOPATA engine set 002: pinned Studio 3.6.0 and original dependency terms.')
$taskSources = @(Get-ChildItem -LiteralPath $taskRuntime -File | Where-Object Name -Match '^(LICENSE|CUDA-)')
$taskSources += @(Get-ChildItem -LiteralPath (Join-Path $taskRuntime 'CargoLicenses') -File)
foreach ($taskFile in $taskSources | Sort-Object FullName) {
    [void]$taskText.AppendLine()
    [void]$taskText.AppendLine([IO.Path]::GetRelativePath($taskRuntime, $taskFile.FullName))
    [void]$taskText.AppendLine([IO.File]::ReadAllText($taskFile.FullName))
}
[IO.File]::WriteAllText((Join-Path $taskLicenses 'texts\music-studio-002-NOTICES.txt'), $taskText.ToString(), [Text.UTF8Encoding]::new($false))
$taskEntry = [ordered]@{
    Id='runtime.music-yue2-studio-002'; Name='YuE2 Studio 3.6 headless · engine set 002'
    Version='v3.6.0-lopata-paths-1'; Author='timoncool and upstream contributors'
    License='MIT; Apache-2.0; MPL-2.0; LGPL; BSD-3-Clause; CC0-1.0; NVIDIA; Microsoft; dependency notices'
    Source='https://github.com/timoncool/YuE2-Studio/releases/tag/v3.6.0'; Checked='2026-10-18'
    Delivery='bundled/download'; Basic=$false; Terms=''
    Texts=@('texts/music-studio-002-NOTICES.txt')
    Ru='Закреплённый сервер Studio 3.6, CPU/Vulkan/CUDA и их библиотеки. Оригинальные лицензии и исходники зависимостей для восстановления и перелинковки сохранены внутри набора. Дополнительно учтены libFLAC (BSD), Symphonia (MPL), LAME (LGPL) и выбранные геометрические стили DiceBear (CC0). Полный файл лицензий стилей сохраняется. Условия весов моделей остаются отдельными; пакет не предоставляет отсутствующих коммерческих прав.'
    En='Pinned Studio 3.6 server and CPU/Vulkan/CUDA libraries. Original terms and dependency sources for rebuilding and relinking are retained in the set. Includes libFLAC (BSD), Symphonia (MPL), LAME (LGPL), and selected geometric DiceBear styles (CC0); the complete styles license file is retained. Model terms remain separate; this package grants no otherwise missing commercial rights.'
}
@($taskEntry) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskLicenses 'music-studio-002-entries.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $taskRoot 'Инструменты\native-engine-002-entries.json') -Destination (Join-Path $taskLicenses 'native-engine-002-entries.json')
& (Join-Path $taskRoot 'Инструменты\prepare-license-catalog.ps1') -AppendEntries (Join-Path $taskLicenses 'music-studio-002-entries.json')
& (Join-Path $taskRoot 'Инструменты\prepare-license-catalog.ps1') -AppendEntries (Join-Path $taskLicenses 'native-engine-002-entries.json')
