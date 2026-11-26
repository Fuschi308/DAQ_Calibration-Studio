using System.Text;
using DAQ.CalibrationStudio.Domain;
using DAQ.CalibrationStudio.Infrastructure.Persistence;
using DAQ.CalibrationStudio.Infrastructure.Reporting;
using Microsoft.Data.Sqlite;
using PdfSharp.Pdf.IO;
using Xunit;

namespace DAQ.CalibrationStudio.Infrastructure.Tests;

public sealed class PersistenceAndReportsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "daq-studio-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StorePersistsRunsApprovalsProfilesAndSettingsAcrossInstances()
    {
        var run = CreateRun();
        var store = new SqliteStudioStore(Path.Combine(_directory, "history.db"));
        await store.SaveRunAsync(run);
        var approval = new Approval(Guid.NewGuid(), run.Id, DateTimeOffset.UtcNow, "MF", "Flash",
            run.Adjustments, Verified: false, Error: "Rücklesung nicht bestätigt");
        await store.AddApprovalAsync(approval);
        await store.SaveSettingsAsync(new StudioSettings("MF", "23 °C", LightTheme: true));

        var reopened = new SqliteStudioStore(Path.Combine(_directory, "history.db"));
        var loadedRuns = await reopened.ListRunsAsync();
        Assert.Single(loadedRuns);
        Assert.Equal(run.Id, loadedRuns[0].Id);
        Assert.Equal(run.Points[0], loadedRuns[0].Points[0]);
        Assert.True(loadedRuns[0].Simulated);
        var loadedApproval = Assert.Single(await reopened.ListApprovalsAsync(run.Id));
        Assert.Equal(approval.Id, loadedApproval.Id);
        Assert.Equal(approval.Verified, loadedApproval.Verified);
        Assert.Equal("MF", (await reopened.LoadSettingsAsync()).Operator);
        Assert.Equal(run.Profile.Id, Assert.Single(await reopened.ListProfilesAsync()).Id);
    }

    [Fact]
    public async Task ProfilesAreVersionedAndRunsAreAppendOnly()
    {
        var store = new SqliteStudioStore(Path.Combine(_directory, "history.db"));
        var run = CreateRun();
        await store.SaveRunAsync(run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProfileAsync(run.Profile with { Name = "Manipuliert" }));
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveRunAsync(run with { Operator = "Geändert" }));
        var revised = run.Profile with { Version = run.Profile.Version + 1, Name = "Profil v2" };
        await store.SaveProfileAsync(revised);
        Assert.Equal(2, (await store.ListProfilesAsync()).Count);
        Assert.Equal(run.Id, Assert.Single(await store.ListRunsAsync()).Id);
    }

    [Fact]
    public async Task SearchFiltersHistoryByTextDateSerialAndOutcome()
    {
        var store = new SqliteStudioStore(Path.Combine(_directory, "history.db"));
        var run = CreateRun();
        await store.SaveRunAsync(run);
        Assert.Single(await store.SearchRunsAsync(text: "DAQ-123"));
        Assert.Single(await store.SearchRunsAsync(from: run.Started.AddMinutes(-1), to: run.Finished.AddMinutes(1), serial: "DAQ-123", outcome: RunOutcome.Passed));
        Assert.Empty(await store.SearchRunsAsync(serial: "anderes Gerät"));
    }

    [Fact]
    public void CsvAndPdfContainRealReportDataAndPdfPaginates()
    {
        var run = CreateRun(pointCount: 120);
        var exporter = new ReportExporter();
        var csvPath = Path.Combine(_directory, "report.csv");
        var pdfPath = Path.Combine(_directory, "report.pdf");
        var approvals = new[] { new Approval(Guid.NewGuid(), run.Id, run.Finished, "MF", "Flash", run.Adjustments, false, "Rücklesen offen") };
        exporter.WriteCsv(csvPath, run, approvals);
        exporter.WritePdf(pdfPath, run, approvals);

        var csv = File.ReadAllText(csvPath, Encoding.UTF8);
        Assert.Contains("Simulation – keine reale DAQ-Messung", csv);
        Assert.Contains("Funktionsprüfbericht", csv);
        Assert.Contains("AI-V120", csv);
        Assert.Contains("Rücklesen offen", csv);
        Assert.Contains("Gain 1,0001", csv);
        var bytes = File.ReadAllBytes(pdfPath);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        using var pdf = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 2, $"Der lange Bericht sollte umbrechen, hatte aber {pdf.PageCount} Seite(n).");
    }

    [Fact]
    public async Task MeasurementWriterKeepsHighRateRowsOutsideTheDatabase()
    {
        var path = Path.Combine(_directory, "measurements.csv");
        await using (var writer = await CsvMeasurementWriter.CreateAsync(path, simulated: true))
        {
            await writer.WriteFrameAsync(new DaqFrame(42, DateTimeOffset.UtcNow,
                [new ChannelSample("AI-V1", 17, 1.2, 1.2001, 1.19, 1.21, 1.2, 0.001, 16)]));
        }
        var content = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("Simulation – keine reale DAQ-Messung", content);
        Assert.Contains("\"AI-V1\"", content);
        Assert.Contains("\"42\"", content);
    }

    private static RunResult CreateRun(int pointCount = 2)
    {
        var start = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, pointCount).Select(index => new PointResult(
            $"AI-V{index + 1}", Quantity.Voltage, 5, 5, null, 5, 5.001, 5.001, 0.002, 16,
            0.001, 0.02, 0.01, 0.1, true, null)).ToArray();
        return new RunResult(Guid.NewGuid(), start, start.AddMinutes(1),
            new TestProfile(Guid.NewGuid(), 1, "Funktionsprüfung Spannung", RunKind.Functional, FitMethod.SinglePoint,
                [new TestStep(StepKind.AnalogPoint, "AI-V1", 5)]),
            new DaqIdentity("DAQ Calibration Studio", "DAQ-123", "1.2.3", "Rev B"),
            [new ReferenceInfo("Referenzquelle", "REF-77", "±0,02 %", new DateOnly(2026, 5, 1), "Z-123", 0.001, false)],
            "MF", "23 °C, 45 % rF", Simulated: true, RunOutcome.Passed, points,
            [new Adjustment("AI-V1", Coefficient.Identity, new Coefficient(1.0001, -0.0002, 2), 0.002, 4.5)]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
