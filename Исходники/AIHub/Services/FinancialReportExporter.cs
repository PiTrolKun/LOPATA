using System.Globalization;
using System.IO;
using System.Text;
using AIHub.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Services;

public static class FinancialReportExporter
{
    public static string OutputFileName(string id, string language, DateTimeOffset timestamp, string extension)
    {
        if (id != "calculation" && !FinancialAnalysisPlan.Stages.Any(stage => stage.Id == id && stage.Report))
            throw new ArgumentOutOfRangeException(nameof(id));
        if (extension is not ("md" or "docx")) throw new ArgumentOutOfRangeException(nameof(extension));
        var localization = new LocalizationService(); localization.Load(language);
        var label = localization.T("Finance.File." + id);
        var invalid = Path.GetInvalidFileNameChars();
        label = string.Concat(label.Select(c => char.IsWhiteSpace(c) || invalid.Contains(c) ? '_' : c)).Trim('.', '_');
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_" + label + "." + extension;
    }
    public static string CalculationText(FinancialInput input, FinancialCalculation c, Func<string, string> l, FinancialPeriod period = FinancialPeriod.Month)
    {
        string Amount(decimal value) => FinancialCalculator.ForPeriod(value, period).ToString("N2", input.Language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU")) + " " + FinancialCurrencies.Resolve(input.Unit, input.Language);
        var text = new StringBuilder().AppendLine(c.IsComplete ? l("Finance.Complete") : l("Finance.Incomplete"))
            .AppendLine(l("Finance.Normalization"))
            .AppendLine(l("Finance.Period") + ": " + l("Finance.Period." + period));
        if (input.Schema == 2) text.AppendLine(l("Finance.PersonalBasis"));
        foreach (var pair in new (string, decimal)[] { ("StableIncome", c.StableIncome), ("AdditionalIncome", c.AdditionalIncome), ("Income", c.Income), ("Expenses", c.Expenses), ("External", c.External), ("Personal", c.Personal), ("Balance", c.Balance), ("Required", c.RequiredPersonal), ("Other", c.OtherPersonal) }.Where(p => input.Schema == 1 || p.Item1 is not ("Expenses" or "External")))
            text.AppendLine(l("Finance.Total." + pair.Item1) + ": " +
                (pair.Item1 == "StableIncome" && c.Missing.Contains("stable_income") || pair.Item1 == "AdditionalIncome" && c.Missing.Contains("additional_income")
                    ? l("Finance.Unknown") : Amount(pair.Item2)));
        text.AppendLine();
        foreach (var g in c.Categories)
            text.AppendLine(l("Finance.Category." + g.Id) + ": " + (input.Schema == 2 ? Amount(g.Personal) : Amount(g.Total) + "; " + l("Finance.Total.Personal") + " " + Amount(g.Personal) + "; " + l("Finance.Total.External") + " " + Amount(g.External)) + (g.Share.HasValue ? $"; {g.Share:0.00}%" : ""));
        text.AppendLine(l("Finance.Ratio") + ": " + (c.StableCoverageRatio?.ToString("0.00", CultureInfo.InvariantCulture) ?? l("Finance.Unavailable")));
        if (!c.IsComplete) text.AppendLine(l("Finance.Missing") + ": " + string.Join(", ", c.Missing.Select(id => l("Finance.Question." + id))));
        if (input.Schema == 2)
            foreach (var a in input.Answers.Where(a => a.Coverage != FinancialCoverage.Self))
                text.AppendLine(l("Finance.Question." + a.Id) + ": " + l("Finance.Coverage." + a.Coverage));
        return text.ToString();
    }
    public static void Export(FinancialRunStore store, string id, FinancialCalculation calculation)
    {
        var run = store.Load(); var stage = store.ReadStage(id) ?? throw new InvalidDataException();
        var localization = new LocalizationService(); localization.Load(run.Input.Language);
        var text = "# " + localization.T("Finance.Title") + " — " + localization.T("Finance.Stage." + id) + "\n\n" +
            CalculationText(run.Input, calculation, localization.T) + "\n\n" + localization.T("Finance.ModelDraft") + "\n\n" + stage.Text +
            "\n\n" + stage.ModelName + " · " + stage.CompletedUtc.ToString("u") + "\n";
        store.WriteText(OutputFileName(id, run.Input.Language, stage.CompletedUtc, "md"), text);
        if (id != "final") return;
        var finalPath = store.Checked(OutputFileName(id, run.Input.Language, stage.CompletedUtc, "docx"));
        var temp = store.Checked(Path.GetFileName(finalPath) + ".tmp");
        using (var doc = WordprocessingDocument.Create(temp, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var part = doc.AddMainDocumentPart(); part.Document = new Document(new Body());
            foreach (var line in text.Split('\n')) part.Document.Body!.Append(new Paragraph(new Run(new Text(line.TrimEnd('\r')) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
            part.Document.Save();
        }
        System.IO.File.Move(temp, store.Checked(Path.GetFileName(finalPath)), true);
    }
}
