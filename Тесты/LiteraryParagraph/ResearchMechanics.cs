using System.IO;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;

// Explicitly invoked fixture exporter; no entry point, model generation or automatic registration.
internal static class ResearchMechanics
{
    private const string HiddenMemory = "ЖЕЛЕ-НЕВЫБРАНО-417";
    private const string HiddenReference = "RAG-НЕВЫБРАНО-693";
    private const string OldTask = "Инженер отдал серебряный ключ Мирону и улетел в космос. ОТВЕРГНУТО-812.";
    private const string Draft = "У закрытой двери мастерской инженер остановился. Изнутри доносился ровный гул трансформатора.";
    private const string FinalTask = "Продолжи одним неторопливым художественным абзацем: инженер перед закрытой дверью " +
        "проверяет в левом кармане свой латунный ключ от мастерской. Покажи короткое ощупывающее движение и паузу. " +
        "Не открывай дверь, не вводи других людей, не переходи к космическому полёту. " +
        "Справочные детали используй лишь если они помогают этому моменту.";

    internal static async Task Run()
    {
        var run = Program.Create("_research-mechanics-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine("RUN " + run);
        // Program.Create retains the original copy and before-hashes; only this fresh child is indexed.
        // Keep native database paths short; the first deeply nested fixture failed with Gridstore IO/os error 3.
        var root = Path.Combine(Path.GetFullPath("_tmp"), "rm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Program.Save(run, "execution-plan.json", new
        {
            root, productionChanged = false, generationCalls = 0,
            order = new[] { "create local chapters and approved memory", "reference index: Giga then local Qdrant",
                "completed-part index: Giga then local Qdrant", "scoped reads: query Giga CPU and local Qdrant",
                "export packets; verify original hashes" },
            limitation = "Packet/evidence exclusion is checked; catalog metadata and revision hashes can inspect unselected sources. No UI automation."
        });
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            await ExportFixture(run, root, deadline.Token);
        }
        catch (Exception error)
        {
            Program.Save(run, "fixture-error.json", new { type = error.GetType().FullName, error.Message, detail = error.ToString() });
            throw;
        }
        finally
        {
            var original = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "source.json")))!["source"]!.GetValue<string>();
            var after = Program.Hashes(original);
            Program.Save(run, "original-hashes-after.json", after);
            var before = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "original-hashes-before.json")));
            var unchanged = JsonNode.DeepEquals(before, JsonNode.Parse(ParagraphJson.Encode(after)));
            Program.Save(run, "original-preservation.json", new { original, unchanged });
            Require(unchanged, "Original project changed during fixture export.");
        }
    }

    private static async Task ExportFixture(string run, string root, CancellationToken ct)
    {
        var project = new LiteraryProject
        {
            ProjectName = "Исследование: инженер у двери", WorkTitle = "Пауза перед мастерской", LanguageCode = "ru",
            CreationBrief = ParagraphJson.Encode(new
            {
                sections = new[] { new { Topic = 1, Question = "Локальный замысел", Text = "Инженер у мастерской; реалистическая сцена без спешки." } },
                route = new[]
                {
                    new { Number = 1, Title = "Передача ключа", Description = "Мирон дарит инженеру ключ. Этот этап уже написан." },
                    new { Number = 2, Title = "Пауза у закрытой двери", Description = "Инженер проверяет ключ в кармане и прислушивается. Дверь пока закрыта." },
                    new { Number = 3, Title = "Позднее", Description = "Возможный будущий полёт; ещё не совершённое событие." }
                }
            })
        };
        Program.Save(root, "project.json", project);
        var layout = new LiteraryProjectLayout(root);
        layout.Initialize(); layout.CommitLayout();
        var chapters = new LiteraryChapterStore(root); chapters.Open();
        const string accepted = "Мирон подарил инженеру латунный ключ от мастерской. " +
            "Теперь ключ принадлежит инженеру. Инженер положил ключ в левый карман. Космический полёт оставался лишь мечтой.";
        chapters.Rename("Передача ключа"); chapters.Save(accepted);
        var acceptedPart = chapters.Active.Id;
        var acceptedNumber = LiteraryEditorSnapshot.Capture(project.Id, root, chapters.Index, accepted, false).Active.Number;
        chapters.Finish();
        var facts = new LiteraryJellyFact[]
        {
            new() { Subject = "Латунный ключ от мастерской", Relation = "принадлежит", Value = "инженеру", Kind = "property",
                Evidence = "Теперь ключ принадлежит инженеру." },
            new() { Subject = "Инженер", Relation = "положил", Value = "латунный ключ в левый карман", Kind = "event",
                Evidence = "Инженер положил ключ в левый карман." }
        };
        var jelly = new LiteraryJellyStore(layout);
        Confirm(jelly, acceptedPart, acceptedNumber, accepted, facts);
        var excludedText = "Сигнальщик записал пароль " + HiddenMemory + " в закрытый блокнот.";
        chapters.Rename("Служебная контрольная часть"); chapters.Save(excludedText);
        var excludedPart = chapters.Active.Id;
        var excludedNumber = LiteraryEditorSnapshot.Capture(project.Id, root, chapters.Index, excludedText, false).Active.Number;
        chapters.Finish();
        Confirm(jelly, excludedPart, excludedNumber, excludedText,
            [new() { Subject = "Сигнальщик", Relation = "записал", Value = HiddenMemory, Kind = "event", Evidence = excludedText }]);
        chapters.Rename("Пауза у двери"); chapters.Save(Draft);
        Require(jelly.Read().Count == 3, "Approved fixture memory was not stored.");
        Program.Save(run, "fixture-authorship.json", new
        {
            project.Id, root, acceptedPart, acceptedNumber, excludedPart, excludedNumber,
            manualFixtureDecisions = true, automaticMemoryExtraction = false,
            accepted, facts, excludedText, memory = jelly.Read(), editor = Draft, finalTask = FinalTask
        });

        var inputs = layout.EnsureFolder("fixture-inputs");
        var references = new[]
        {
            (Name: "key.txt", Text: "На латунном ключе от мастерской есть три поперечные насечки. Их можно различить пальцем, не вынимая ключ из кармана."),
            (Name: "festival.txt", Text: "На площади соседнего города раз в семь лет устраивают праздник медных чайников. Этот обычай не связан с мастерской и её ключом."),
            (Name: "unchecked.txt", Text: "Запись другого проекта: пароль маяка " + HiddenReference + ".")
        };
        foreach (var item in references) File.WriteAllText(Path.Combine(inputs, item.Name), item.Text);
        // These are the first calls that can start Giga/Qdrant. The coordinator runs this exporter explicitly.
        var progress = new Progress<LiteraryPreparationProgress>(p => Console.WriteLine("PREPARE " + p.Stage));
        await using (var index = new LiterarySourceIndex(projectRoot: root))
        {
            await index.PrepareAsync(references.Select(x => Path.Combine(inputs, x.Name)).ToArray(), progress, ct);
            index.CopyInto(root);
            for (var i = 0; i < references.Length; i++)
                File.Copy(Path.Combine(inputs, references[i].Name), Path.Combine(root, "Materials", $"{i + 1:D4}_" + references[i].Name));
            index.Commit();
        }
        await new LiteraryWorkIndex(layout).PrepareAsync(progress, ct);

        var localize = new LocalizationService(); localize.Load("ru");
        var editor = LiteraryEditorSnapshot.Capture(project.Id, root, chapters.Index, chapters.Load(), false);
        var catalog = new LiteraryParagraphCatalog(project, editor, localize.T);
        var scopes = new LiteraryRagReader(editor).ReferenceScopes();
        Program.Save(run, "reference-scopes.json", scopes);
        Program.Save(run, "catalog.json", new { catalog.Roots, catalog.Routes });
        string Scope(string name) => scopes.Single(s => s.Source == name).Number;
        var keyScope = Scope("0001_key.txt"); var noiseScope = Scope("0002_festival.txt"); var hiddenScope = Scope("0003_unchecked.txt");
        string Node(string kind, string key) => catalog.Nodes.Values.Single(n => n.Kind == kind && n.Key == key).Id;
        var selected = new Dictionary<string, ParagraphSelection>
        {
            [Node("chapter", acceptedNumber)] = new() { Selected = true },
            [Node("ragPart", acceptedPart)] = new() { Selected = true },
            [Node("ragSection", keyScope)] = new() { Selected = true },
            [Node("ragSection", noiseScope)] = new() { Selected = true },
            [Node("jellySubject", ParagraphJson.Encode(new[] { "property", "Латунный ключ от мастерской" }))] = new() { Selected = true },
            [Node("jellySubject", ParagraphJson.Encode(new[] { "event", "Инженер" }))] = new() { Selected = true }
        };
        var excludedNodes = new[]
        {
            Node("chapter", excludedNumber), Node("ragPart", excludedPart), Node("ragSection", hiddenScope),
            Node("jellySubject", ParagraphJson.Encode(new[] { "event", "Сигнальщик" }))
        };
        Program.Save(run, "scope-selection.json", new { selected, excludedNodes, keyScope, noiseScope, hiddenScope });

        var store = new LiteraryParagraphStore(layout);
        var state = store.Load(); state.RouteId = "route/1"; state.Selection = selected;
        state.Request = "Помоги подготовить короткое действие инженера перед дверью.";
        state.RecordExchange(LiteraryChatProfile.Advisor, state.Request, OldTask);
        state.Prepared = OldTask; state.Stage = ParagraphStage.Prepared;
        store.Save(state); Program.Save(run, "screen-before-edit.json", store.Load());
        state.Prepared = FinalTask; state.RouteId = "route/2";
        store.Save(state); state = store.Load();
        Require(state.History.Any(t => t.Text == OldTask), "Screen history did not retain the rejected proposal.");
        Require(state.Prepared == FinalTask && state.RouteId == "route/2", "Latest manual edit or switched route was lost.");
        Program.Save(run, "screen-after-edit.json", state);
        var request = new ParagraphRequest(LiteraryChatProfile.Writer, state.Prepared, editor, state.History.ToArray(),
            state.Selection, state.RouteId, state.SessionId);
        var first = await ExportRequest(run, "selected", project, catalog, request, ct);
        ValidateSelected(first.Evidence, keyScope, noiseScope, hiddenScope, acceptedNumber, excludedNumber, facts);
        ValidateWriter(first.Packet, FinalTask);

        // A fresh reader instance checks zero selections; this does not assert zero metadata/hash file reads.
        var emptyRequest = request with { Selection = new Dictionary<string, ParagraphSelection>() };
        var empty = await ExportRequest(run, "unselected", project, catalog, emptyRequest, ct);
        Require(empty.Evidence.Materials.Count == 0 && empty.Evidence.Receipts.Count == 0, "Unchecked source content entered evidence.");
        Require(empty.Packet["materials"]!.AsArray().Count == 0, "Unchecked source content entered packet.");
        ValidateWriter(empty.Packet, FinalTask);

        var previousSession = state.SessionId;
        state.Clear(); store.Save(state); state = store.Load();
        Require(state.SessionId != previousSession && state.History.Count == 0 && state.Prepared.Length == 0
            && state.Request.Length == 0 && state.Stage == ParagraphStage.Request, "Clear did not reset the session.");
        Require(state.RouteId == "route/2" && state.Selection.Keys.Order().SequenceEqual(selected.Keys.Order())
            && state.Selection.Values.All(s => s.Selected), "Clear lost the selected route or sources.");
        Program.Save(run, "screen-after-clear.json", state);
        state.Request = "Продолжаем ту же паузу после очистки разговора.";
        state.Prepared = FinalTask; state.Stage = ParagraphStage.Prepared; store.Save(state);
        var afterClear = request with { Task = state.Prepared, History = state.History.ToArray(),
            Selection = state.Selection, RouteId = state.RouteId, SessionId = state.SessionId };
        var last = await ExportRequest(run, "selected-after-clear", project, catalog, afterClear, ct);
        ValidateSelected(last.Evidence, keyScope, noiseScope, hiddenScope, acceptedNumber, excludedNumber, facts);
        ValidateWriter(last.Packet, FinalTask);
        Program.Save(run, "fixture-result.json", new
        {
            fixtureRoot = root, selectedScopes = selected.Count, manualTaskRetained = true,
            writerHistoryEmpty = true, routeAfterSwitchAndClear = state.RouteId,
            uncheckedContentExcluded = true, rawEvidencePacketExported = true, literaryGenerationCalls = 0,
            embeddingCallsExpected = true,
            nextModelInput = "selected-after-clear-packet.json",
            limitation = "Service/state/packet checks only; generation quality and WPF delivery have not been tested."
        });
        Console.WriteLine("FIXTURE EXPORTED " + run);
    }

    private static void Confirm(LiteraryJellyStore jelly, string part, string number, string text, LiteraryJellyFact[] facts)
    {
        var batch = new LiteraryJellyBatch(Guid.NewGuid().ToString("N"), part, number, LiteraryWorkIndex.Revision(text), text, facts);
        jelly.Stage(batch); jelly.Confirm(batch, facts);
        Require(jelly.Find(part, batch.Revision)?.Status == "confirmed", "Memory confirmation did not persist.");
    }

    private static async Task<(ParagraphEvidence Evidence, JsonObject Packet)> ExportRequest(string run, string name,
        LiteraryProject project, LiteraryParagraphCatalog catalog, ParagraphRequest request, CancellationToken ct)
    {
        var revision = LiteraryParagraphRevision.Capture(request.Editor);
        Program.Save(run, name + "-raw.json", request);
        Program.Save(run, name + "-revision-before.json", revision);
        var receiptEvents = new List<ParagraphReceipt>();
        var evidence = await new LiteraryParagraphSources(project, request.Editor, catalog).ReadAsync(request.Task,
            request.Selection, r => { receiptEvents.Add(r); Program.Save(run, name + "-receipt-events.json", receiptEvents); }, ct);
        Program.Save(run, name + "-evidence.json", evidence);
        var after = LiteraryParagraphRevision.Capture(request.Editor);
        Program.Save(run, name + "-revision-after.json", after);
        Require(revision == after, "Project revision changed during source reading.");
        Require(evidence.Complete, "One or more selected source reads failed; inspect saved receipts.");
        Require(evidence.Receipts.Select(r => r.Id).Order().SequenceEqual(request.Selection.Where(s => s.Value.Selected).Select(s => s.Key).Order()),
            "Receipts do not match selected scopes.");
        var materialIds = evidence.Materials.Select(m => m.Id).ToHashSet();
        Require(evidence.Receipts.SelectMany(r => r.Materials).ToHashSet().SetEquals(materialIds), "Receipt/material linkage is incomplete.");
        var packet = JsonNode.Parse(ParagraphJson.Encode(LiteraryParagraphPacket.Build(request, evidence, catalog)))!.AsObject();
        Program.Save(run, name + "-packet.json", packet);
        return (evidence, packet);
    }

    private static void ValidateSelected(ParagraphEvidence evidence, string keyScope, string noiseScope, string hiddenScope,
        string acceptedNumber, string excludedNumber, LiteraryJellyFact[] expectedFacts)
    {
        Require(evidence.Receipts.All(r => r.Status == "found"), "Selected fixture scope returned no material.");
        var materials = evidence.Materials.Select(m => (m.Kind, Data: JsonNode.Parse(ParagraphJson.Encode(m.Data))!)).ToArray();
        foreach (var scope in new[] { keyScope, noiseScope })
            Require(materials.Any(m => m.Kind == "original_book_fragment" && m.Data["number"]?.ToString() == scope), "Selected reference section missing.");
        Require(!materials.Any(m => m.Kind == "original_book_fragment" && m.Data["number"]?.ToString() == hiddenScope), "Unchecked RAG section was returned.");
        Require(materials.Any(m => m.Kind == "completed_project_fragment" && m.Data["number"]?.ToString() == acceptedNumber), "Selected project RAG part missing.");
        Require(materials.Any(m => m.Kind == "completed_project_part"), "Selected direct chapter read missing.");
        foreach (var fact in expectedFacts)
            Require(materials.Any(m => m.Kind == "confirmed_project_memory" && m.Data["Fact"]?["Id"]?.ToString() == fact.Id), "Approved owner/location fact missing.");
        Require(!materials.Any(m => m.Kind == "completed_project_fragment" && m.Data["number"]?.ToString() == excludedNumber), "Unchecked project RAG part was returned.");
        var text = ParagraphJson.Encode(evidence);
        Require(!text.Contains(HiddenMemory, StringComparison.Ordinal) && !text.Contains(HiddenReference, StringComparison.Ordinal), "Unchecked content leaked into evidence.");
    }

    private static void ValidateWriter(JsonObject packet, string task)
    {
        Require(packet["task"]?.GetValue<string>() == task, "Writer did not receive the last manual task.");
        Require(packet["history"]!.AsArray().Count == 0 && packet["stage"]?.ToString() == "Writer", "Writer role/history contract failed.");
        Require(packet["currentRouteId"]?.ToString() == "route/2"
            && packet["current_route"]?["Title"]?.ToString() == "Пауза у закрытой двери", "Current route was not delivered.");
        var text = ParagraphJson.Encode(packet);
        Require(!text.Contains(OldTask, StringComparison.Ordinal) && !text.Contains(HiddenMemory, StringComparison.Ordinal)
            && !text.Contains(HiddenReference, StringComparison.Ordinal), "Rejected or unchecked content leaked into Writer packet.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
