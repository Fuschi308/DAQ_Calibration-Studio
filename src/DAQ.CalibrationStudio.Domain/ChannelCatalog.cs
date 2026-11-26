namespace DAQ.CalibrationStudio.Domain;

public static class ChannelCatalog
{
    public static IReadOnlyList<ChannelDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new ChannelDefinition("AI-V1", "Spannungseingang 1", Quantity.Voltage, ChannelGroup.Analog, 0, 10, 0.008, "0–10 V", "ADS131E06 CH1", 0.2),
        new ChannelDefinition("AI-V2", "Spannungseingang 2", Quantity.Voltage, ChannelGroup.Analog, 0, 10, 0.008, "0–10 V", "ADS131E06 CH2", 0.2),
        new ChannelDefinition("AI-V3", "Spannungseingang 3", Quantity.Voltage, ChannelGroup.Analog, 0, 10, 0.008, "0–10 V", "ADS131E06 CH3", 0.2),
        new ChannelDefinition("AI-I1", "Stromeingang 1", Quantity.Current, ChannelGroup.Analog, 0, 20, 0.012, "0–20 mA", "ADS131E06 CH4", 0.1),
        new ChannelDefinition("AI-I2", "Stromeingang 2", Quantity.Current, ChannelGroup.Analog, 4, 20, 0.012, "4–20 mA", "ADS131E06 CH5", 0.1),
        new ChannelDefinition("AI-I3", "Stromeingang 3", Quantity.Current, ChannelGroup.Analog, 0, 20, 0.012, "0–20 mA", "ADS131E06 CH6", 0.1),
        new ChannelDefinition("RTD1", "Temperatur 1", Quantity.Resistance, ChannelGroup.Rtd, 0, 390, 0.08, "PT100 · 4-Leiter", "RTD 1", 2.4 / 390),
        new ChannelDefinition("RTD2", "Temperatur 2", Quantity.Resistance, ChannelGroup.Rtd, 0, 390, 0.08, "PT100 · 4-Leiter", "RTD 2", 2.4 / 390),
        new ChannelDefinition("RTD3", "Temperatur 3", Quantity.Resistance, ChannelGroup.Rtd, 0, 3900, 0.8, "PT1000 · 3-Leiter", "RTD 3", 2.4 / 3900),
        new ChannelDefinition("RTD4", "Temperatur 4", Quantity.Resistance, ChannelGroup.Rtd, 0, 3900, 0.8, "PT1000 · 2-Leiter", "RTD 4", 2.4 / 3900),
        new ChannelDefinition("ENC1", "Quadraturencoder", Quantity.Frequency, ChannelGroup.Counter, 0, 1_000_000, 50, "RS422 A/B, 4-fach", "TIM Encoder"),
        new ChannelDefinition("HSC1", "Zähler 1", Quantity.Frequency, ChannelGroup.Counter, 0, 1_000_000, 50, "24 V · nicht belegt", "TIM", Unwired: true),
        new ChannelDefinition("HSC2", "Zähler 2", Quantity.Frequency, ChannelGroup.Counter, 0, 1_000_000, 50, "5 V · nicht belegt", "TIM", Unwired: true),
        new ChannelDefinition("DI1", "Digitaleingang 1", Quantity.Digital, ChannelGroup.Digital, 0, 1, 0, "24 V · Loopback DO1", "Digital I/O", DigitalOutput: "DO1"),
        new ChannelDefinition("DI2", "Digitaleingang 2", Quantity.Digital, ChannelGroup.Digital, 0, 1, 0, "24 V · Loopback DO2", "Digital I/O", DigitalOutput: "DO2"),
        new ChannelDefinition("DI3", "Digitaleingang 3", Quantity.Digital, ChannelGroup.Digital, 0, 1, 0, "24 V · Loopback DO3", "Digital I/O", DigitalOutput: "DO3"),
        new ChannelDefinition("DI4", "Digitaleingang 4", Quantity.Digital, ChannelGroup.Digital, 0, 1, 0, "24 V · Loopback DO4", "Digital I/O", DigitalOutput: "DO4"),
        new ChannelDefinition("DI5", "Digitaleingang 5", Quantity.Digital, ChannelGroup.Digital, 0, 1, 0, "24 V · offen", "Digital I/O", Unwired: true)
    });

    public static ChannelDefinition Get(string id) => All.FirstOrDefault(channel => channel.Id == id)
        ?? throw new ArgumentException($"Unbekannter Kanal {id}.", nameof(id));

    public static string Unit(Quantity quantity) => quantity switch
    {
        Quantity.Voltage => "V",
        Quantity.Current => "mA",
        Quantity.Resistance => "Ω",
        Quantity.Frequency => "Hz",
        _ => ""
    };

    public static string Name(Quantity quantity) => quantity switch
    {
        Quantity.Voltage => "Spannung",
        Quantity.Current => "Strom",
        Quantity.Resistance => "RTD",
        Quantity.Frequency => "Frequenz",
        _ => "Digital"
    };

    public static string GroupName(ChannelGroup group) => group switch
    {
        ChannelGroup.Analog => "Analogeingänge",
        ChannelGroup.Rtd => "RTD",
        ChannelGroup.Counter => "Zähler",
        _ => "Digitaleingänge"
    };

    public static double ToBaseUnit(double value, Quantity quantity) => quantity == Quantity.Current ? value / 1_000 : value;
    public static double FromBaseUnit(double value, Quantity quantity) => quantity == Quantity.Current ? value * 1_000 : value;
}
