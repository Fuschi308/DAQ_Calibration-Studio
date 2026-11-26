using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Daq;
using DAQ.CalibrationStudio.Instruments;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class DevicesViewModel : ObservableObject
{
    private static readonly DeviceKind[] Kinds = [DeviceKind.Daq, DeviceKind.Fnirsi, DeviceKind.Owon, DeviceKind.PeakTech];
    private readonly StudioSession session;
    private readonly Dictionary<(bool Simulated, DeviceKind Kind), string> addresses = new();
    private DeviceStatus? lastStatus;
    private bool lastBusy;
    private bool lastMode;
    private bool lastAvailable;
    private bool loadingEndpoints;
    private Fact[] details = [];

    public DevicesViewModel(StudioSession session)
    {
        this.session = session;
        Daq = new(this, session);
        Fnirsi = new(this, session);
        Owon = new(this, session);
        Scope = new(this, session);
        session.ModeChanged += () =>
        {
            LoadEndpoint();
            UpdateStatus();
        };
        LoadEndpoint();
        UpdateStatus();
    }

    public IReadOnlyList<string> DeviceNames { get; } = ["STM32H7-DAQ", "FNIRSI SG-004A", "OWON DGE", "PeakTech P 1400"];
    public DeviceDaqViewModel Daq { get; }
    public DeviceSourceViewModel Fnirsi { get; }
    public DeviceGeneratorViewModel Owon { get; }
    public DeviceScopeViewModel Scope { get; }
    public DeviceKind CurrentDevice { get; private set; } = DeviceKind.Daq;
    public bool ShowDaq => CurrentDevice == DeviceKind.Daq;
    public bool ShowFnirsi => CurrentDevice == DeviceKind.Fnirsi;
    public bool ShowOwon => CurrentDevice == DeviceKind.Owon;
    public bool ShowScope => CurrentDevice == DeviceKind.PeakTech;
    public bool IsConnected => CurrentStatus.Connected;
    public bool Simulation => session.Devices.Active.Simulated;
    public bool CanChooseScopeMode => ShowScope && Simulation;
    public string ScopeModeText => ScopeSimulation ? "Reales P 1400 auswählen" : "Simuliertes Oszilloskop wählen";

    internal bool ScopeSimulation => session.Devices.Active.ScopeIsSimulated;
    private bool CurrentDeviceSimulated => session.Devices.Active.IsDeviceSimulated(CurrentDevice);

    internal DeviceStatus CurrentStatus => session.Devices.Active[CurrentDevice].Status;
    internal bool Can(DeviceKind kind, DeviceCapability capability) =>
        !session.Busy && session.Devices.Active[kind].Status is { Connected: true } status && status.Has(capability);

    [ObservableProperty] private string deviceName = "STM32H7-DAQ";
    [ObservableProperty] private string icon = "daq";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] private string statusText = "Getrennt";
    [ObservableProperty] private string stateTone = "";
    [ObservableProperty] private string connectText = "Verbinden";
    [ObservableProperty] private string endpoint = "";
    [ObservableProperty] private string[] endpoints = [];
    [ObservableProperty] private Fact[] facts = [];
    [ObservableProperty] private Fact[] capabilities = [];
    [ObservableProperty] private Fact[] diagnostics = [];

    public void Open(DeviceKind kind)
    {
        CurrentDevice = kind;
        DeviceName = DeviceNames[Array.IndexOf(Kinds, kind)];
        OnPropertyChanged(nameof(CurrentDevice));
        OnPropertyChanged(nameof(ShowDaq));
        OnPropertyChanged(nameof(ShowFnirsi));
        OnPropertyChanged(nameof(ShowOwon));
        OnPropertyChanged(nameof(ShowScope));
        LoadEndpoint();
        UpdateStatus();
    }

    partial void OnDeviceNameChanged(string value)
    {
        var index = Array.IndexOf(DeviceNames.ToArray(), value);
        if (index >= 0 && Kinds[index] != CurrentDevice)
            Open(Kinds[index]);
    }

    partial void OnEndpointChanged(string value)
    {
        if (!loadingEndpoints && !string.IsNullOrWhiteSpace(value))
            addresses[(CurrentDeviceSimulated, CurrentDevice)] = value;
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private void LoadEndpoint()
    {
        loadingEndpoints = true;
        Endpoints = CurrentDeviceSimulated ? ["Simulation"] : [];
        Endpoint = CurrentDeviceSimulated ? "Simulation" : addresses.GetValueOrDefault((false, CurrentDevice), "");
        loadingEndpoints = false;
        details = [];
        lastStatus = null;
    }

    public void Tick()
    {
        UpdateStatus();
        switch (CurrentDevice)
        {
            case DeviceKind.Daq: Daq.Tick(); break;
            case DeviceKind.Fnirsi: Fnirsi.Tick(); break;
            case DeviceKind.Owon: Owon.Tick(); break;
            case DeviceKind.PeakTech: Scope.Tick(); break;
        }
    }

    private bool connectionAllowed => !session.Busy && CurrentStatus.State != DeviceState.Connecting
        && (CurrentStatus.Connected || CurrentDeviceSimulated || !string.IsNullOrWhiteSpace(Endpoint));

    private bool canDiscover => !session.Busy;
    private bool canIdentify => !session.Busy && IsConnected;
    private bool canToggleScopeMode => !session.Busy && CanChooseScopeMode;

    [RelayCommand(CanExecute = nameof(canToggleScopeMode))]
    private async Task ToggleScopeModeAsync()
    {
        var chooseHardware = ScopeSimulation;
        await session.PerformAsync(async () =>
        {
            await session.Devices.SetSimulationScopeAsync(chooseHardware, session.LifetimeToken);
            if (CurrentDevice == DeviceKind.PeakTech)
                LoadEndpoint();
            session.Say(chooseHardware
                ? "P 1400 ausgewählt. Gerät suchen und verbinden."
                : "Simuliertes Oszilloskop verbunden.");
        }, exclusive: true);
        UpdateStatus(force: true);
    }

    [RelayCommand(CanExecute = nameof(connectionAllowed))]
    private Task ConnectAsync()
    {
        var kind = CurrentDevice;
        var address = CurrentDeviceSimulated ? "Simulation" : Endpoint;
        return PerformAsync(async (equipment, token) =>
        {
            var device = equipment[kind];
            if (device.Status.Connected)
                await device.DisconnectAsync(token);
            else
                await device.ConnectAsync(address, token);
            session.LogDevice(device.Status.Name, device.Status.Connected ? "Verbunden." : "Getrennt.");
            details = [];
        });
    }

    [RelayCommand(CanExecute = nameof(canDiscover))]
    private Task DiscoverAsync()
    {
        var kind = CurrentDevice;
        return session.PerformAsync(async () =>
        {
            IReadOnlyList<string> matches = [];
            await session.Devices.UseAsync(async (equipment, token) =>
                matches = await equipment[kind].DiscoverAsync(token), session.LifetimeToken);
            if (CurrentDevice != kind) return;
            var current = Endpoint;
            Endpoints = matches.ToArray();
            Endpoint = matches.Count == 1 && string.IsNullOrWhiteSpace(current) ? matches[0] : current;
            session.Say(matches.Count == 0 ? "Keine passende Schnittstelle gefunden." :
                $"{matches.Count} Schnittstelle(n) gefunden. Ziel auswählen und verbinden.");
        }, exclusive: true);
    }

    [RelayCommand(CanExecute = nameof(canIdentify))]
    private Task IdentifyAsync() => IdentifyDeviceAsync(CurrentDevice);

    internal Task IdentifyDeviceAsync(DeviceKind kind) => PerformAsync(async (equipment, token) =>
    {
        var device = equipment[kind];
        await device.IdentifyAsync(token);
        Fact[] result = device switch
        {
            UsbDaqDevice daq => Daq.ApplyDiagnostics(await daq.ReadDiagnosticsAsync(token), daq.LastCounters),
            FnirsiDevice source => SourceDiagnostics(await source.ReadRawDiagnosticsAsync(token)),
            OwonDevice generator => [new("SCPI-Fehlerwarteschlange", await generator.ReadNextErrorAsync(token))],
            _ => [new("Verbindungstest", "Geräteantwort empfangen", "ok")]
        };
        if (kind == CurrentDevice) details = result;
    }, "Geräteantwort empfangen.");

    private static Fact[] SourceDiagnostics((ushort OutputSwitchRegister, float MeasuredInput) values) =>
    [
        new("Register 40008", $"0x{values.OutputSwitchRegister:X4}"),
        new("Gemeldeter Eingangswert", Presentation.Number(values.MeasuredInput, "0.0000")),
        new("Elektrischer Ausgang", "Unbekannt", "warn")
    ];

    internal async Task PerformAsync(Func<Equipment, CancellationToken, Task> operation, string? success = null)
    {
        await session.PerformAsync(() => session.Devices.UseAsync(async (equipment, token) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                await operation(equipment, token);
            else
                await Dispatcher.UIThread.InvokeAsync(() => operation(equipment, token));
        }, session.LifetimeToken), success, exclusive: true);
        UpdateStatus(force: true);
    }

    internal void PublishDaqDiagnostics(Fact[] diagnostics)
    {
        if (CurrentDevice != DeviceKind.Daq) return;
        details = diagnostics;
        Diagnostics = diagnostics;
    }

    private void UpdateStatus(bool force = false)
    {
        var equipment = session.Devices.Active;
        var status = equipment[CurrentDevice].Status;
        var busy = session.Busy;
        var simulated = equipment.IsDeviceSimulated(CurrentDevice);
        var deviceChanged = lastStatus != status || lastMode != equipment.Simulated;
        var commandChanged = deviceChanged || lastBusy != busy;
        if (!force && !deviceChanged && !commandChanged) return;
        lastStatus = status;
        lastMode = equipment.Simulated;
        lastBusy = busy;
        if (lastAvailable != status.Connected)
        {
            lastAvailable = status.Connected;
            OnPropertyChanged(nameof(IsConnected));
        }

        Title = status.Name;
        (StatusText, StateTone) = Presentation.Connection(status, simulated);
        ConnectText = status.Connected ? "Trennen" : "Verbinden";
        Icon = CurrentDevice switch { DeviceKind.Daq => "daq", DeviceKind.Fnirsi => "source", DeviceKind.Owon => "generator", _ => "scope" };
        Subtitle = $"{status.Interface} · {status.Verification}";
        Facts =
        [
            new("Modell", status.Identity?.Model ?? "—"),
            new("Seriennummer", status.Identity?.Serial ?? "—"),
            new("Firmware", status.Identity?.Firmware ?? "—"),
            new("Hardware-Revision", status.Identity?.Revision ?? "—"),
            new("Schnittstelle", status.Interface),
            new("Treiber", DriverText(CurrentDevice, simulated)),
            new("Prüfstatus", status.Verification)
        ];
        Diagnostics = status.Error is { Length: > 0 } error ? [new("Gerätefehler", error, "err")] : details;
        Capabilities = DescribeCapabilities(status);
        if (!commandChanged && !force) return;
        ConnectCommand.NotifyCanExecuteChanged();
        DiscoverCommand.NotifyCanExecuteChanged();
        IdentifyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChooseScopeMode));
        OnPropertyChanged(nameof(ScopeModeText));
        ToggleScopeModeCommand.NotifyCanExecuteChanged();
        Daq.RefreshAvailability();
        Fnirsi.RefreshAvailability();
        Owon.RefreshAvailability();
        Scope.RefreshAvailability();
    }

    private static string DriverText(DeviceKind kind, bool sim) => sim ? "Studio-Simulator" : kind switch
    {
        DeviceKind.Daq => "DAQ USB-Service, Protokoll 1",
        DeviceKind.Fnirsi => "SG-004A, 9600 8N1",
        DeviceKind.Owon => "OWON DGE, VISA SCPI",
        _ => "PeakTech USB Bulk, libusb-win32"
    };

    private static Fact[] DescribeCapabilities(DeviceStatus status)
    {
        (DeviceCapability Flag, string Label)[] supported = status.Kind switch
        {
            DeviceKind.Daq => [(DeviceCapability.Measurements, "Messwerte"), (DeviceCapability.AcquisitionControl, "Start/Stopp"),
                (DeviceCapability.RawStreaming, "Rohdatenstream"), (DeviceCapability.Route, "Prüfadapter"),
                (DeviceCapability.DigitalOutput, "Digitalausgänge"), (DeviceCapability.Coefficients, "Koeffizienten")],
            DeviceKind.Fnirsi => [(DeviceCapability.AnalogSource, "Ausgang EIN/AUS"), (DeviceCapability.SetpointOnly, "Sollwert im aktiven Modus"),
                (DeviceCapability.Diagnostics, "Rücklesung")],
            DeviceKind.Owon => [(DeviceCapability.Waveform, "Zwei Kanäle"), (DeviceCapability.Sweep, "Frequenz-Sweep"),
                (DeviceCapability.Quadrature, "Dynamische Quadratur")],
            _ => [(DeviceCapability.Scope, "Bildschirmaufnahme"), (DeviceCapability.Diagnostics, "Gerätedaten")]
        };
        return supported.Select(item => new Fact(item.Label, status.Connected ? status.Has(item.Flag) ? "Verfügbar" : "Gesperrt" : "—",
            status.Connected && status.Has(item.Flag) ? "ok" : "")).ToArray();
    }
}
