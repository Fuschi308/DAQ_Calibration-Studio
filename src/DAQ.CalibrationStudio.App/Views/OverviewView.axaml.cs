using Avalonia.Controls;
using Avalonia.Threading;

namespace DAQ.CalibrationStudio.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.UIThread.Post(OverviewScroll.ScrollToHome, DispatcherPriority.Loaded);
    }
}
