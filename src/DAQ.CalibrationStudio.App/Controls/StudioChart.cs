using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using ScottPlot.TickGenerators;

namespace DAQ.CalibrationStudio.App.Controls;

public sealed record ChartSeries(string Name, string Unit, double[] X, double[] Y, int PaletteIndex, bool IsDashed = false)
{
    public double[]? Minimum { get; init; }
    public double[]? Maximum { get; init; }
    public bool IsStep { get; init; }
}

public sealed class StudioChart : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<StudioChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<bool> IsScopeProperty =
        AvaloniaProperty.Register<StudioChart, bool>(nameof(IsScope));

    public static readonly StyledProperty<bool> ShowCursorProperty =
        AvaloniaProperty.Register<StudioChart, bool>(nameof(ShowCursor));

    public static readonly StyledProperty<bool> ShowLegendProperty =
        AvaloniaProperty.Register<StudioChart, bool>(nameof(ShowLegend), true);

    public static readonly StyledProperty<double> WindowSecondsProperty =
        AvaloniaProperty.Register<StudioChart, double>(nameof(WindowSeconds));

    public static readonly StyledProperty<string?> XAxisUnitProperty =
        AvaloniaProperty.Register<StudioChart, string?>(nameof(XAxisUnit));

    public static readonly DirectProperty<StudioChart, string> CursorReadoutProperty =
        AvaloniaProperty.RegisterDirect<StudioChart, string>(nameof(CursorReadout), control => control.CursorReadout);

    private static readonly CultureInfo NumberCulture = CultureInfo.GetCultureInfo("de-DE");
    private readonly AvaPlot plotControl = new() { HandleMouseWheelEvent = true };
    private readonly TextBlock empty = new()
    {
        Text = "Keine Messdaten",
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        IsHitTestVisible = false
    };
    private Crosshair? cursor;
    private Coordinates? cursorPosition;
    private VerticalLine? scopeCursorA;
    private VerticalLine? scopeCursorB;
    private double? scopePositionA;
    private double? scopePositionB;
    private int draggingCursor;
    private string cursorReadout = "";
    private bool manualLimits;
    private string? lastSignalSet;

    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public bool IsScope
    {
        get => GetValue(IsScopeProperty);
        set => SetValue(IsScopeProperty, value);
    }

    public bool ShowCursor
    {
        get => GetValue(ShowCursorProperty);
        set => SetValue(ShowCursorProperty, value);
    }

    public bool ShowLegend
    {
        get => GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
    }

    public double WindowSeconds
    {
        get => GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    public string? XAxisUnit
    {
        get => GetValue(XAxisUnitProperty);
        set => SetValue(XAxisUnitProperty, value);
    }

    public string CursorReadout => cursorReadout;

    public StudioChart()
    {
        Content = new Grid { Children = { plotControl, empty } };
        ActualThemeVariantChanged += (_, _) => RenderSnapshot();
        plotControl.AddHandler(PointerPressedEvent, OnPointerPressedInPlot, RoutingStrategies.Tunnel);
        plotControl.AddHandler(PointerWheelChangedEvent, (_, _) => manualLimits = true, RoutingStrategies.Tunnel);
        plotControl.AddHandler(PointerReleasedEvent, OnPointerReleasedInPlot, RoutingStrategies.Tunnel);
        plotControl.PointerMoved += OnPointerMovedInPlot;
        plotControl.PointerCaptureLost += (_, _) => draggingCursor = 0;
        ToolTip.SetTip(this, "Mausrad: Zoom · Ziehen: Achsen verschieben · Doppelklick: Ansicht zurücksetzen");
        AutomationProperties.SetName(this, "Messdiagramm");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RenderSnapshot();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowSecondsProperty || change.Property == IsScopeProperty)
            manualLimits = false;
        if (change.Property == SeriesProperty || change.Property == IsScopeProperty ||
            change.Property == ShowCursorProperty || change.Property == ShowLegendProperty ||
            change.Property == WindowSecondsProperty || change.Property == XAxisUnitProperty)
            RenderSnapshot();
    }

    public void ResetViewport()
    {
        manualLimits = false;
        scopePositionA = scopePositionB = null;
        RenderSnapshot();
    }

    private void OnPointerPressedInPlot(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetViewport();
            e.Handled = true;
            return;
        }

        var point = e.GetCurrentPoint(plotControl);
        if (point.Properties.IsLeftButtonPressed && scopeCursorA is not null && scopeCursorB is not null)
        {
            double distanceA = Math.Abs(plotControl.Plot.GetPixel(new(scopeCursorA.X, 0)).X - point.Position.X);
            double distanceB = Math.Abs(plotControl.Plot.GetPixel(new(scopeCursorB.X, 0)).X - point.Position.X);
            if (Math.Min(distanceA, distanceB) <= 10)
            {
                draggingCursor = distanceA <= distanceB ? 1 : 2;
                e.Pointer.Capture(plotControl);
                e.Handled = true;
                return;
            }
        }
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            manualLimits = true;
    }

    private void OnPointerReleasedInPlot(object? sender, PointerReleasedEventArgs e)
    {
        if (draggingCursor == 0)
            return;
        draggingCursor = 0;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPointerMovedInPlot(object? sender, PointerEventArgs e)
    {
        if (cursor is null && draggingCursor == 0)
            return;

        var point = e.GetPosition(plotControl);
        var position = plotControl.Plot.GetCoordinates((float)point.X, (float)point.Y);
        if (draggingCursor == 1 && scopeCursorA is not null)
            scopeCursorA.X = (scopePositionA = position.X).Value;
        if (draggingCursor == 2 && scopeCursorB is not null)
            scopeCursorB.X = (scopePositionB = position.X).Value;
        if (draggingCursor != 0)
            UpdateScopeReadout();
        if (cursor is not null)
        {
            cursor.Position = position;
            cursorPosition = position;
        }
        plotControl.Refresh();
    }

    private void RenderSnapshot()
    {
        if (!this.IsAttachedToVisualTree())
            return;

        var plot = plotControl.Plot;
        var series = Series?.Where(item => item.X.Length == item.Y.Length && item.X.Length > 0).ToArray() ?? [];
        string signalSet = string.Join('|', series.Select(item => $"{item.Name}:{item.Unit}"));
        if (signalSet != lastSignalSet)
        {
            manualLimits = false;
            cursorPosition = null;
            scopePositionA = scopePositionB = null;
            lastSignalSet = signalSet;
        }

        plot.Clear();
        ApplyTheme(plot);
        empty.IsVisible = series.Length == 0;

        foreach (var item in series)
        {
            var color = GetSeriesColor(item);
            if (item.Minimum is { } minimum && item.Maximum is { } maximum &&
                minimum.Length == item.X.Length && maximum.Length == item.X.Length)
            {
                var band = plot.Add.FillY(item.X, minimum, maximum);
                band.FillColor = color.WithAlpha(.16);
                band.LineWidth = 0;
            }

            var line = plot.Add.Scatter(item.X, item.Y, color);
            line.LegendText = item.Name;
            line.MarkerSize = item.X.Length < 2 ? 5 : 0;
            line.LineWidth = item.IsDashed ? 1 : 1.4f;
            if (item.IsDashed)
                line.LinePattern = LinePattern.Dashed;
            if (item.IsStep)
                line.ConnectStyle = ConnectStyle.StepHorizontal;
        }

        var unit = series.FirstOrDefault()?.Unit;
        plot.Axes.Left.Label.Text = unit ?? "";
        if (!manualLimits && series.Length != 0)
        {
            plot.Axes.AutoScale();
            if (WindowSeconds > 0)
            {
                var end = series.Max(item => item.X[^1]);
                plot.Axes.SetLimitsX(end - WindowSeconds, end);
            }
        }

        cursor = null;
        scopeCursorA = scopeCursorB = null;
        if (ShowCursor && series.Length != 0)
        {
            if (IsScope)
            {
                var axes = plot.Axes.GetLimits();
                scopePositionA ??= axes.Left + (axes.Right - axes.Left) * .26;
                scopePositionB ??= axes.Left + (axes.Right - axes.Left) * .76;
                var color = GetPlotColor("ScopeCursorBrush");
                scopeCursorA = plot.Add.VerticalLine(scopePositionA.Value, 1, color, LinePattern.Dashed);
                scopeCursorB = plot.Add.VerticalLine(scopePositionB.Value, 1, color, LinePattern.Dashed);
                scopeCursorA.Text = "A";
                scopeCursorB.Text = "B";
                UpdateScopeReadout();
            }
            else
            {
                var position = cursorPosition ?? new Coordinates(series[0].X[^1], series[0].Y[^1]);
                var crosshair = plot.Add.Crosshair(position.X, position.Y);
                crosshair.LineColor = GetPlotColor("AccentBrush");
                crosshair.LinePattern = LinePattern.Dashed;
                cursor = crosshair;
            }
        }
        else
            SetAndRaise(CursorReadoutProperty, ref cursorReadout, "");

        plot.Legend.IsVisible = ShowLegend && series.Length > 0;
        plotControl.Refresh();
    }

    private void UpdateScopeReadout()
    {
        if (scopePositionA is not double a || scopePositionB is not double b)
            return;
        double delta = Math.Abs(b - a);
        if (XAxisUnit == "s")
        {
            SetAndRaise(CursorReadoutProperty, ref cursorReadout,
                $"A {FormatTime(a)} · B {FormatTime(b)} · Δt {FormatTime(delta)}" +
                (delta > 0 ? $" · 1/Δt {FormatFrequency(1 / delta)}" : ""));
            return;
        }
        var unit = XAxisUnit is { Length: > 0 } text ? $" {text}" : "";
        SetAndRaise(CursorReadoutProperty, ref cursorReadout,
            $"A {FormatTick(a)}{unit} · B {FormatTick(b)}{unit} · Δ {FormatTick(delta)}{unit}");
    }

    private void ApplyTheme(Plot plot)
    {
        var foreground = GetPlotColor(IsScope ? "ScopeTextBrush" : "Text2Brush");
        var grid = GetPlotColor(IsScope ? "ScopeGridBrush" : "ChartGridBrush");
        var frame = GetPlotColor(IsScope ? "ScopeGridBrush" : "ChartAxisBrush");
        plot.FigureBackground.Color = GetPlotColor(IsScope ? "ScopeScreenBrush" : "PanelBrush");
        plot.DataBackground.Color = GetPlotColor(IsScope ? "ScopeScreenBrush" : "SubtleBrush");
        plot.Axes.Color(foreground);
        plot.Axes.FrameColor(frame);
        plot.Axes.Bottom.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        plot.Axes.Left.TickLabelStyle.FontName = "Segoe UI";
        plot.Axes.Left.TickLabelStyle.FontSize = 11;
        plot.Axes.Left.Label.FontName = "Segoe UI";
        plot.Axes.Left.Label.FontSize = 11;
        if (plot.Axes.Bottom.TickGenerator is NumericAutomatic horizontalTicks)
        {
            var unit = XAxisUnit ?? (WindowSeconds > 0 ? "s" : null);
            horizontalTicks.LabelFormatter = IsScope && unit == "s"
                ? FormatTime
                : x => FormatTick(x) + (unit is null ? "" : $" {unit}");
        }
        if (plot.Axes.Left.TickGenerator is NumericAutomatic verticalTicks)
            verticalTicks.LabelFormatter = FormatTick;
        plot.Grid.LineColor = grid;
        plot.Grid.LineWidth = 1;
        plot.Legend.FontName = "Segoe UI";
        plot.Legend.FontSize = 11;
        plot.Legend.FontColor = foreground;
        plot.Legend.BackgroundColor = plot.FigureBackground.Color;
        plot.Legend.OutlineColor = frame;
        empty.Foreground = new SolidColorBrush(Avalonia.Media.Color.FromArgb(foreground.A, foreground.R, foreground.G, foreground.B));
    }

    private ScottPlot.Color GetSeriesColor(ChartSeries item)
    {
        if (item.IsDashed)
            return GetPlotColor("Text2Brush");
        if (IsScope)
            return GetPlotColor(item.PaletteIndex == 0 ? "ScopeCh1Brush" : "ScopeCh2Brush");

        return GetPlotColor($"Series{Math.Max(0, item.PaletteIndex) % 8 + 1}Brush");
    }

    private ScottPlot.Color GetPlotColor(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is ISolidColorBrush brush)
            return new ScottPlot.Color(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);

        return ScottPlot.Colors.Gray;
    }

    private static string FormatTick(double value) => value.ToString("0.########", NumberCulture).Replace('-', '−');

    private static string FormatTime(double seconds)
    {
        var magnitude = Math.Abs(seconds);
        return magnitude switch
        {
            0 => "0 s",
            >= 1 => $"{FormatTick(seconds)} s",
            >= .001 => $"{FormatTick(seconds * 1000)} ms",
            _ => $"{FormatTick(seconds * 1_000_000)} µs"
        };
    }

    private static string FormatFrequency(double hertz) => hertz >= 1000
        ? $"{FormatTick(Math.Round(hertz / 1000, 4))} kHz"
        : $"{FormatTick(Math.Round(hertz, 2))} Hz";
}
