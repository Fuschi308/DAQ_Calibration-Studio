using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DAQ.CalibrationStudio.App.Views;

public partial class ApprovalView : UserControl
{
    public ApprovalView() => InitializeComponent();

    internal void RequestClose()
    {
        if (CancelButton.Command is { } command && command.CanExecute(CancelButton.CommandParameter))
            command.Execute(CancelButton.CommandParameter);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
            Dispatcher.UIThread.Post(() => OperatorField.Focus(), DispatcherPriority.Loaded);
    }
}
