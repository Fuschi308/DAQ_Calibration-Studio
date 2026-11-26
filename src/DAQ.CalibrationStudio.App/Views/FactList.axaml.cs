using Avalonia.Controls;

namespace DAQ.CalibrationStudio.App.Views;

public partial class FactList : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public FactList() => InitializeComponent();
}
