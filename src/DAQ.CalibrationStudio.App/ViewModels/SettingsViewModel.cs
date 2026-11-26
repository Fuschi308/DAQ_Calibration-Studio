using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class LimitSetting(ChannelDefinition channel, double initial) : ObservableObject
{
    public string Id => channel.Id;
    public string Label => $"{channel.Id} · {channel.Name}";
    public string Unit => channel.Quantity == Quantity.Frequency ? "ppm" : channel.Unit;
    [ObservableProperty] private double value = initial;
}

public sealed partial class ReferenceSetting(ReferenceInfo reference) : ObservableObject
{
    public string Model { get; } = reference.Model;
    [ObservableProperty] private string serial = reference.Serial ?? "";
    [ObservableProperty] private string accuracy = reference.Accuracy ?? "";
    [ObservableProperty] private string calibrationDate = reference.CalibratedOn?.ToString("dd.MM.yyyy") ?? "";
    [ObservableProperty] private string certificate = reference.Certificate ?? "";
    [ObservableProperty] private double uncertainty = reference.Uncertainty ?? 0;
    [ObservableProperty] private bool traceable = reference.Traceable;

    public ReferenceInfo Read()
    {
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(CalibrationDate))
        {
            if (!DateOnly.TryParseExact(CalibrationDate, "dd.MM.yyyy", Presentation.German,
                System.Globalization.DateTimeStyles.None, out var parsed))
                throw new ArgumentException($"{Model}: Verwenden Sie für das Kalibrierdatum TT.MM.JJJJ.");
            date = parsed;
        }
        if (!double.IsFinite(Uncertainty) || Uncertainty < 0)
            throw new ArgumentException($"{Model}: Messunsicherheit ungültig.");
        if (Traceable && (string.IsNullOrWhiteSpace(Serial) || string.IsNullOrWhiteSpace(Certificate) || date is null || Uncertainty <= 0))
            throw new ArgumentException($"{Model}: Für die Rückführungsangabe bitte Seriennummer, Kalibrierdatum, Zertifikat und Messunsicherheit eintragen.");
        return new ReferenceInfo(Model, Serial.Trim(), Accuracy.Trim(), date, Certificate.Trim(),
            Uncertainty > 0 ? Uncertainty : null, Traceable);
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly StudioSession session;
    private bool loaded;

    [ObservableProperty] private string section = "Allgemein";
    [ObservableProperty] private string @operator = "";
    [ObservableProperty] private string conditions = "";
    [ObservableProperty] private bool lightTheme;
    [ObservableProperty] private bool isSimulated = true;
    [ObservableProperty] private int speed = 1;
    [ObservableProperty] private double noise = 1;
    [ObservableProperty] private string faultDevice = "STM32H7-DAQ";
    [ObservableProperty] private IReadOnlyList<LimitSetting> limits = [];
    [ObservableProperty] private IReadOnlyList<ReferenceSetting> references = [];

    public SettingsViewModel(StudioSession session)
    {
        this.session = session;
        session.SettingsChanged += () =>
        {
            if (!loaded) Reload();
            LightTheme = session.Settings.LightTheme;
        };
        session.ModeChanged += Tick;
        Reload();
        loaded = false;
    }

    public IReadOnlyList<string> Sections { get; } =
        ["Allgemein", "Kalibrierung", "Grenzwerte", "Referenzgeräte", "Simulation", "Datenspeicherung", "Architektur"];
    public int[] SpeedOptions { get; } = [1, 2, 5, 10];
    public string[] DeviceNames { get; } = ["STM32H7-DAQ", "FNIRSI SG-004A", "OWON DGE2035", "PeakTech P 1400"];
    public string DataDirectory => session.Paths.Root;
    public bool ShowGeneral => Section == "Allgemein";
    public bool ShowCalibration => Section == "Kalibrierung";
    public bool ShowLimits => Section == "Grenzwerte";
    public bool ShowReferences => Section == "Referenzgeräte";
    public bool ShowSimulation => Section == "Simulation";
    public bool ShowStorage => Section == "Datenspeicherung";
    public bool ShowArchitecture => Section == "Architektur";

    public void Tick()
    {
        IsSimulated = session.Devices.Active.Simulated;
        if (Speed != session.Simulator.Speed)
            Speed = session.Simulator.Speed;
    }

    [RelayCommand]
    private Task SaveAsync() => session.PerformAsync(async () =>
    {
        var saved = Limits.ToDictionary(limit => limit.Id, limit =>
            double.IsFinite(limit.Value) && limit.Value >= 0 ? limit.Value : throw new ArgumentException($"{limit.Id}: Ungültige Toleranz."));
        await session.SaveSettingsAsync(new StudioSettings(Operator.Trim(), Conditions.Trim(), LightTheme, References.Select(reference => reference.Read()).ToArray(),
            saved));
    }, "Einstellungen gespeichert.");

    [RelayCommand]
    private Task SwitchModeAsync() => session.PerformAsync(session.SwitchModeAsync);

    [RelayCommand]
    private Task ApplyNoiseAsync() => session.PerformAsync(() =>
    {
        if (!session.Devices.Active.Simulated)
            throw new InvalidOperationException("Rauschfaktor ist nur in der Simulation verfügbar.");
        session.Simulator.Daq.NoiseFactor = Noise;
        return Task.CompletedTask;
    }, "Rauschfaktor übernommen.");

    [RelayCommand]
    private void InjectFault()
    {
        if (!session.Devices.Active.Simulated)
            return;
        var index = Array.IndexOf(DeviceNames, FaultDevice);
        if (index < 0) return;
        var kind = (DeviceKind)index;
        if (!session.Devices.Active.IsDeviceSimulated(kind))
        {
            session.Say("Für dieses Gerät ist der Hardwaretreiber ausgewählt.");
            return;
        }
        session.Simulator.InjectFault(kind);
        session.LogDeviceFault(FaultDevice, "Kommunikationsfehler ausgelöst (Simulation)");
        session.Say($"{FaultDevice}: simulierter Verbindungsverlust.");
    }

    [RelayCommand]
    private Task OpenDataAsync() => session.PerformAsync(() =>
    {
        Directory.CreateDirectory(DataDirectory);
        using var opened = Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
        return Task.CompletedTask;
    });

    partial void OnSpeedChanged(int value)
    {
        if (SpeedOptions.Contains(value)) session.Simulator.Speed = value;
    }

    partial void OnSectionChanged(string value)
    {
        foreach (var name in new[] { nameof(ShowGeneral), nameof(ShowCalibration), nameof(ShowLimits),
            nameof(ShowReferences), nameof(ShowSimulation), nameof(ShowStorage), nameof(ShowArchitecture) })
            OnPropertyChanged(name);
    }

    private void Reload()
    {
        Operator = session.Settings.Operator;
        Conditions = session.Settings.Conditions;
        LightTheme = session.Settings.LightTheme;
        Limits = ChannelCatalog.All.Where(channel => !channel.Unwired && channel.Quantity != Quantity.Digital)
            .Select(channel => new LimitSetting(channel, session.Tolerance(channel))).ToArray();
        References = DeviceNames.Skip(1).Select(model => new ReferenceSetting(session.Settings.References?.FirstOrDefault(reference => reference.Model == model)
            ?? new ReferenceInfo(model))).ToArray();
        loaded = true;
    }
}
