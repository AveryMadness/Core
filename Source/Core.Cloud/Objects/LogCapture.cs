using System.Collections.Concurrent;

using Serilog.Core;
using Serilog.Events;

namespace Core.Cloud.Objects;

/* What the logger was told while a request was being answered.
 *
 * CUE4Parse says what went wrong with a package by logging it and carrying on: a bulk data segment
 * it cannot open, a read that came up short, a version it does not know. None of that reaches the
 * caller, only the log file, so a diagnosis that wants to say why a mesh came back empty has to
 * listen to the logger while it reads the mesh. Registered as a sink by the application, kept off
 * until a capture asks, and one at a time: this is a single-user tool. */
public sealed class LogCapture : ILogEventSink
{
    public static readonly LogCapture Instance = new();

    private readonly ConcurrentQueue<LogEvent> _events = new();
    private volatile bool _capturing;

    public void Emit(LogEvent logEvent)
    {
        if (_capturing) _events.Enqueue(logEvent);
    }

    public IDisposable Begin()
    {
        _events.Clear();
        _capturing = true;

        return new Scope(this);
    }

    public IReadOnlyList<CapturedLine> Drain()
    {
        var lines = new List<CapturedLine>();

        while (_events.TryDequeue(out var logEvent))
        {
            lines.Add(new CapturedLine(
                logEvent.Timestamp.ToString("HH:mm:ss.fff"),
                logEvent.Level.ToString(),
                logEvent.RenderMessage(),
                logEvent.Exception?.ToString()));
        }

        return lines;
    }

    public sealed record CapturedLine(string Time, string Level, string Message, string? Exception);

    private sealed class Scope(LogCapture owner) : IDisposable
    {
        public void Dispose() => owner._capturing = false;
    }
}
