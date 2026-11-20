using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DAQ.CalibrationStudio.App.Controls;

internal static class StatusTone
{
    public static string Normalize(string? value) => value?.ToLowerInvariant() switch
    {
        "ok" or "connected" or "passed" or "success" => "ok",
        "warn" or "warning" or "warnung" or "check" => "warn",
        "err" or "error" or "fehler" or "failed" or "fault" => "err",
        "sim" or "simulation" or "simulated" => "sim",
        "accent" or "busy" or "running" => "accent",
        _ => "neutral"
    };

    public static void Apply(StyledElement control, AvaloniaPropertyChangedEventArgs change)
    {
        control.Classes.Remove(Normalize(change.OldValue as string));
        control.Classes.Add(Normalize(change.NewValue as string));
    }
}

public sealed class StatusBadge : ContentControl
{
    public static readonly StyledProperty<string?> ToneProperty =
        AvaloniaProperty.Register<StatusBadge, string?>(nameof(Tone));

    static StatusBadge() => ToneProperty.Changed.AddClassHandler<StatusBadge>(StatusTone.Apply);

    public string? Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }
}

public sealed class ToneText : TextBlock
{
    public static readonly StyledProperty<string?> ToneProperty =
        AvaloniaProperty.Register<ToneText, string?>(nameof(Tone));

    static ToneText() => ToneProperty.Changed.AddClassHandler<ToneText>(StatusTone.Apply);

    public string? Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }
}

public sealed class StateDot : Control
{
    public static readonly StyledProperty<string?> ToneProperty =
        AvaloniaProperty.Register<StateDot, string?>(nameof(Tone));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<StateDot, IBrush?>(nameof(Foreground));

    static StateDot()
    {
        AffectsRender<StateDot>(ToneProperty, ForegroundProperty);
        ToneProperty.Changed.AddClassHandler<StateDot>(StatusTone.Apply);
    }

    public string? Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var tone = StatusTone.Normalize(Tone);
        var brush = Foreground ?? Brushes.SlateGray;
        if (tone == "err")
        {
            var triangle = new StreamGeometry();
            using (var path = triangle.Open())
            {
                path.BeginFigure(new Point(Bounds.Width / 2, 0), true);
                path.LineTo(new Point(Bounds.Width, Bounds.Height));
                path.LineTo(new Point(0, Bounds.Height));
                path.EndFigure(true);
            }
            context.DrawGeometry(brush, null, triangle);
            return;
        }

        var filled = tone is not ("neutral" or "accent");
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(0, Math.Min(center.X, center.Y) - .65);
        context.DrawEllipse(filled ? brush : null, new Pen(brush, 1.3), center, radius, radius);
    }
}
