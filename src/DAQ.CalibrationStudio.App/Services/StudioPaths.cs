using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DAQ.CalibrationStudio.App.Services;

public sealed class StudioPaths
{
    public StudioPaths(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        Root = Path.GetFullPath(index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DAQ Calibration Studio"));
        StartInHardware = args.Contains("--hardware", StringComparer.OrdinalIgnoreCase);
    }

    public string Root { get; }
    public bool StartInHardware { get; }
    public string Database => Path.Combine(Root, "studio.sqlite3");
    public string SimulationFlash => Path.Combine(Root, "simulation-flash.json");
    public string Recordings => Path.Combine(Root, "recordings");
    public string LogFolder => Path.Combine(Root, "logs");
}

public sealed class DesktopFiles
{
    public Window? Owner { get; set; }

    public async Task<string?> SaveAsync(string title, string suggestedName, string extension)
    {
        if (Owner is null || !Owner.StorageProvider.CanSave)
            throw new InvalidOperationException("Der Dateidialog steht nicht zur Verfügung.");
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }]
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string action, string? acknowledgement = null)
    {
        if (Owner is null)
            return false;
        var prompt = new Window
        {
            Title = title, Width = 505, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var primary = new Button { Content = action, MinWidth = 105 };
        primary.Classes.Add(acknowledgement is null ? "primary" : "danger");
        var checkbox = new CheckBox
        {
            Content = acknowledgement,
            IsVisible = acknowledgement is not null
        };
        if (acknowledgement is not null)
        {
            primary.IsEnabled = false;
            checkbox.IsCheckedChanged += (_, _) => primary.IsEnabled = checkbox.IsChecked == true;
        }
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100 };
        primary.Click += (_, _) => prompt.Close(true);
        cancel.Click += (_, _) => prompt.Close(false);
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22), Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                checkbox,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8,
                    Children = { cancel, primary }
                }
            }
        };
        return await prompt.ShowDialog<bool>(Owner);
    }
}
