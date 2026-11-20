namespace Daq.Service.Protocol;

/// <summary>
/// Eckdaten des USB-Serviceprotokolls. Spezifikation:
/// DAQ_STM32_Firmware/docs/service_protocol.md
/// </summary>
public static class ServiceProtocol
{
    /// <summary>Unterstützte Protokollversion (<c>proto</c> in <c>*IDN?</c> und <c>!HELLO</c>).</summary>
    public const int Version = 1;
    public const int MaxLineLength = 255;
    public const string Identify = "*IDN?";
    public const string State = "SYS:STATE?";
    public const string Fault = "SYS:FAULT?";
    public const string Monitor = "MON?";
    public const string Analog = "MEAS:ANALOG?";
    public const string Rtd = "MEAS:RTD?";
    public const string InputsAndCounters = "MEAS:IO?";
    public const string StartRtd = "START_RTD";
    public const string StopRtd = "STOP_RTD";
    public const string StartCounters = "START_COUNTERS";
    public const string Calibration = "CAL?";
    public const string BeginCalibration = "CAL:BEGIN";
    public const string CalibrationPoint = "CAL:POINT";
    public const string StoreCalibration = "CAL:STORE";
    public const string AbortCalibration = "CAL:ABORT";
    public const string Configuration = "CFG?";
    public const string SetConfiguration = "CFG:SET";
    public const string StoreConfiguration = "CFG:STORE";
    public const string ReloadConfiguration = "CFG:RELOAD";
    public const int DigitalInputCount = 8;
    public const int RtdCount = 4;
    public const int MaxDebounceMs = 1000;
    public const string Start = "START";
    public const string Stop = "STOP";
    public const string Acknowledge = "SYS:FAULT:CLEAR";
    public const string EnterDfu = "SYS:DFU";
}
