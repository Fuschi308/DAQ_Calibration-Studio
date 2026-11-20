using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed record Fact(string Label, string Value, string Tone = "");
public sealed record RunStepRow(string Name, string Detail, string Status, string Tone);
public sealed record AdjustmentRow(string Channel, string GainOld, string GainNew, string OffsetOld, string OffsetNew,
    string AsFound, string AsLeft);

public static class Presentation
{
    public static CultureInfo German { get; } = CultureInfo.GetCultureInfo("de-DE");

    public static string Number(double? number, string format = "0.0000") =>
        number?.ToString(format, German).Replace('-', '−') ?? "—";

    public static string Value(double? number, ChannelDefinition channel, string format = "0.0000") =>
        Value(number, channel.Quantity, format);

    public static string Value(double? number, Quantity quantity, string format = "0.0000")
    {
        if (number is null) return "—";
        if (quantity == Quantity.Digital) return number == 0 ? "LOW" : "HIGH";
        if (quantity == Quantity.Frequency && format == "0.0000") format = "#,##0.00";
        return $"{Number(number, format)} {ChannelCatalog.Unit(quantity)}";
    }

    public static string RunKindLabel(RunKind kind) => kind switch
    {
        RunKind.Calibration => "Kalibrierprotokoll",
        RunKind.Verification => "Verifikationsbericht",
        _ => "Funktionsprüfbericht"
    };

    public static (string Label, string Tone) Outcome(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Passed => ("Bestanden", "ok"),
        RunOutcome.Failed => ("Nicht bestanden", "warn"),
        RunOutcome.Aborted => ("Abgebrochen", "warn"),
        _ => ("Fehler", "err")
    };

    public static (string Label, string Tone) Connection(DeviceStatus device, bool sim) => device.State switch
    {
        DeviceState.Connected => ("Verbunden", sim ? "sim" : "ok"),
        DeviceState.Connecting => ("Verbindet …", "accent"),
        DeviceState.Faulted => ("Fehler", "err"),
        _ => ("Getrennt", "")
    };

    public static string EngineLabel(EngineState state) => state switch
    {
        EngineState.Preparing => "Vorbereiten",
        EngineState.ConfiguringInstrument => "Konfigurieren",
        EngineState.Settling => "Stabilisieren",
        EngineState.Acquiring => "Erfassen",
        EngineState.Evaluating => "Auswerten",
        EngineState.Paused => "Pausiert",
        EngineState.Aborting => "Bricht ab",
        EngineState.Completed => "Fertig",
        EngineState.Aborted => "Abgebrochen",
        EngineState.Failed => "Fehler",
        _ => "Bereit"
    };

    public static string MethodLabel(FitMethod method) => method switch
    {
        FitMethod.SinglePoint => "Einpunktprüfung",
        FitMethod.OffsetOnly => "Nur Offset",
        FitMethod.TwoPoint => "Zweipunkt",
        _ => "Mehrpunkt (lineare Regression)"
    };

    public static string StepLabel(TestStep step)
    {
        if (!string.IsNullOrWhiteSpace(step.Label)) return step.Label;
        if (step.Kind == StepKind.Wait) return "Warten";
        return $"{step.ChannelId} · {Value(step.Value, ChannelCatalog.Get(step.ChannelId), "0.###")}";
    }

    public static AdjustmentRow[] AdjustmentRows(RunResult run) => run.Adjustments.Select(adjustment =>
    {
        var unit = ChannelCatalog.Get(adjustment.ChannelId);
        var error = run.Points.Where(point => point.ChannelId == adjustment.ChannelId).Select(point => Math.Abs(point.Error)).DefaultIfEmpty().Max();
        return new AdjustmentRow(adjustment.ChannelId, Number(adjustment.Original.Gain, "0.000000"),
            Number(adjustment.Proposed.Gain, "0.000000"), Value(adjustment.Original.Offset, unit),
            Value(adjustment.Proposed.Offset, unit), Value(error, unit), Value(adjustment.MaxResidual, unit));
    }).ToArray();

    public static ChartSeries[] HistoryChart(IEnumerable<MeasurementTick> frames, ChannelDefinition channel, int color, string mode)
    {
        var window = frames.SelectMany(tick => tick.Frame.Samples.Where(sample => sample.ChannelId == channel.Id)
            .Select(sample => (tick.Frame.Time, Sample: sample))).ToArray();
        if (window.Length == 0)
            return [];
        var end = window[^1].Time;
        var data = window.Where((_, index) => index % Math.Max(1, (int)Math.Ceiling(window.Length / 600d)) == 0 || index == window.Length - 1)
            .ToArray();
        var x = data.Select(sample => (sample.Time - end).TotalSeconds).ToArray();
        var values = data.Select(sample => (mode switch
        {
            "Abweichung" => sample.Sample.Corrected - sample.Sample.Setpoint,
            "Stabilität" => sample.Sample.StdDev,
            _ => sample.Sample.Corrected
        }) ?? double.NaN).ToArray();
        var series = new ChartSeries(channel.Id, channel.Unit, x, values, color)
        {
            Minimum = mode == "Soll / Ist" ? data.Select(item => item.Sample.Min ?? double.NaN).ToArray() : null,
            Maximum = mode == "Soll / Ist" ? data.Select(item => item.Sample.Max ?? double.NaN).ToArray() : null,
            IsStep = channel.Quantity == Quantity.Digital
        };
        if (mode != "Soll / Ist" || !data.Any(point => point.Sample.Setpoint.HasValue))
            return [series];
        return [series, new ChartSeries($"{channel.Id} Soll", channel.Unit, x,
            data.Select(point => point.Sample.Setpoint ?? double.NaN).ToArray(), color, true)];
    }
}

public partial class ChannelRow(ChannelDefinition channel, int color) : ObservableObject
{
    public ChannelDefinition Definition { get; } = channel;
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Type => ChannelCatalog.Name(Definition.Quantity);
    public string Range => Definition.Range;
    public string Group => ChannelCatalog.GroupName(Definition.Group);
    public int ColorIndex { get; } = color;

    [ObservableProperty] private bool isChecked;
    [ObservableProperty] private string requested = "—";
    [ObservableProperty] private string measured = "—";
    [ObservableProperty] private string error = "—";
    [ObservableProperty] private string relative = "—";
    [ObservableProperty] private string tolerance = "—";
    [ObservableProperty] private string status = "n. a.";
    [ObservableProperty] private string stateTone = "";
    [ObservableProperty] private string calibration = "—";
    [ObservableProperty] private string stability = "—";
    public Coefficient? Coefficient { get; private set; }

    public void Update(ChannelSample? sample, double? average, double? stdDev, double limit, string relativeMode)
    {
        var measuredValue = average ?? sample?.Corrected;
        var reference = sample?.Setpoint;
        Requested = Presentation.Value(reference, Definition, "0.000");
        Measured = Presentation.Value(measuredValue, Definition);
        Stability = Presentation.Value(stdDev, Definition, "0.000000");
        var errorValue = measuredValue - reference;
        Error = Definition.Quantity == Quantity.Digital ? "—" : Presentation.Value(errorValue, Definition, "+0.0000;-0.0000;0.0000");
        var frequency = Definition.Quantity == Quantity.Frequency;
        var realLimit = frequency ? CalibrationMath.AbsoluteTolerance(Definition, reference ?? 0, limit) : limit;
        Tolerance = Definition.Quantity == Quantity.Digital ? "Pegel" : frequency ? $"±{Presentation.Number(limit, "0")} ppm"
            : $"±{Presentation.Value(limit, Definition)}";
        double? relativeValue = null;
        if (errorValue is double delta && reference is double target)
        {
            relativeValue = frequency ? CalibrationMath.PercentOfReading(delta, target) * 10_000
                : relativeMode == "% v. Messwert" ? CalibrationMath.PercentOfReading(delta, target)
                : CalibrationMath.PercentOfSpan(delta, Definition.Span);
        }

        Relative = relativeValue is null || relativeMode == "aus" || Definition.Quantity == Quantity.Digital ? "—"
            : $"{Presentation.Number(relativeValue, "+0.000;-0.000;0.000")} {(frequency ? "ppm" : "%")}";
        var next = errorValue is not double measurementError || Definition.Unwired ? ("n. a.", "")
            : CalibrationMath.IsWithinTolerance(measurementError, realLimit) ? ("OK", "ok")
            : realLimit > 0 && Math.Abs(measurementError) <= realLimit * 3 ? ("Prüfen", "warn") : ("Fehler", "err");
        Status = next.Item1;
        StateTone = next.Item2;
    }

    public void SetCoefficient(Coefficient? value)
    {
        Coefficient = value;
        Calibration = value switch
        {
            { CalibratedOn: { } date } when date.AddYears(1) < DateOnly.FromDateTime(DateTime.Today) => "Abgelaufen",
            { CalibratedOn: { } date } => $"Gültig bis {date.AddYears(1):dd.MM.yyyy}",
            { Gain: 1, Offset: 0 } => "Unkalibriert",
            { Version: > 0 } => "RAM (temporär)",
            { Version: 0 } => "Unkalibriert",
            _ => Definition.Quantity == Quantity.Frequency && !Definition.Unwired ? "Quarzreferenz" : "—"
        };
    }
}
