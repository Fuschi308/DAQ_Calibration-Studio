using DAQ.CalibrationStudio.Application;

namespace DAQ.CalibrationStudio.Instruments;

public static class HardwareInstruments
{
    public static IDeviceSession[] Create() => [new FnirsiDevice(), new OwonDevice(), new PeakTechDevice()];
}
