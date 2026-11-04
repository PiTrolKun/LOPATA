"""Add optional component terms and UI strings without changing existing acknowledgements."""
import pathlib, json, urllib.request, importlib.metadata as meta
ROOT = pathlib.Path(__file__).resolve().parents[2]
APP = ROOT/'Исходники/AIHub'

labels = {
'Title':('Апскейл видео','Video upscale'), 'Description':('Увеличивайте видео до 4K, восстанавливайте детали и сравнивайте результат с оригиналом.','Upscale video to 4K, restore details, and compare with the original.'),
'ChooseMode':('Как будем улучшать видео?','Choose your workflow'),
'Mode.Manual':('Ручной','Manual'), 'Mode.Balanced':('Усреднённый','Balanced'), 'Mode.Heavy':('Тяжёлый','Heavy'),
'Mode.Manual.Help':('Соберите цепочку: все способы, порядок шагов и полная калибровка. Обычное увеличение работает без моделей.','Build a chain with all methods, step ordering and complete calibration. Ordinary scaling needs no models.'),
'Mode.Balanced.Help':('FlashVSR+ восстанавливает детали примерно в 1080p, затем обычный алгоритм увеличивает до 4K.','FlashVSR+ restores details at about 1080p, followed by ordinary scaling to 4K.'),
'Mode.Heavy.Help':('Два нейронных прохода: 1080p → 1440p с мягким смешиванием второго результата → 4K. Медленнее; результат не обязательно лучше.','Two neural passes: 1080p → 1440p with gentle blending of the second result → 4K. Slower, not necessarily better.'),
'Import':('+ Добавить видео','+ Add video'), 'OpenProject':('Открыть проект…','Open project…'), 'SaveAs':('Сохранить проект…','Save project…'),
'Mode':('Режим…','Mode…'), 'Export':('Экспорт всего видео…','Export full video…'), 'Cache':('Рабочая папка','Working folder'),
'Preview':('Создать превью','Create preview'), 'Pause':('Пауза','Pause'), 'Resume':('Продолжить','Resume'), 'Cancel':('Остановить','Stop'),
'RecoveryHint':('Готовые части сохраняются после каждого шага. Закрытие занятого окна сворачивает его; задание остаётся в общем управлении фоновыми операциями.','Completed parts are saved after every step. Closing a busy window hides it; the shared background controls keep managing the job.'),
'Original':('Оригинал','Original'), 'Processed':('Результат','Result'), 'Start':('|◀','|◀'), 'PreviousFrame':('◀|','◀|'), 'NextFrame':('|▶','|▶'), 'Play':('▶ / Ⅱ','▶ / Ⅱ'),
'Synchronized':('Синхронное воспроизведение','Synchronized playback'), 'Software':('Программные способы','Ordinary processing'), 'AI':('Нейронные способы и экспорт','Neural processing and export'),
'PreviewCurrent':('Превью соответствует настройкам','Preview matches current settings'), 'PreviewStale':('Создайте превью для текущих настроек и отрывка','Create a preview for the current settings and selection'),
'Up':('↑','↑'), 'Down':('↓','↓'), 'Duplicate':('Повторить шаг','Duplicate step'), 'Remove':('Убрать шаг','Remove step'), 'Reset':('Сбросить настройки шага','Reset step settings'),
'Width':('Ширина','Width'), 'Height':('Высота','Height'), 'Mix':('Доля результата','Result blend'), 'Algorithm':('Алгоритм увеличения','Scaling algorithm'),
'Width.Help':('Целевая ширина в пикселях, чётное число. Пропорции сохраняются с полями. Больше размер — больше времени и памяти.','Target width in pixels, even number. Aspect ratio is preserved with padding. Larger sizes need more time and memory.'),
'Height.Help':('Целевая высота в пикселях, чётное число.','Target height in pixels, even number.'),
'Mix.Help':('0 — исходник, 1 — результат шага. Меньшая доля ослабляет изменения, но не сокращает расчёт.','0 keeps the input, 1 uses the processed result. Lower blending softens changes but does not reduce computation.'),
'Codec':('Кодирование','Encoding'), 'Crf':('Сжатие CRF','CRF compression'), 'Crf.Help':('0–51: меньше число — выше качество и больше файл. Для FFV1 не используется.','0–51: lower means higher quality and larger files. Not used for FFV1.'),
'ExportHelp':('Одна цепочка применяется ко всему видео. Пропорции сохраняются; поля добавляются при необходимости. MP4: все звуковые дорожки → AAC. MKV: звук копируется. FFV1 сохраняет промежуточные кадры без потерь.','One chain applies to the whole video. Aspect ratio is preserved with padding where needed. MP4: all audio tracks → AAC. MKV: audio is copied. FFV1 preserves intermediate frames losslessly.'),
'SavePreset':('Сохранить цепочку…','Save chain…'), 'LoadPreset':('Открыть цепочку…','Open chain…'), 'PresetFilter':('Цепочка апскейла (*.lopataUpscalePreset)|*.lopataUpscalePreset','Upscale chain (*.lopataUpscalePreset)|*.lopataUpscalePreset'),
'ProjectFilter':('Проект апскейла (*.lopataUpscale)|*.lopataUpscale','Upscale project (*.lopataUpscale)|*.lopataUpscale'),
'VideoFilter':('Видео|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.mts;*.m2ts|Все файлы|*.*','Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.mts;*.m2ts|All files|*.*'),
'PreviewRange':('Отрывок превью:','Preview selection:'), 'MaxFive':('не более 5 секунд','maximum 5 seconds'), 'Importing':('Читаем видео и готовим кадры…','Reading video and preparing thumbnails…'),
'Cancelled':('Остановлено. Готовые части сохранены; можно продолжить.','Stopped. Completed parts are saved; you can resume.'),
'Components':('Компоненты апскейла видео','Video upscale components'), 'ComponentsRequired':('Подготовьте компоненты перед продолжением.','Prepare components before continuing.'),
'InvalidProject':('Не удалось прочитать проект или цепочку.','Cannot read the project or chain.'), 'InvalidParameter':('Проверьте настройки: размер, допустимые числа и диапазоны.','Check the sizes, allowed values and parameter ranges.'),
'InvalidSource':('Не удалось определить размер, длительность или частоту видео.','Cannot determine video dimensions, duration or frame rate.'),
'InvalidRange':('Выберите непустой отрывок длительностью не более 5 секунд.','Select a nonempty preview no longer than 5 seconds.'),
'EmptyChain':('Включите хотя бы один шаг.','Enable at least one step.'), 'DecodeMultiple':('Порция декодирования должна быть кратна четырём.','Decoder batch must be a multiple of four.'),
'OutputExists':('Выберите новое имя результата. Исходник и существующие файлы не перезаписываются.','Choose a new output name. Source and existing files are never overwritten.'),
'OutputMismatch':('Результат не прошёл проверку размера или количества кадров. Готовые части сохранены.','Output dimensions or frame count failed validation. Completed parts are saved.'),
'CheckpointDamaged':('Сохранённая часть изменилась или повреждена. Она не будет незаметно использована повторно.','A saved part changed or is damaged. It will not be silently reused.'),
'SourceChanged':('Исходное видео изменилось. Импортируйте его в новый проект.','Source video changed. Import it into a new project.'),
'SourceMissing':('Исходное видео не найдено.','Source video is missing.'),
'CudaRequired':('FlashVSR+ требует совместимую NVIDIA CUDA. Программное увеличение доступно на процессоре.','FlashVSR+ requires compatible NVIDIA CUDA hardware. Ordinary scaling runs on CPU.'),
'DiskSpace':('Недостаточно места для следующей части и промежуточных файлов. Готовые части сохранены.','Insufficient space for the next part and intermediate files. Completed parts are saved.'),
'NativeFailed':('Исполнитель завершился с ошибкой. Подробности сохранены в журнале; автоматического повторения не будет.','The processing tool failed. Details are logged; there is no automatic retry.'),
'Operation':('Апскейл видео','Video upscale'), 'Decoding':('Подготовка кадров','Decoding frames'), 'Encoding':('Кодирование и проверка конечного файла…','Encoding and validating the final file…'), 'Complete':('Готово. Результат сохранён.','Done. Output saved.'),
'ActualSize':('Масштаб 100%','100% view'), 'Fit':('Вписать в окно','Fit to window'),
}
engines = {
'Scale':('Увеличение и фильтры без ИИ','Ordinary scaling and filters','Lanczos, Bicubic, Spline и другие алгоритмы; шумоподавление и резкость. CPU, без скачивания моделей.','Lanczos, Bicubic, Spline and other algorithms; denoise and sharpen. CPU, no model downloads.'),
'Flash':('FlashVSR+','FlashVSR+','Временное восстановление деталей. NVIDIA CUDA; INT8 экономит память. Большое разрешение может резко увеличить время и память.','Temporal detail restoration. NVIDIA CUDA; INT8 saves memory. Large resolutions can greatly increase time and memory.'),
'NeuralRendering':('Творческая реконструкция NR','Creative NR reconstruction','Переосмысление света и фактур через Visual Enhancer. Может изменить лица и детали; это творческая обработка. Требуется совместимая NVIDIA RTX.','Reinterprets lighting and textures through Visual Enhancer. May alter faces and details; a creative effect. Requires compatible NVIDIA RTX.'),
'RtxVideo':('RTX Video Super Resolution','RTX Video Super Resolution','Восстановление и увеличение через NVIDIA RTX Video. Требует поддерживаемый GPU и драйвер.','Restoration and scaling through NVIDIA RTX Video. Requires a supported GPU and driver.'),
'VfxVsr':('NVIDIA VFX Video Super Resolution','NVIDIA VFX Video Super Resolution','Временное увеличение через NVIDIA Video Effects SDK. Проверяется отдельно от RTX Video.','Temporal upscaling through NVIDIA Video Effects SDK. Distinct from RTX Video.'),
'VfxUpscale':('NVIDIA VFX Upscale','NVIDIA VFX Upscale','Пространственное увеличение через NVIDIA VFX; может работать быстрее временной реконструкции.','Spatial scaling through NVIDIA VFX; may run faster than temporal reconstruction.')}
for k,(ru,en,rh,eh) in engines.items(): labels['Engine.'+k]=(ru,en); labels['Engine.'+k+'.Help']=(rh,eh)
params = {
'mode':('Семейство весов','Weight family','Turbo быстрее; Base и v1 скачиваются отдельно.','Turbo is faster; Base and v1 are separate downloads.'),
'precision':('Точность весов','Weight precision','INT8 снижает память; BF16/FP16 используют полные веса и больше VRAM.','INT8 saves memory; BF16/FP16 use full weights and more VRAM.'),
'device':('Устройство CUDA','CUDA device','auto выбирает совместимую карту со свободной памятью; cuda:0, cuda:1 выбирают конкретную.','auto selects a compatible GPU by free memory; cuda:0, cuda:1 select a device.'),
'seed':('Зерно генерации','Random seed','Одинаковое зерно помогает сравнивать настройки; разные зёрна могут менять детали.','A fixed seed helps comparisons; changing it can change details.'),
'decode_every':('Кадров в порции декодера','Decoder batch frames','Кратно 4. Меньше — меньше пиковая память, возможна потеря скорости.','Multiple of 4. Lower reduces peak memory, potentially at the cost of speed.'),
'temporal_compression':('Временное сжатие','Temporal compression','Сжатие обработки по времени. Больше — обычно быстрее, мелкое движение может упрощаться.','Temporal processing compression. Higher is usually faster but may simplify small motions.'),
'temporal_merge':('Объединение временных окон','Temporal window merge','gaussian плавно смешивает границы; first оставляет первый вариант перекрытия.','gaussian blends overlap boundaries; first keeps the first overlap result.'),
'temporal_gaussian_sigma':('Ширина смешивания','Blend width','Ширина весов gaussian на границах временных окон.','Width of gaussian weights across temporal windows.'),
'cache_offload_mode':('Выгрузка кеша в ОЗУ','Cache offload to RAM','always экономит VRAM, never может ускорить, но требует больше видеопамяти; auto решает исполнитель.','always saves VRAM, never may be faster but needs more VRAM; auto lets upstream decide.'),
'offload_original_cache':('Исходный кеш в ОЗУ','Offload original cache','Экономит VRAM за счёт оперативной памяти и копирования.','Saves VRAM at the cost of RAM and transfers.'),
'tiny_decoder_mode':('Параллельность декодера','Decoder parallelism','serial снижает пиковую память; parallel может ускорить при достаточной VRAM.','serial lowers peak memory; parallel may be faster with enough VRAM.'),
'tiny_decoder_channels_last':('Оптимизация размещения каналов','Channels-last optimization','Может ускорить свёртки на совместимой видеокарте.','May speed up convolutions on compatible hardware.'),
'color_correction':('Сохранять цвета исходника','Preserve source colors','Сдерживает сдвиг цветов, но может ослабить творческие изменения.','Reduces color shifts, potentially softening creative changes.'),
'color_correction_levels':('Уровней коррекции цвета','Color correction levels','Число масштабов коррекции; больше — точнее сравнение широких цветовых областей.','Number of correction scales; higher compares broader color structures.'),
'color_correction_chunk_size':('Порция коррекции цвета','Color correction batch','Больше — быстрее при достаточной памяти.','Larger batches may be faster if enough memory is available.'),
'ffn_chunk_tokens':('Размер блока FFN','FFN block size','Меньше — меньше пиковая память. 0 выключает разбиение и может сильно увеличить расход VRAM.','Lower saves peak memory. 0 disables splitting and can greatly increase VRAM use.'),
'nr_style':('Стиль реконструкции','Reconstruction style','Разные характеры изменения света, кожи и фактур. Сравнивайте по превью.','Different treatments of lighting, skin and texture. Compare previews.'),
'nr_intensity':('Сила реконструкции','Reconstruction intensity','Больше — заметнее изменения и выше риск новых деталей.','Higher means stronger changes and more risk of invented details.'),
'nr_passes':('Число проходов','Pass count','Каждый проход добавляет время и может усиливать изменения лица и фактур.','Each pass takes more time and can amplify face and texture changes.'),
'automatic_mask':('Автоматическая маска','Automatic mask','Ограничивает воздействие по областям; выключение даёт модели больше свободы.','Limits treatment by region; turning it off gives the model more freedom.'),
'local_tone_strength':('Местная светотень','Local tone','Сила локальной коррекции света и контраста.','Strength of local lighting and contrast changes.'),
'local_structure_strength':('Локальные фактуры','Local structure','Сила изменения мелких структур. Выше — риск перерисовки.','Strength of local detail changes. Higher increases repainting risk.'),
'skin_structure_strength':('Фактура кожи','Skin structure','Отдельная сила изменений кожи и лиц.','Separate intensity for skin and face structure.'),
'vsr_quality':('Качество RTX VSR','RTX VSR quality','Больше — выше качество и нагрузка.','Higher increases quality and GPU load.'),
'vfx_vsr_mode':('Режим VFX VSR','VFX VSR mode','Профиль исполнителя 0–3: меняет баланс качества и скорости.','Upstream profile 0–3 changes the quality/speed tradeoff.'),
'vfx_vsr_strength':('Сила VFX VSR','VFX VSR strength','Сила восстановления; слишком высокая может подчеркнуть артефакты.','Restoration strength; excessive values may emphasize artifacts.'),
'vfx_upscale_strength':('Сила VFX Upscale','VFX Upscale strength','Сила восстановления контуров при увеличении.','Edge restoration intensity during scaling.'),
'sharpen':('Резкость','Sharpen','0 выключает. Слишком высокая резкость добавляет ореолы по контурам.','0 disables sharpening. Excessive values create edge halos.'),
'denoise':('Шумоподавление','Denoise','0 выключает. Сильное шумоподавление может сгладить мелкие фактуры.','0 disables denoising. Strong settings may smooth fine texture.'),
}
for k,(ru,en,rh,eh) in params.items(): labels['Param.'+k]=(ru,en); labels['Param.'+k+'.Help']=(rh,eh)
for lang,i in [('ru',0),('en',1)]:
    path=APP/'Localization'/f'{lang}.json'; text=path.read_text(encoding='utf-8-sig'); existing=json.loads(text)
    additions={'VideoUpscale.'+k:v[i] for k,v in labels.items() if 'VideoUpscale.'+k not in existing}
    for tag,key in [('video_upscale','Title'),('video_upscale_preview','PreviewRange'),('video_upscale_chain','SavePreset')]:
        additions['Cloud.Tag.'+tag]=labels[key][i].replace('…','').rstrip(':')
        additions['Cloud.Description.'+tag]=labels['Description'][i]
    # Catalog's exact key conventions are validated by navigation tests.
    insertion='\n'.join('  '+json.dumps(k,ensure_ascii=False)+': '+json.dumps(v,ensure_ascii=False)+',' for k,v in additions.items())
    path.write_text(text.replace('{','{\n'+insertion,1),encoding='utf-8')

dest=APP/'Licenses/texts'; dest.mkdir(exist_ok=True)
source=ROOT/'Runtime/VideoUpscaleStand/tools/flashvsr-t8'
(dest/'video-upscale-flash-LICENSE.txt').write_bytes((source/'LICENSE').read_bytes())
for name in ['LICENSE','THIRD-PARTY-NOTICES']:
    with urllib.request.urlopen('https://raw.githubusercontent.com/Merserk/dlss5-visual-enhancer/v21.0/'+name) as f: content=f.read()
    (dest/('video-upscale-ve-'+name+'.txt')).write_bytes(content)
notices=['Pinned Python 3.10.18 / PyTorch 2.7 CUDA runtime. Original installed package licence texts follow.\n']
notices.append((ROOT/'Runtime/VideoUpscaleStand/python/cpython-3.10.18-windows-x86_64-none/LICENSE.txt').read_text(encoding='utf-8'))
for d in sorted(meta.distributions(), key=lambda d:d.metadata['Name']):
    notices.append('\n\n=== '+d.metadata['Name']+' '+d.version+' ===\n')
    for f in d.files or []:
        if any(x in str(f).lower() for x in ('license','licence','copying','notice')) and '.dist-info/' in str(f).replace('\\','/'):
            p=d.locate_file(f)
            if p.is_file(): notices.append('\n'+str(f)+'\n'+p.read_text(encoding='utf-8',errors='replace'))
(dest/'video-upscale-runtime-NOTICES.txt').write_text(''.join(notices),encoding='utf-8')
entries=[]
def entry(id,name,version,license,page,texts,ru,en):
    entries.append(dict(Id=id,Name=name,Version=version,Author='Upstream authors',License=license,Source=page,Checked='2026-11-04',Ru=ru,En=en,Basic=False,Delivery='optional-download',Terms='',Texts=['texts/'+x for x in texts]))
entry('video-upscale-flash-runtime','FlashVSR+ Python CUDA runtime','3.10.18 / 2.7.0 cu128','PSF; BSD; Apache-2.0; MIT; NVIDIA CUDA','https://pytorch.org/',['video-upscale-runtime-NOTICES.txt'],'Отдельный runtime, без изменений системного Python. Условия вложенных библиотек и NVIDIA сохраняются.','Separate runtime; system Python remains unchanged. Included dependency and NVIDIA terms apply.')
entry('video-upscale-flash-source','FlashVSR+ T8 ModelHandle','ac60d1e3','Apache-2.0','https://github.com/T8mars/Comfyui-Flashvsr-Plus-T8',['video-upscale-flash-LICENSE.txt'],'Готовая интеграция T8. ЛОПАТА добавляет ввод/вывод и FFV1 без изменения нейронного алгоритма.','Ready T8 integration. LOPATA adds I/O and FFV1 without changing the neural algorithm.')
for mode in ['turbo','base','v1']:
    for precision in ['int8','float']:
        entry(f'video-upscale-flash-{mode}-{precision}',f'FlashVSR+ {mode} {precision}','pinned-20261104','Apache-2.0','https://huggingface.co/ruchangyao/FlashVSR_plus',['video-upscale-flash-LICENSE.txt'],'Веса FlashVSR; вариант скачивается отдельно. Нейронное восстановление может изменять детали.','FlashVSR weights; downloaded per variant. Neural restoration may alter details.')
entry('video-upscale-visual-enhancer','Visual Enhancer CLI','21.0','Merserk Source License; third-party terms','https://github.com/Merserk/dlss5-visual-enhancer/releases/tag/v21.0',['video-upscale-ve-LICENSE.txt','video-upscale-ve-THIRD-PARTY-NOTICES.txt'],'Проприетарный внешний исполнитель: скачивается только с официального канала автора. Перепаковка и зеркалирование без разрешения запрещены. Использование результатов допускается по условиям автора и вложенных компонентов.','Proprietary external tool downloaded only from the author’s official channel. Repackaging or mirroring requires permission. Outputs may be used under the author and third-party terms.')
(ROOT/'Тесты/VideoUpscaleStand/upscale-license-entries.json').write_text(json.dumps(entries,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
