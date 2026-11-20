using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.App.Services;

public sealed record StudioLogEntry(DateTimeOffset Timestamp, string Level, string Source, string Message)
{
    public string Time => Timestamp.LocalDateTime.ToString("HH:mm:ss");
}

public sealed class StudioLog(StudioPaths paths) : ILoggerProvider
{
    private readonly Lock sync = new();
    private readonly Queue<StudioLogEntry> entries = new();
    private int revision;
    private string? fileError;

    public int Revision => Volatile.Read(ref revision);

    public StudioLogEntry[] Snapshot()
    {
        lock (sync)
            return entries.Reverse().ToArray();
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName.Split('.').Last());
    public void Dispose() { }

    private void Write(StudioLogEntry entry, object? properties, Exception? error)
    {
        lock (sync)
        {
            Enqueue(entry);
            try
            {
                Directory.CreateDirectory(paths.LogFolder);
                var path = Path.Combine(paths.LogFolder, $"{entry.Timestamp:yyyy-MM-dd}.jsonl");
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    entry.Timestamp, entry.Level, entry.Source, entry.Message,
                    Properties = properties, Exception = error?.ToString()
                }) + Environment.NewLine);
                fileError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (fileError != ex.Message)
                    Enqueue(new(entry.Timestamp, "Fehler", "Protokoll", $"Logdatei nicht beschreibbar. {ex.Message}"));
                fileError = ex.Message;
            }
        }
    }

    private void Enqueue(StudioLogEntry entry)
    {
        entries.Enqueue(entry);
        if (entries.Count > 400)
            entries.Dequeue();
        Interlocked.Increment(ref revision);
    }

    private sealed class Sink(StudioLog owner, string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Warning => "Warnung",
                >= LogLevel.Error => "Fehler",
                _ => "Info"
            };
            var message = formatter(state, exception);
            if (exception is not null)
                message += $" · {exception.Message}";
            var properties = (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(item =>
                new { item.Key, Value = item.Value?.ToString() }).ToArray();
            owner.Write(new(DateTimeOffset.Now, level, source, message), properties, exception);
        }
    }
}
