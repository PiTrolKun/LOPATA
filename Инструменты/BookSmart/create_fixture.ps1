Add-Type -AssemblyName System.Drawing
$root='H:\AI_HUB\Тесты\BookSmartStand'
$bitmap=[System.Drawing.Bitmap]::new(1200,1600)
$g=[System.Drawing.Graphics]::FromImage($bitmap)
$g.Clear([System.Drawing.Color]::White)
$g.TextRenderingHint=[System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$heading=[System.Drawing.Font]::new('Arial',32,[System.Drawing.FontStyle]::Bold)
$body=[System.Drawing.Font]::new('Arial',22)
$foot=[System.Drawing.Font]::new('Arial',18)
$black=[System.Drawing.Brushes]::Black
$g.DrawString('ЛОПАТА: проверка распознавания',$heading,$black,70,70)
$g.DrawString('Глава 1. Две колонки',$body,$black,70,150)
$left=@('На берегу реки стоял старый дом.','По утрам окна отражали солнце.','В библиотеке хранились книги.','Каждая книга имела свой номер.','','Первая колонка читается сверху','вниз, затем начинается вторая.','Это важное правило порядка¹.','','Числа: 17, 2048 и 3,14.','Буквы: ё, й, щ, ъ, ы, э.','Кавычки: «точный текст».')
$right=@('Вечером пошёл тихий дождь.','Путешественник открыл тетрадь.','Он записал название деревни.','Никаких новых слов добавлять','в распознанный текст не нужно.','','Таблица измерений:','День        Температура','Понедельник       12 °C','Вторник           15 °C','','Конец второй колонки.')
for($i=0;$i -lt $left.Count;$i++) { $g.DrawString($left[$i],$body,$black,70,(240+$i*64)) }
for($i=0;$i -lt $right.Count;$i++) { $g.DrawString($right[$i],$body,$black,640,(240+$i*64)) }
$g.DrawLine([System.Drawing.Pens]::Black,70,1250,1130,1250)
$g.DrawString('¹ Примечание: читать колонки последовательно.',$foot,$black,70,1280)
$g.DrawString('1',$foot,$black,580,1500)
$bitmap.Save((Join-Path $root 'russian-two-columns.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bitmap.Dispose();$heading.Dispose();$body.Dispose();$foot.Dispose()
