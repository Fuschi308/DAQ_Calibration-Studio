using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DAQ.CalibrationStudio.App.Controls;

public sealed class StudioIcon : Avalonia.Controls.Shapes.Path
{
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<StudioIcon, string?>(nameof(Glyph));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<StudioIcon, IBrush?>(nameof(Foreground));

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public StudioIcon()
    {
        Stretch = Stretch.Uniform;
        StrokeThickness = 1.2;
        StrokeLineCap = PenLineCap.Round;
        StrokeJoin = PenLineJoin.Round;
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateGlyph();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphProperty)
            UpdateGlyph();
        if (change.Property == ForegroundProperty)
            Stroke = Foreground;
    }

    private void UpdateGlyph()
    {
        var icon = Glyph switch { "daq" => "chip", "generator" => "gen", _ => Glyph };
        Data = icon is not null &&
            this.TryFindResource($"Icon.{icon}", ActualThemeVariant, out var resource)
            ? resource as Geometry
            : null;
    }
}
