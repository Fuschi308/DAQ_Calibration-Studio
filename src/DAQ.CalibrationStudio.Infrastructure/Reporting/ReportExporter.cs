using System.Globalization;
using System.Text;
using DAQ.CalibrationStudio.Domain;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace DAQ.CalibrationStudio.Infrastructure.Reporting;

/// <summary>Exports immutable run snapshots as German CSV and paginated PDF reports.</summary>
public sealed class ReportExporter
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-AT");

    public void WriteCsv(string path, RunResult run, IReadOnlyList<Approval>? approvals = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(run);
        approvals ??= [];
        var output = new StringBuilder();
        Row(output, "Berichtstyp", ReportTitle(run));
        Row(output, "Prüflauf", run.DisplayId);
        Row(output, "Beginn", run.Started.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz", German));
        Row(output, "Ende", run.Finished.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz", German));
        Row(output, "Prüfer", run.Operator);
        Row(output, "Prüfbedingungen", run.Conditions);
        Row(output, "Prüfobjekt", run.Daq.Model);
        Row(output, "Seriennummer", run.Daq.Serial ?? "nicht angegeben");
        Row(output, "Hardware-Revision", run.Daq.Revision ?? "nicht angegeben");
        Row(output, "Firmware", run.Daq.Firmware ?? "nicht angegeben");
        Row(output, "Prüfprofil", $"{run.Profile.Name} (Version {run.Profile.Version})");
        Row(output, "Prüfergebnis", OutcomeText(run.Outcome));
        Row(output, "Messdatenherkunft", run.Simulated ? "Simulation – keine reale DAQ-Messung" : "DAQ-Messlauf");
        Row(output, "Rückführbarkeit", TraceabilityText(run.References));
        Row(output, "Fehlerhinweis", run.Error ?? "");
        output.AppendLine();
        Row(output, "Referenzgeräte");
        Row(output, "Modell", "Seriennummer", "Genauigkeit", "Kalibriert am", "Zertifikat", "Unsicherheit", "Rückführbar");
        foreach (var reference in run.References)
            Row(output, reference.Model, reference.Serial ?? "", reference.Accuracy ?? "",
                reference.CalibratedOn?.ToString("dd.MM.yyyy", German) ?? "", reference.Certificate ?? "",
                DisplayNumber(reference.Uncertainty), reference.Traceable ? "Ja" : "Nein");
        output.AppendLine();
        Row(output, "Messpunkte");
        Row(output, "Kanal", "Messgröße", "Sollwert angefordert", "Sollwert bestätigt", "unabhängige Referenz", "Referenzwert", "Istwert", "unkorrigiert", "Abweichung", "Streuung", "Messwerte", "Toleranz", "Bewertung", "erweiterte Unsicherheit");
        foreach (var point in run.Points)
            Row(output, point.ChannelId, QuantityText(point.Quantity), Number(point.Requested), Number(point.Confirmed),
                Number(point.IndependentReference), Number(point.Reference),
                Number(point.Measured), Number(point.Uncorrected), Number(point.Error), Number(point.StandardDeviation),
                point.Samples.ToString(German), Number(point.Tolerance), point.Passed ? "Bestanden" : "Nicht bestanden",
                Number(point.ExpandedUncertainty));
        output.AppendLine();
        Row(output, "Koeffizienten und Justierung");
        Row(output, "Kanal", "Ursprünglicher Gain", "Ursprünglicher Offset", "Vorgeschlagener Gain", "Vorgeschlagener Offset", "max. Residuum", "TUR");
        foreach (var adjustment in run.Adjustments)
            Row(output, adjustment.ChannelId, Number(adjustment.Original.Gain), Number(adjustment.Original.Offset),
                Number(adjustment.Proposed.Gain), Number(adjustment.Proposed.Offset), Number(adjustment.MaxResidual), Number(adjustment.Tur));
        output.AppendLine();
        Row(output, "Freigabe- und Schreibprotokoll");
        Row(output, "Zeit", "Prüfer", "Ziel", "Koeffizienten", "Rücklesen verifiziert", "Fehler");
        foreach (var approval in approvals)
            Row(output, approval.Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz", German), approval.Operator,
                approval.Target, AdjustmentText(approval.Adjustments), approval.Verified ? "Ja" : "Nein", approval.Error ?? "");
        WriteText(path, output.ToString());
    }

    public void WritePdf(string path, RunResult run, IReadOnlyList<Approval>? approvals = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(run);
        approvals ??= [];
        var document = new Document { Info = { Title = $"{ReportTitle(run)} – {run.DisplayId}", Author = run.Operator } };
        var normal = document.Styles[StyleNames.Normal] ?? throw new InvalidOperationException("Der Normalstil fehlt.");
        normal.Font.Name = "Arial";
        normal.Font.Size = 8;
        normal.Font.Color = Colors.DarkSlateGray;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(1.5);
        var heading = document.Styles[StyleNames.Heading1] ?? throw new InvalidOperationException("Der Überschriftstil fehlt.");
        heading.Font.Name = "Arial"; heading.Font.Size = 16; heading.Font.Bold = true;
        heading.Font.Color = Color.FromRgb(22, 96, 120);
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(0);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
        var sectionHeading = document.Styles[StyleNames.Heading2] ?? throw new InvalidOperationException("Der Abschnittsstil fehlt.");
        sectionHeading.Font.Name = "Arial"; sectionHeading.Font.Size = 10; sectionHeading.Font.Bold = true;
        sectionHeading.Font.Color = Color.FromRgb(22, 96, 120);
        sectionHeading.ParagraphFormat.SpaceBefore = Unit.FromPoint(4);
        sectionHeading.ParagraphFormat.SpaceAfter = Unit.FromPoint(1.5);
        sectionHeading.ParagraphFormat.KeepWithNext = true;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.15);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.15);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.35);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.35);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8;
        footer.AddText($"DAQ Calibration Studio · {run.DisplayId} · Seite ");
        footer.AddPageField(); footer.AddText(" von "); footer.AddNumPagesField();

        section.AddParagraph(ReportTitle(run), StyleNames.Heading1);
        var subtitle = section.AddParagraph(run.DisplayId);
        subtitle.Format.Font.Size = 11; subtitle.Format.Font.Bold = true;
        AddInfoTable(section, run);
        AddHeading(section, "Verwendete Referenzgeräte");
        var refs = AddTable(section, ["Modell", "Seriennummer", "Genauigkeit", "Kalibriert am", "Zertifikat", "Unsicherheit", "Rückführbar"]);
        foreach (var reference in run.References)
            AddRow(refs, reference.Model, reference.Serial ?? "–", reference.Accuracy ?? "–",
                reference.CalibratedOn?.ToString("dd.MM.yyyy", German) ?? "–", reference.Certificate ?? "–",
                Number(reference.Uncertainty), reference.Traceable ? "Ja" : "Nein");
        AddHeading(section, "Prüfpunkte und Ergebnisse");
        var points = AddTable(section, ["Kanal", "Größe", "Soll anfg.", "Soll best.", "Ref. unabh.", "Referenz", "Istwert", "Abweichung", "Streuung", "n", "Toleranz", "Bewertung", "erw. Unsich."]);
        foreach (var point in run.Points)
            AddRow(points, point.ChannelId, QuantityText(point.Quantity), Number(point.Requested), DisplayNumber(point.Confirmed),
                DisplayNumber(point.IndependentReference), Number(point.Reference), Number(point.Measured), Number(point.Error),
                Number(point.StandardDeviation), point.Samples.ToString(German), Number(point.Tolerance),
                point.Passed ? "Bestanden" : "Nicht bestanden", DisplayNumber(point.ExpandedUncertainty));
        if (run.Adjustments.Count > 0)
        {
            AddHeading(section, "Kalibrierkoeffizienten");
            var coefficients = AddTable(section, ["Kanal", "Gain vorher", "Offset vorher", "Gain vorgeschlagen", "Offset vorgeschlagen", "Residuum", "TUR"]);
            foreach (var adjustment in run.Adjustments)
                AddRow(coefficients, adjustment.ChannelId, Number(adjustment.Original.Gain), Number(adjustment.Original.Offset),
                Number(adjustment.Proposed.Gain), Number(adjustment.Proposed.Offset), Number(adjustment.MaxResidual), DisplayNumber(adjustment.Tur));
            section.AddParagraph("Vorgeschlagene Koeffizienten sind nicht gleichbedeutend mit einer erfolgreich in der DAQ gespeicherten Justierung.");
        }
        AddHeading(section, "Freigabe und Übertragung");
        if (approvals.Count == 0) section.AddParagraph("Für diesen Prüflauf liegt kein Freigabe- oder Übertragungsprotokoll vor.");
        else
        {
            var approvalTable = AddTable(section, ["Zeit", "Prüfer", "Ziel", "Koeffizienten", "Rücklesen", "Fehler"]);
            foreach (var approval in approvals)
                AddRow(approvalTable, approval.Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", German), approval.Operator,
                    approval.Target, AdjustmentText(approval.Adjustments), approval.Verified ? "Verifiziert" : "Nicht verifiziert", approval.Error ?? "–");
        }
        AddHeading(section, "Hinweise zur Aussagekraft");
        section.AddParagraph(run.Simulated
            ? "SIMULATION: Die Messwerte stammen aus einer Simulation und belegen keine Prüfung eines realen Geräts."
            : "Messwerte und Ergebnisse beziehen sich auf die dokumentierten Prüfbedingungen und Referenzgeräte.");
        section.AddParagraph(TraceabilityText(run.References));
        section.AddParagraph("Messunsicherheiten wurden nur berücksichtigt, soweit sie im Datensatz angegeben sind. Nicht angegebene oder nicht verifizierte Unsicherheiten sind nicht bewertet.");
        if (!string.IsNullOrWhiteSpace(run.Error))
        {
            AddHeading(section, "Fehlerhinweis");
            section.AddParagraph(run.Error);
        }

        if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        renderer.PdfDocument.Save(fullPath);
    }

    private static void AddInfoTable(Section section, RunResult run)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.AddColumn(Unit.FromCentimeter(5.2));
        table.AddColumn(Unit.FromCentimeter(20.8));
        AddInfo("Prüfobjekt", run.Daq.Model);
        AddInfo("Seriennummer", run.Daq.Serial ?? "nicht angegeben");
        AddInfo("Hardware / Firmware", $"{run.Daq.Revision ?? "nicht angegeben"} / {run.Daq.Firmware ?? "nicht angegeben"}");
        AddInfo("Prüfprofil", $"{run.Profile.Name}, Version {run.Profile.Version} · {FitMethodText(run.Profile.Method)}");
        AddInfo("Ergebnis", OutcomeText(run.Outcome));
        AddInfo("Prüfer", string.IsNullOrWhiteSpace(run.Operator) ? "nicht angegeben" : run.Operator);
        AddInfo("Prüfbedingungen", string.IsNullOrWhiteSpace(run.Conditions) ? "nicht angegeben" : run.Conditions);
        AddInfo("Prüfzeitraum", $"{run.Started.ToLocalTime():dd.MM.yyyy HH:mm:ss} bis {run.Finished.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        void AddInfo(string label, string value)
        {
            var row = table.AddRow(); row.Cells[0].AddParagraph(label).Format.Font.Bold = true; row.Cells[1].AddParagraph(value);
        }
    }

    private static void AddHeading(Section section, string text) => section.AddParagraph(text, StyleNames.Heading2);

    private static Table AddTable(Section section, string[] headers)
    {
        var table = section.AddTable();
        table.Borders.Color = Color.FromRgb(202, 214, 218);
        table.Borders.Width = 0.5;
        table.Rows.LeftIndent = Unit.FromCentimeter(0);
        foreach (var _ in headers) table.AddColumn(Unit.FromCentimeter(26.5 / headers.Length));
        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Color.FromRgb(230, 239, 241);
        header.Format.Font.Bold = true;
        header.Format.Font.Size = 8;
        for (var i = 0; i < headers.Length; i++) header.Cells[i].AddParagraph(headers[i]);
        return table;
    }

    private static void AddRow(Table table, params string[] values)
    {
        if (values.Length != table.Columns.Count)
            throw new ArgumentException($"Zeile hat {values.Length} Werte, Tabelle erwartet {table.Columns.Count} Spalten.", nameof(values));
        var row = table.AddRow();
        row.Format.Font.Size = 8;
        for (var i = 0; i < values.Length; i++) row.Cells[i].AddParagraph(values[i]);
    }

    private static string ReportTitle(RunResult run) => run.Profile.Kind switch
    {
        RunKind.Functional => "Funktionsprüfbericht",
        RunKind.Calibration => "Kalibrierprotokoll",
        RunKind.Verification => "Verifikationsbericht",
        _ => "Prüfbericht"
    };

    private static string OutcomeText(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Passed => "Bestanden",
        RunOutcome.Failed => "Nicht bestanden",
        RunOutcome.Aborted => "Abgebrochen",
        RunOutcome.Error => "Fehler",
        _ => "Unbekannt"
    };

    private static string QuantityText(Quantity quantity) => quantity switch
    {
        Quantity.Voltage => "Spannung (V)",
        Quantity.Current => "Strom (mA)",
        Quantity.Resistance => "Widerstand (Ω)",
        Quantity.Frequency => "Frequenz (Hz)",
        Quantity.Digital => "Digital",
        _ => quantity.ToString()
    };

    private static string FitMethodText(FitMethod method) => method switch
    {
        FitMethod.SinglePoint => "Einpunkt",
        FitMethod.OffsetOnly => "Nur Offset",
        FitMethod.TwoPoint => "Zweipunkt",
        FitMethod.Regression => "Mehrpunktregression",
        _ => method.ToString()
    };

    private static string AdjustmentText(IReadOnlyList<Adjustment> adjustments) => string.Join(", ", adjustments.Select(a =>
        $"{a.ChannelId}: Gain {Number(a.Proposed.Gain)}, Offset {Number(a.Proposed.Offset)}"));

    private static string TraceabilityText(IReadOnlyList<ReferenceInfo> references) => references.Count == 0
        ? "Es sind keine Referenzgeräte dokumentiert; Rückführbarkeit wurde nicht nachgewiesen."
        : references.All(r => r.Traceable)
            ? "Alle dokumentierten Referenzgeräte sind als rückführbar gekennzeichnet. Diese Angabe allein ist keine Kalibrierbescheinigung."
            : "Mindestens ein Referenzgerät ist nicht als rückführbar gekennzeichnet. Eine metrologische Rückführbarkeit wird nicht behauptet.";

    private static string Number(double? value) => value is null ? "" : value.Value.ToString("0.########", German);
    private static string DisplayNumber(double? value) => value is null ? "–" : Number(value);

    private static void Row(StringBuilder output, params string[] values) =>
        output.AppendLine(string.Join(';', values.Select(CsvField)));

    private static string CsvField(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static void WriteText(string path, string text)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "\uFEFF" + text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
