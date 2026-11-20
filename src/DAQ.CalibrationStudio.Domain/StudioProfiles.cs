namespace DAQ.CalibrationStudio.Domain;

public static class StudioProfiles
{
    public static IReadOnlyList<TestProfile> Defaults { get; } = CreateDefaults();

    public static IEnumerable<TestStep> AnalogPoints(ChannelDefinition channel, IEnumerable<double> points,
        double duration = 2, double? tolerance = null, double? stability = null, double window = 1)
        => points.Select(point => new TestStep(StepKind.AnalogPoint, channel.Id, point, duration,
            tolerance ?? channel.Tolerance, stability ?? (channel.Quantity == Quantity.Current ? 0.002 : 0.0005), window));

    public static double[] SuggestedPoints(ChannelDefinition channel) =>
        Enumerable.Range(0, 6).Select(i => channel.Minimum + i * channel.Span / 5).ToArray();

    private static TestProfile[] CreateDefaults()
    {
        var voltage = ChannelCatalog.All.Where(channel => channel.Quantity == Quantity.Voltage)
            .SelectMany(channel => AnalogPoints(channel, SuggestedPoints(channel))).ToArray();
        var current = ChannelCatalog.All.Where(channel => channel.Quantity == Quantity.Current)
            .SelectMany(channel => AnalogPoints(channel, SuggestedPoints(channel))).ToArray();
        var frequency = new[] { 1_000d, 10_000d, 51_200d, 100_000d }
            .Select(value => new TestStep(StepKind.FrequencyPoint, "ENC1", value, 1, 50)).ToArray();
        var digital = ChannelCatalog.All.Where(channel => channel.DigitalOutput is not null)
            .SelectMany(channel => new[] { 1d, 0d }
                .Select(value => new TestStep(StepKind.DigitalPoint, channel.Id, value, 0.2))).ToArray();
        return
        [
            Profile(1, "Spannungseingänge 0–10 V kalibrieren", RunKind.Calibration, voltage),
            Profile(2, "Stromeingänge 0–20 mA prüfen", RunKind.Verification,
                current.Where(step => step.ChannelId != "AI-I2").ToArray()),
            Profile(3, "Stromeingang 4–20 mA kalibrieren", RunKind.Calibration,
                current.Where(step => step.ChannelId == "AI-I2").ToArray()),
            Profile(4, "Encoder-Frequenztest", RunKind.Functional, frequency),
            Profile(5, "DAQ-Gesamtprüfung", RunKind.Functional, [.. voltage, .. current, .. frequency, .. digital]),
            Profile(6, "Digitale Loopback-Prüfung", RunKind.Functional, digital),
            Profile(7, "Spannung 0–10 V verifizieren", RunKind.Verification, voltage)
        ];
    }

    private static TestProfile Profile(int number, string name, RunKind kind, IReadOnlyList<TestStep> steps)
        => new(new Guid($"da0c1000-0000-4000-8000-{number:000000000000}"), 1, name, kind, FitMethod.Regression, steps);
}
