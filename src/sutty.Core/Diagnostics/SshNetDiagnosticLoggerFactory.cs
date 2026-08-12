using Microsoft.Extensions.Logging;

namespace sutty.Core.Diagnostics;

/// <summary>
/// Per-session SSH.NET logger. Trace is enabled only around ConnectAsync, preventing
/// normal terminal packet traffic from flooding the connection-failure log.
/// </summary>
internal sealed class SshNetDiagnosticLoggerFactory : ILoggerFactory
{
    private readonly Guid _sessionId;
    private readonly string _sessionTitle;
    private readonly string _endpoint;
    private int _captureDepth;

    public SshNetDiagnosticLoggerFactory(
        Guid sessionId,
        string sessionTitle,
        string endpoint)
    {
        _sessionId = sessionId;
        _sessionTitle = sessionTitle;
        _endpoint = endpoint;
    }

    public IDisposable BeginCapture()
    {
        Interlocked.Increment(ref _captureDepth);
        return new CaptureScope(this);
    }

    public ILogger CreateLogger(string categoryName) =>
        new SshNetDiagnosticLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
        // SSH.NET only asks the factory for loggers. External providers are not needed.
    }

    public void Dispose()
    {
    }

    private bool IsCapturing => Volatile.Read(ref _captureDepth) > 0;

    private void EndCapture() => Interlocked.Decrement(ref _captureDepth);

    private sealed class CaptureScope(SshNetDiagnosticLoggerFactory owner) : IDisposable
    {
        private SshNetDiagnosticLoggerFactory? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndCapture();
    }

    private sealed class SshNetDiagnosticLogger(
        SshNetDiagnosticLoggerFactory owner,
        string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            owner.IsCapturing && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
                return;

            ConnectionLogStore.Append(
                owner._sessionId,
                owner._sessionTitle,
                owner._endpoint,
                Map(logLevel),
                string.IsNullOrWhiteSpace(categoryName) ? "SSH.NET" : categoryName,
                message,
                message,
                exception is null ? null : ConnectionFailureDetails.Format(exception));
        }

        private static ConnectionLogSeverity Map(LogLevel level) => level switch
        {
            LogLevel.Trace => ConnectionLogSeverity.Verbose,
            LogLevel.Debug => ConnectionLogSeverity.Debug,
            LogLevel.Information => ConnectionLogSeverity.Information,
            LogLevel.Warning => ConnectionLogSeverity.Warning,
            LogLevel.Error => ConnectionLogSeverity.Error,
            LogLevel.Critical => ConnectionLogSeverity.Critical,
            _ => ConnectionLogSeverity.Verbose,
        };
    }
}
