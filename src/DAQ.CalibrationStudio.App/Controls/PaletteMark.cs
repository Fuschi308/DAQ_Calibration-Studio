using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DAQ.CalibrationStudio.App.Controls;

public sealed class PaletteMark : Border
{
    public static readonly StyledProperty<int> IndexProperty =
        AvaloniaProperty.Register<PaletteMark, int>(nameof(Index));

    public int Index
    {
        get => GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    public PaletteMark() => ActualThemeVariantChanged += (_, _) => UpdateBrush();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateBrush();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IndexProperty)
            UpdateBrush();
    }

    private void UpdateBrush()
    {
        if (this.TryFindResource($"Series{Math.Max(0, Index) % 8 + 1}Brush", ActualThemeVariant, out var brush))
            Background = brush as IBrush;
    }
}
