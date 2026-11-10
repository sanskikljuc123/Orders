using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

/// <summary>Serilog sink that keeps every event as text: message, all properties, exception.</summary>
public sealed class LogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<string> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(string.Join(" | ",
        logEvent.RenderMessage(),
        string.Join(", ", logEvent.Properties.Select(p => $"{p.Key}={p.Value}")),
        logEvent.Exception?.ToString() ?? ""));

    public IReadOnlyList<string> Events => _events.ToArray();
}
