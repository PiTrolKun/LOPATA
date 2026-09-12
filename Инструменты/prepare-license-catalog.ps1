param([switch]$RefreshTexts)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$dest=Join-Path $root 'Исходники/AIHub/Licenses'
New-Item -ItemType Directory -Force "$dest/texts" | Out-Null
$snapshot=Get-Content (Join-Path $root 'Документы_проекта/Лицензии/каталоги_2026-09-05.json') -Raw | ConvertFrom-Json
$nugetRows=Import-Csv (Join-Path $root 'Документы_проекта/Лицензии/nuget_2026-09-05.csv')
$entries=[Collections.Generic.List[object]]::new()
function Add-Entry($id,$name,$version,$license,$source,$author,$basic,$delivery,$ru,$en) {
 $entries.Add([ordered]@{Id=$id;Name=$name;Version=$version;Author=$author;License=$license;Source=$source;Checked='2026-09-05';Ru=$ru;En=$en;Basic=$basic;Delivery=$delivery;Terms='';Texts=@()})
}
$ru='Сведения о лицензии указаны поставщиком. Соблюдайте условия оригинальной лицензии. Проверка всех вложенных частей отдельно не завершена.'
$en='License information is declared by the provider. Follow the original license terms. Review of all bundled parts is not complete.'
foreach($c in $snapshot.Components) {
 if($c.DeliveryKind -eq 'planned'){continue}
 $source=$c.DownloadUrl
 if(!$source){$source='https://github.com/PiTrolKun/LOPATA/blob/main/THIRD_PARTY_NOTICES.md'}
 if($c.IsBuiltIn){
  $package=$nugetRows | Where-Object Id -eq $c.Name | Select-Object -First 1
  if($package.ProjectUrl){$source=$package.ProjectUrl}
  if($c.Id -eq 'builtin.dotnet'){$source='https://github.com/dotnet/runtime'}
  if($c.Id -eq 'builtin.wpf-images'){$source='https://github.com/dotnet/wpf'}
 }
 $license=$c.License
 if($c.Id -eq 'builtin.wpf-images'){$license='MIT (.NET/WPF); third-party notices apply'}
 Add-Entry $c.Id $c.Name $c.Version $license $source $c.Source $true $(if($c.IsBuiltIn){'bundled'}else{'download'}) $ru $en
}
foreach($m in $snapshot.ManagedModels) {
 Add-Entry $m.ModelArtifactId $m.DisplayName $m.Revision $m.License $m.SourcePage $m.RepositoryId ($m.Role -eq 'core') 'download' $ru $en
}
Add-Entry 'lopata' 'ЛОПАТА / LOPATA' '0.1' 'GPL-3.0-or-later' 'https://github.com/PiTrolKun/LOPATA' 'PiTrolKun and contributors' $true 'bundled' 'Лицензии сторонних компонентов действуют отдельно. Подтверждение не меняет предоставленных ими прав.' 'Third-party licenses apply separately. Acknowledgement does not change the rights they grant.'
foreach($n in (Import-Csv (Join-Path $root 'Документы_проекта/Лицензии/nuget_2026-09-05.csv'))) {
 Add-Entry "nuget.$($n.Id)" $n.Id $n.Version $n.License $n.ProjectUrl $n.Authors $true 'bundled' $ru $en
}
Add-Entry 'backend.llama' 'llama.cpp' 'b9442' 'MIT' 'https://github.com/ggml-org/llama.cpp/tree/b9442' 'ggml-org contributors' $true 'bundled' $ru $en
Add-Entry 'backend.chatllm' 'chatllm.cpp' 'v24' 'MIT' 'https://github.com/foldl/chatllm.cpp' 'foldl' $true 'bundled' $ru $en
Add-Entry 'runtime.espeak' 'eSpeak NG' '1.52.0' 'GPL-3.0-or-later' 'https://github.com/espeak-ng/espeak-ng/tree/1.52.0' 'eSpeak NG contributors' $true 'bundled' $ru $en
Add-Entry 'native.cuda' 'NVIDIA CUDA runtime / cuBLAS' '12.4' 'NVIDIA CUDA EULA' 'https://docs.nvidia.com/cuda/archive/12.4.0/eula/index.html' 'NVIDIA' $true 'bundled' $ru $en
Add-Entry 'native.openssl' 'OpenSSL' '1.1.1k' 'OpenSSL AND SSLeay' 'https://github.com/openssl/openssl/tree/OpenSSL_1_1_1k' 'OpenSSL Project, Eric Young, Tim Hudson' $true 'bundled' $ru $en
Add-Entry 'native.vulkan' 'Vulkan Loader' '1.4.304.0' 'Apache-2.0 and permissive exceptions' 'https://github.com/KhronosGroup/Vulkan-Loader/tree/v1.4.304' 'Khronos Group contributors' $true 'bundled' $ru $en
Add-Entry 'native.imagemagick' 'ImageMagick (chatllm)' '7.1.2-30' 'ImageMagick License; delegate licenses apply' 'https://imagemagick.org/license/' 'ImageMagick Studio LLC' $true 'bundled' $ru $en
Add-Entry 'native.libomp' 'OpenMP runtime (libomp140)' 'bundled DLL' 'Not fully identified / не уточнена' 'https://github.com/ggml-org/llama.cpp/releases/tag/b9442' 'See distribution source' $true 'bundled' 'Точное происхождение и комплект лицензий этой DLL на дату проверки не установлены. Это не означает отсутствия ограничений.' 'The exact provenance and license bundle of this DLL have not been established as of the check date. This does not mean there are no restrictions.'
Add-Entry 'bge-reranker-v2-m3-tool' 'BAAI/bge-reranker-v2-m3' 'catalog revision' 'Apache-2.0' 'https://huggingface.co/BAAI/bge-reranker-v2-m3' 'BAAI' $true 'download' $ru $en
Add-Entry 'Qwen/Qwen3-0.6B-GGUF' 'Qwen3-0.6B GGUF' 'catalog revision' 'Apache-2.0' 'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF' 'Qwen' $true 'download' $ru $en
$omni=$entries | Where-Object Id -eq 'model-qwen2-5-omni-3b'
$omni.Ru='Qwen Research License: некоммерческие исследования и оценка. Бесплатность приложения не разрешает любое использование. Для коммерческого использования требуется отдельное разрешение правообладателя.'
$omni.En='Qwen Research License: noncommercial research and evaluation. A free application does not authorize every use. Commercial use requires separate permission from the licensor.'
Add-Entry 'model-qwen2-5-omni-3b-q4km' 'Qwen2.5-Omni-3B Q4_K_M + Q8 projector' '75f1b73b657a50f5092502799457ccb4a4a1f9df' $omni.License 'https://huggingface.co/ggml-org/Qwen2.5-Omni-3B-GGUF/tree/75f1b73b657a50f5092502799457ccb4a4a1f9df' 'Qwen; GGUF conversion by ggml-org' $false 'download' $omni.Ru $omni.En
$alpha=$entries | Where-Object Id -eq 'model-qwen2-5-omni-3b-q4km'
$alpha.Checked='2026-09-06'
$alpha.Ru+=' Карточка GGUF-конверсии отсылает к лицензии исходной Qwen2.5-Omni-3B. Модель и проектор скачиваются отдельно; встроенная генерация речи не используется.'
$alpha.En+=' The GGUF conversion card refers to the upstream Qwen2.5-Omni-3B license. Model and projector are downloaded separately; built-in speech generation is not used.'
Add-Entry 'model-qwen3-8-4b-distill-q5km' 'Qwen3.8-4B-Distill Q5_K_M + F16 projector' '796f0c8fbdab2e6e0c14a13d499075850e4a16b3' 'Apache-2.0' 'https://huggingface.co/mradermacher/Qwen3.8-4B-Distill-GGUF/tree/796f0c8fbdab2e6e0c14a13d499075850e4a16b3' 'Empero; Qwen; GGUF conversion by mradermacher' $false 'download' '' ''
$distill=$entries | Where-Object Id -eq 'model-qwen3-8-4b-distill-q5km'
$distill.Checked='2026-09-06'
$distill.Ru='В карточках исходной модели и GGUF-конверсии заявлена Apache-2.0. Модель и проектор скачиваются отдельно. Отдельные файлы LICENSE в этих репозиториях на дату проверки не найдены; приложен стандартный текст Apache-2.0. Исходная модель: https://huggingface.co/empero-ai/Qwen3.8-4B-Distill/tree/c83cb7aa2999d2f35c43e9ae0634a30eb8985a1e. Соблюдайте условия лицензии и сохраняйте необходимые уведомления при распространении.'
$distill.En='The upstream model and GGUF conversion cards declare Apache-2.0. Model and projector are downloaded separately. Separate LICENSE files were not found in these repositories as of the check date; the standard Apache-2.0 text is included. Upstream model: https://huggingface.co/empero-ai/Qwen3.8-4B-Distill/tree/c83cb7aa2999d2f35c43e9ae0634a30eb8985a1e. Follow the license terms and retain required notices when redistributing.'
Add-Entry 'model-qwen3-8-9b-distill-q4km' 'Qwen3.8-9B-Distill Q4_K_M + Qwen3.5-9B F16 projector' '760121cd70bb4c36b2b5ec58eb765e0df5987efe:3885219b6810b007914f3a7950a8d1b469d598a5' 'Apache-2.0' 'https://huggingface.co/empero-ai/Qwen3.8-9B-Distill-GGUF/tree/760121cd70bb4c36b2b5ec58eb765e0df5987efe' 'Empero; Qwen; projector conversion by Unsloth' $false 'download' '' ''
$beta=$entries | Where-Object Id -eq 'model-qwen3-8-9b-distill-q4km'
$beta.Checked='2026-09-06'
$beta.Ru='Составной комплект: текстовая модель Empero и F16 проектор исходной Qwen3.5-9B из Unsloth. В карточках заявлена Apache-2.0; приложен стандартный текст лицензии. Исходник: https://huggingface.co/empero-ai/Qwen3.8-9B-Distill/tree/0934f3d2327ff2df2197495278c4c46ae5a56bd9. Проектор: https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/tree/3885219b6810b007914f3a7950a8d1b469d598a5. Проектор не является проверенной конверсией весов Empero; качество этой пары требует реального теста. Файлы скачиваются отдельно, в установщик не входят. При распространении соблюдайте условия и сохраняйте уведомления.'
$beta.En='Composite bundle: Empero text model and the base Qwen3.5-9B F16 projector converted by Unsloth. The cards declare Apache-2.0; the standard license text is included. Source: https://huggingface.co/empero-ai/Qwen3.8-9B-Distill/tree/0934f3d2327ff2df2197495278c4c46ae5a56bd9. Projector: https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/tree/3885219b6810b007914f3a7950a8d1b469d598a5. The projector is not a verified conversion of Empero weights; this pair requires a real quality test. Files are downloaded separately and are not bundled with the installer. Follow the terms and retain notices when redistributing.'
Add-Entry 'model-qwen3-8-27b-ridge-37bpw' 'Qwen3.8-27B-Ridge 3.7 bpw + BF16 projector' '486faa5f2032ff99bdc8993ade1b8fff13d1464c' 'Apache-2.0' 'https://huggingface.co/empero-ai/Qwen3.8-27B-Ridge-GGUF/tree/486faa5f2032ff99bdc8993ade1b8fff13d1464c' 'Qwen; Ridge quantization by Empero' $false 'download' '' ''
$gamma=$entries | Where-Object Id -eq 'model-qwen3-8-27b-ridge-37bpw'
$gamma.Checked='2026-09-06'
$gamma.Ru='Смешанная GGUF-квантизация Ridge и BF16 модуль изображений опубликованы Empero. Карточка указывает Apache-2.0; оригинальный LICENSE исходной Qwen3.8-27B проверен: https://huggingface.co/Qwen/Qwen3.8-27B/blob/1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0/LICENSE. Файлы скачиваются отдельно и не входят в установщик. При распространении соблюдайте оригинальные условия и сохраняйте необходимые уведомления. Качество и расход памяти конкретного комплекта проверяются отдельно.'
$gamma.En='The Ridge mixed GGUF quantization and BF16 image module are published by Empero. The card declares Apache-2.0; the original upstream Qwen3.8-27B LICENSE was checked: https://huggingface.co/Qwen/Qwen3.8-27B/blob/1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0/LICENSE. Files are downloaded separately and are not bundled with the installer. Follow the original terms and retain required notices when redistributing. Quality and memory use of this specific bundle are evaluated separately.'
$kokoro=$entries | Where-Object Id -eq 'model-kokoro-ru-sveta'
$kokoro.License='OpenRAIL (weights, exact variant unavailable); Apache-2.0 (code); eSpeak GPL; accentuator declared MIT'
$kokoro.Ru='Автор указал OpenRAIL для весов и Apache-2.0 для кода. Точный вариант и полный текст OpenRAIL на дату проверки не найдены. Репозиторий accentuator указывает MIT; применимость к отдельным словарям уточняется. Подтверждение ознакомления не заменяет отсутствующих условий и не предоставляет дополнительных прав.'
$kokoro.En='The author declares OpenRAIL for weights and Apache-2.0 for code. The exact OpenRAIL variant and full text were not found as of the check date. The accentuator repository declares MIT; coverage of individual dictionaries remains unverified. Acknowledgement does not replace missing terms or grant additional rights.'
$kimi=$entries | Where-Object Id -eq 'model-kimi-vl-a3b-thinking-2506-chatllm-q4_1'
$kimi.Ru='Исходная модель Moonshot заявлена под MIT. Карточка конвертации на ModelScope указывает other, сообщает об исследовательском/учебном назначении и отсылает к соглашениям соответствующих моделей. Отдельный полный текст лицензии конвертации не найден.'
$kimi.En='The upstream Moonshot model declares MIT. The conversion card on ModelScope lists other, describes research/study purposes and refers to the respective model agreements. A separate full conversion license was not found.'
Add-Entry 'backend.qdrant' 'Qdrant' '1.19.1' 'Apache-2.0' 'https://github.com/qdrant/qdrant/releases/tag/v1.19.1' 'Qdrant contributors' $false 'download' 'Локальный сервер поиска и хранения. Официальная Windows x64 сборка скачивается отдельно; в установщик не входит. Приложен оригинальный LICENSE версии 1.19.1. При распространении сохраняйте лицензию и применимые уведомления; отдельный аудит всех зависимостей готового бинарного файла не завершён.' 'Local search and storage server. The official Windows x64 binary is downloaded separately, not bundled in the installer. Original version 1.19.1 LICENSE is included. Retain the license and applicable notices when redistributing; a separate audit of all binary dependencies is not complete.'
$qdrant = $entries | Where-Object Id -eq 'backend.qdrant'
$qdrant.Checked = '2026-09-10'
$qdrant.Texts = @('texts/qdrant-LICENSE.txt')
Add-Entry 'model.giga-embeddings-480m' 'ai-sage/Giga-Embeddings-instruct-480M-0826' '0c94f705aa35719324fb46f7e75b0a5c275da6e4' 'MIT (declared by publisher)' 'https://huggingface.co/ai-sage/Giga-Embeddings-instruct-480M-0826' 'ai-sage' $false 'download' 'Карточка автора указывает MIT. В закреплённом репозитории отдельный LICENSE отсутствует; точный текст уведомления правообладателя не опубликован. Скачиваются отдельно веса, токенизатор и код модели закреплённой версии. Ознакомление не создаёт дополнительных прав. Пока используется только для индексации первоисточника.' 'The publisher declares MIT. The pinned repository has no separate LICENSE or copyright notice. Weights, tokenizer and pinned model code are downloaded separately. Acknowledgement does not grant additional rights. Currently used only to index project source books.'
Add-Entry 'model.runeweaver' 'MN-12B-Runeweaver-RP-RU Q4_K_M' '84fa96954eef3eec4e92433e133c5bb1c774fc22' 'Apache-2.0 (declared by publisher)' 'https://huggingface.co/limloop/MN-12B-Runeweaver-RP-RU-GGUF' 'limloop and upstream model authors' $false 'download' 'Автор GGUF указывает Apache-2.0. Это объединённая модель; отдельный аудит всех исходных моделей не завершён. Веса скачиваются отдельно либо используются из уже зарегистрированного файла. Условия исходных компонентов сохраняют силу.' 'The GGUF publisher declares Apache-2.0. This is a merge; a separate audit of every upstream model is not complete. Weights are downloaded separately or reused from an existing registered file. Upstream component terms still apply.'
Add-Entry 'runtime.giga-python' 'Giga Python processing environment' 'Python 3.12.10 / PyTorch 2.10.0 cu128 / Transformers 5.3.0 / pip 25.3' 'PSF-2.0; BSD-3-Clause; Apache-2.0; MIT; NVIDIA CUDA terms; dependency licenses' 'https://pytorch.org/get-started/previous-versions/' 'Python Software Foundation, PyTorch, Hugging Face, pip and dependency authors' $false 'download' 'Изолированное окружение скачивается отдельно: Python, pip, PyTorch с CUDA 12.8 и Transformers. Приложены оригинальные основные лицензии; пакеты сохраняют собственные LICENSE/NOTICE. Условия NVIDIA: https://docs.nvidia.com/cuda/archive/12.8.0/eula/index.html. Точный состав транзитивных зависимостей фиксируется при установке; отдельный аудит всех зависимостей не завершён.' 'A separate environment downloads Python, pip, PyTorch with CUDA 12.8 and Transformers. Original primary licenses are included; packages retain their LICENSE/NOTICE files. NVIDIA terms: https://docs.nvidia.com/cuda/archive/12.8.0/eula/index.html. Transitive package versions are recorded during installation; a separate review of every dependency is incomplete.'
foreach($id in @('model.giga-embeddings-480m','model.runeweaver','runtime.giga-python')) { ($entries | Where-Object Id -eq $id).Checked='2026-09-10' }
($entries | Where-Object Id -eq 'runtime.giga-python').Texts=@('texts/python-3.12.10-LICENSE.txt','texts/pytorch-LICENSE.txt','texts/transformers-LICENSE.txt','texts/pip-LICENSE.txt')
Add-Entry 'model.jelly-gliner' 'GLiNER2.5 Multi' 'aaecfe45db1d828c963717054ccb868e8ad1f1d5' 'Apache-2.0' 'https://huggingface.co/fastino/gliner2.5-multi-v1' 'Fastino' $false 'download' 'Карточка закреплённой модели указывает Apache-2.0. Модель и токенизатор скачиваются отдельно. Отдельный LICENSE в пакете отсутствует; сохраняется карточка автора. Полный аудит исходных обучающих данных не проводился.' 'The pinned model card declares Apache-2.0. Model and tokenizer are downloaded separately. No separate LICENSE is present in the package; the model card is retained. Training data have not been independently audited.'
Add-Entry 'model.jelly-nuextract' 'NuExtract3' 'c99dc8f5641b866aa0192b6ea78f84bf9f3535f1' 'Apache-2.0' 'https://huggingface.co/numind/NuExtract3' 'NuMind' $false 'download' 'Веса и токенизатор скачиваются отдельно, оригинальный LICENSE сохраняется рядом с моделью. Карточка и LICENSE указывают Apache-2.0. Полный аудит исходных обучающих данных не проводился.' 'Weights and tokenizer are downloaded separately; the original LICENSE is retained beside the model. The card and LICENSE declare Apache-2.0. Training data have not been independently audited.'
Add-Entry 'runtime.jelly-python' 'Jelly isolated extraction libraries' 'GLiNER2 2.0.0 / Transformers 4.57.6 and 5.5.4' 'Apache-2.0; MIT; BSD; dependency licenses' 'https://github.com/fastino-ai/GLiNER2' 'Fastino, Hugging Face and dependency authors' $false 'download' 'Две изолированные группы Python-пакетов используют подготовленный PyTorch/CUDA. Версии фиксируются в манифесте и отчёте установки. Оригинальные LICENSE/NOTICE сохраняются в пакетах. Полный аудит всех транзитивных зависимостей не завершён; применяются также условия Python, PyTorch и NVIDIA.' 'Two isolated Python package sets use the prepared PyTorch/CUDA runtime. Versions are pinned in the manifest and installation report. Packages retain original LICENSE/NOTICE files. Full transitive dependency review is incomplete; Python, PyTorch and NVIDIA terms also apply.'
foreach($id in @('model.jelly-gliner','model.jelly-nuextract','runtime.jelly-python')) { ($entries | Where-Object Id -eq $id).Checked='2026-09-12' }
($entries | Where-Object Id -eq 'runtime.jelly-python').Texts=@('texts/transformers-LICENSE.txt')
$sources=@{
 'MIT'='https://raw.githubusercontent.com/ggml-org/llama.cpp/b9442/LICENSE'
 'Apache-2.0'='https://www.apache.org/licenses/LICENSE-2.0.txt'
 'GPL-3.0-or-later'='https://raw.githubusercontent.com/espeak-ng/espeak-ng/1.52.0/COPYING'
 'OpenSSL AND SSLeay'='https://raw.githubusercontent.com/openssl/openssl/OpenSSL_1_1_1k/LICENSE'
 'Qwen Research'='https://huggingface.co/Qwen/Qwen2.5-Omni-3B/raw/f75b40e3da2003cdd6e1829b1f420ca70797c34e/LICENSE'
}
# Download only small license texts on explicit maintainer refresh. No runtime downloads.
foreach($key in $sources.Keys) {
 $name=($key -replace '[^a-zA-Z0-9.-]','_')+'.txt';$path=Join-Path "$dest/texts" $name
 if($RefreshTexts -or !(Test-Path $path)){Invoke-WebRequest $sources[$key] -OutFile $path -TimeoutSec 30}
 foreach($e in $entries){if(($e.License -eq $key -and ($key -ne 'MIT' -or $e.Id -eq 'backend.llama')) -or ($e.Id -in @('model-qwen2-5-omni-3b','model-qwen2-5-omni-3b-q4km') -and $key -eq 'Qwen Research')){$e.Texts=@($e.Texts)+"texts/$name"}}
}
# Per-package copyright notices must be retained, not replaced by a generic MIT example.
$assets=Get-Content (Join-Path $root 'Исходники/AIHub/obj/project.assets.json') -Raw | ConvertFrom-Json
foreach($folder in $assets.packageFolders.PSObject.Properties.Name){
 foreach($lib in $assets.libraries.PSObject.Properties){
  $e=$entries | Where-Object Id -eq ('nuget.'+$lib.Name.Split('/')[0]);if(!$e){continue}
  $package=Join-Path $folder $lib.Value.path
  if(!(Test-Path $package)){continue}
  foreach($f in (Get-ChildItem $package -File -Recurse | Where-Object {$_.Name -match '^(LICENSE|NOTICE|COPYING)(\.|$)' -and $_.Length -lt 200000})){
   $name=($lib.Name -replace '/','_')+'_'+$f.Name
   Copy-Item $f.FullName "$dest/texts/$name" -Force
   $e.Texts=@($e.Texts)+"texts/$name"
  }
 }
}
foreach($pair in @(@('backend.chatllm','Runtime/Backends/chatllm.cpp/v24/win-x64/LICENSE-chatllm.txt'),@('native.imagemagick','Runtime/Backends/chatllm.cpp/v24/win-x64/imagemagick/LICENSE.txt'),@('native.imagemagick','Runtime/Backends/chatllm.cpp/v24/win-x64/imagemagick/NOTICE.txt'))){
 $name=$pair[0]+'_'+(Split-Path $pair[1] -Leaf);Copy-Item (Join-Path $root $pair[1]) "$dest/texts/$name" -Force
 ($entries | Where-Object Id -eq $pair[0]).Texts+= "texts/$name"
}
foreach($pair in @(@('builtin.dotnet','microsoft.netcore.app.runtime.win-x64'),@('builtin.wpf-images','microsoft.windowsdesktop.app.runtime.win-x64'))){
 foreach($folder in $assets.packageFolders.PSObject.Properties.Name){
  $package=Join-Path $folder ($pair[1]+'/10.0.8')
  if(!(Test-Path $package)){continue}
  foreach($f in (Get-ChildItem $package -File | Where-Object {$_.Name -match 'LICENSE|THIRD-PARTY-NOTICES'})){
   $name=$pair[0]+'_'+$f.Name;Copy-Item $f.FullName "$dest/texts/$name" -Force
   ($entries | Where-Object Id -eq $pair[0]).Texts+= "texts/$name"
  }
 }
}
foreach($e in $entries){
 $e.Texts=@($e.Texts | Select-Object -Unique)
 $terms=$e.License+$e.Ru+$e.En
 foreach($f in $e.Texts){$terms+=[IO.File]::ReadAllText((Join-Path $dest $f))}
 $e.Terms=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($terms)))
}
$entries | ConvertTo-Json -Depth 8 | Set-Content "$dest/catalog.json" -Encoding utf8
$basic=@($entries | Where-Object Basic)
$text="ЛОПАТА / LOPATA — лицензии компонентов`r`nПодтверждая ознакомление, вы обязуетесь соблюдать применимые условия. Это не изменяет лицензии и не предоставляет отсутствующих прав.`r`n`r`n"
foreach($e in $basic){$delivery=if($e.Delivery -eq 'bundled'){'Входит в установку'}else{'Скачивается отдельно'};$text+="$($e.Name) — $($e.License)`r`n$delivery`r`n$($e.Author)`r`n$($e.Ru)`r`nПроверено: $($e.Checked)`r`n$($e.Source)`r`n`r`n"}
foreach($f in @($basic.Texts | Select-Object -Unique)){$text+="`r`n$f`r`n"+[IO.File]::ReadAllText((Join-Path $dest $f))}
[IO.File]::WriteAllText("$dest/installer.txt",$text,[Text.UTF8Encoding]::new($true))
@($basic | ForEach-Object {@{Id=$_.Id;Terms=$_.Terms;AcceptedAt='__ACCEPTED_AT__';Source='installer';AppVersion='__APP_VERSION__'}}) | ConvertTo-Json -Depth 5 | Set-Content "$dest/installer-receipt.json" -Encoding ascii
Write-Host "Prepared $($entries.Count) license entries. Review catalog and installer text before shipping."
