using Avalonia.Controls;
using Avalonia.Input;

namespace DAQ.CalibrationStudio.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Frame.SizeChanged += (_, args) =>
        {
            Frame.Classes.Set("compact", args.NewSize.Width < 1640);
            Frame.Classes.Set("short", args.NewSize.Height < 850);
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && ApprovalOverlay.IsVisible)
        {
            ApprovalOverlay.RequestClose();
            e.Handled = true;
        }
    }
}
