using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

static class NovelFixture
{
    public const string Title = "Тихий шлюз Вейраны";
    public const string Draft = "Нэлва Риан остановилась у закрытого шлюза. Орт Девель держал пустую коробку. Они ещё не решили, следует ли открывать проход.";
    public static readonly string[] Sections =
    [
        """
        Тихий шлюз Вейраны. Учебный вымышленный первоисточник. Автор: Стенд ЛОПАТЫ.
        1. Ожидание
        В Вейране воду выдавали по звуку колокола. Нэлва Риан чинила трубы, а Орт Девель записывал уровень воды в узкую тетрадь. Они не были родственниками. Орт приходился Нэлве бывшим наставником и теперь работал вместе с ней на равных.
        На старой доске возле шлюза было написано: «Ключ, зеркало, соль». Сторож уверял, что эти три вещи открывают проход. Нэлва переписала надпись, но Орт заметил: доска относится к давно снятому механизму. Они решили проверить чертёж, прежде чем верить сторожу.
        Из нижнего города пришёл слух, будто у шлюза никто не выживает. Нэлва спрятала тетрадь в ящик и сказала, что в случае беды Орт должен отнести её сестре. Однако сестра жила далеко и в дальнейшем не участвовала в событиях. До полуночи герои ждали лодку с чертежом. Лодочник привёз вместо чертежа пустой конверт: настоящий документ уже находился в ремонтной мастерской.
        """,
        """
        Тихий шлюз Вейраны. Автор: Стенд ЛОПАТЫ.
        2. Настоящее устройство
        В мастерской Нэлва нашла действующий чертёж. Для открытия шлюза требовались ровно три предмета в строгом порядке: медная нить, белый камень, чёрная чашка. Сначала нить соединяла контакты, затем камень прижимал рычаг, а чашка принимала первую порцию воды. Ключ, зеркало и соль со старой доски были бесполезны: это осталось от прежнего устройства.
        На полях стоял код допуска ЖУ-5831. Он обозначал разрешение мастера на испытание, а не номер шлюза и не дату. Орт прочёл его дважды, чтобы не перепутать цифры. Нэлва нашла медную нить в мотке, камень взяла с подоконника, а чёрную чашку принесла из буфета. Другие предметы для открытия не понадобились.
        После установки чашки вода пошла слишком быстро. Орт хотел убрать камень, но Нэлва велела подождать: пустая труба должна была заполниться. Их спор закончился, когда поток сам ослаб. Открытый проход вывел воду к заброшенному соляному саду.
        """,
        """
        Тихий шлюз Вейраны. Автор: Стенд ЛОПАТЫ.
        3. После паводка
        Утром в Вейране объявили, что Нэлва утонула. Кто-то увидел её пустую лодку и принял догадку за известие. Орт не поверил слуху и прошёл вдоль трубы. На другом берегу Нэлва сушила куртку и записывала повреждения. Она была жива и не получила тяжёлых ранений.
        В финале Нэлва Риан уехала в Кевар и стала смотрительницей соляного сада. Она не умерла, не попала в тюрьму и не вернулась к ремонту городских труб. Это было её собственное решение: саду нужен был человек, понимающий устройство шлюза. Орт остался в Вейране и продолжил вести журнал уровня воды; он не поехал вместе с ней.
        Последняя запись Нэлвы была обычным списком: починить калитку, перенести саженцы, встретить нового сторожа. Никакой тайной угрозы за этим списком не скрывалось. Местные дети прозвали её хозяйкой сухого дождя, но официальная должность оставалась прежней — смотрительница соляного сада. На этом рассказ заканчивается.
        """
    ];
    public static string FullText => string.Join("\n\n", Sections);
    public static Case[] Cases() =>
    [
        new("novel_end", LiteraryChatProfile.Advisor, "Чем заканчивается история Нэлвы Риан в оригинале «Тихий шлюз Вейраны»? Где она оказалась и чем стала заниматься? Ответь коротко.", "Финал Нэлвы Риан: где она оказалась и кем стала?", true, Draft),
        new("novel_objects", LiteraryChatProfile.Advisor, "Какие ровно три предмета и в каком порядке действительно открывают шлюз в оригинале «Тихий шлюз Вейраны»? Хочу сохранить это правило. Ответь коротко.", "Три предмета для открытия шлюза в строгом порядке", true, Draft),
        new("novel_change", LiteraryChatProfile.Advisor, "В моей версии Нэлва остаётся в Вейране и становится учительницей. Это принятое изменение. Сравни с финалом оригинала «Тихий шлюз Вейраны» и предложи один способ связать версии. Не отменяй моё решение.", "Финал Нэлвы Риан: где она оказалась и кем стала?", true, Draft)
    ];
    public static async Task Prepare(string root, string output, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var materials = Path.Combine(root, "Materials"); Directory.CreateDirectory(materials);
        var paths = Sections.Select((text, i) => {
            var path = Path.Combine(materials, $"veyrana-{i+1:D2}.txt"); File.WriteAllText(path, text); return path;
        }).ToArray();
        var project = new LiteraryProject { ProjectName = "Novel source probe", WorkTitle = Title,
            BasedOnExistingWorld = true, WorldSource = Title, Materials = paths.Select((p, i) => $"Materials/{i+1:D4}_" + Path.GetFileName(p)).ToList() };
        File.WriteAllText(Path.Combine(root, "project.json"), JsonSerializer.Serialize(project));
        File.WriteAllText(Path.Combine(output, "ground-truth.json"), JsonSerializer.Serialize(new {
            fixedBeforeRequestsUtc = DateTimeOffset.UtcNow, title = Title, author = "Стенд ЛОПАТЫ",
            ending = "Жива; уехала в Кевар; смотрительница соляного сада",
            objects = new[] { "медная нить", "белый камень", "чёрная чашка" }, code = "ЖУ-5831",
            acceptedChange = "Остаётся в Вейране и становится учительницей",
            sourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FullText)))
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        await using var index = new LiterarySourceIndex(projectRoot: root);
        await index.PrepareAsync(paths, new Progress<LiteraryPreparationProgress>(p => Console.WriteLine("INDEX " + p)), ct);
        index.CopyInto(root); index.Commit();
        for (var i = 0; i < paths.Length; i++)
            File.Move(paths[i], Path.Combine(materials, $"{i+1:D4}_" + Path.GetFileName(paths[i])));
        var store = new LiteraryChapterStore(root); store.Open(); store.Save(Draft);
    }
}
