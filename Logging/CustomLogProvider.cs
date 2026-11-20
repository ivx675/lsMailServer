using lsMailServer.RestApi;
using System.Collections.Concurrent;

namespace lsMailServer.Logging 
{
    /// <summary>
    /// Custom <see cref="ILoggerProvider"/> that routes server log events to the live ring buffer and persisted warning/error storage.
    /// </summary>
    public sealed class CustomLogProvider : ILoggerProvider
    {
        #region class CustomLogger

        /// <summary>
        /// Category-scoped <see cref="ILogger"/> implementation that forwards log records to the live buffer and persists warnings and errors.
        /// </summary>
        private sealed class CustomLogger : ILogger
        {
            private readonly string _category;
            private readonly ILiveLogBuffer _liveBuffer;
            private readonly IStoredLogWriter _storedWriter;

            /// <summary>
            /// Initializes a new instance of the <see cref="CustomLogger"/> class.
            /// </summary>
            /// <param name="category">The normalized logging category or source label.</param>
            /// <param name="liveBuffer">The live in-memory log ring buffer.</param>
            /// <param name="storedWriter">The writer for persisted warning and error log entries.</param>
            public CustomLogger(string category, ILiveLogBuffer liveBuffer, IStoredLogWriter storedWriter)
            {
                _category = category;
                _liveBuffer = liveBuffer;
                _storedWriter = storedWriter;
            }

            /// <summary>
            /// Begins a logical operation scope (not used; returns <see langword="null"/>).
            /// </summary>
            /// <typeparam name="TState">The type of the scope state.</typeparam>
            /// <param name="state">The scope state object.</param>
            /// <returns><see langword="null"/>.</returns>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <summary>
            /// Determines whether logging is enabled for the specified <see cref="LogLevel"/>.
            /// </summary>
            /// <param name="logLevel">The log severity level to check.</param>
            /// <returns><see langword="true"/> for all levels except <see cref="LogLevel.None"/>.</returns>
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            /// <summary>
            /// Formats and records a log event in the live buffer, and persists it to disk when severity is <see cref="LogLevel.Warning"/> or higher.
            /// </summary>
            /// <typeparam name="TState">The type of the state object being logged.</typeparam>
            /// <param name="logLevel">The severity level of the event.</param>
            /// <param name="eventId">The event identifier associated with the log entry.</param>
            /// <param name="state">The state entry to format.</param>
            /// <param name="exception">Optional exception associated with the event.</param>
            /// <param name="formatter">Function used to format <paramref name="state"/> and <paramref name="exception"/> into a message.</param>
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                string message = formatter(state, exception);
                if (string.IsNullOrWhiteSpace(message) && exception is null)
                    return;

                string? detail = exception?.ToString();
                DateTimeOffset timestamp = DateTimeOffset.UtcNow;

                _liveBuffer.Add(timestamp, logLevel, _category, message, detail);

                if (logLevel >= LogLevel.Warning) {
                    var entry = new LogEntryDto(
                        Id: 0,
                        Timestamp: timestamp,
                        Level: logLevel.ToString(),
                        Category: _category,
                        Message: message,
                        Detail: detail
                    );

                    _storedWriter.Write(entry);
                }
            }
        }

        #endregion

        private readonly ILiveLogBuffer _liveBuffer;
        private readonly IStoredLogWriter _storedWriter;
        private readonly ConcurrentDictionary<string, CustomLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="CustomLogProvider"/> class.
        /// </summary>
        /// <param name="liveBuffer">The in-memory live log buffer for real-time admin log streaming.</param>
        /// <param name="storedWriter">The writer responsible for persisting warning and error entries to disk.</param>
        public CustomLogProvider(ILiveLogBuffer liveBuffer, IStoredLogWriter storedWriter)
        {
            _liveBuffer = liveBuffer;
            _storedWriter = storedWriter;
        }


        #region method CreateLogger

        /// <summary>
        /// Creates or retrieves a cached <see cref="CustomLogger"/> instance for the specified logging category.
        /// </summary>
        /// <param name="categoryName">The raw logger category name supplied by the logging framework.</param>
        /// <returns>An <see cref="ILogger"/> instance configured with a normalized category name.</returns>
        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new CustomLogger(NormalizeCategory(name), _liveBuffer, _storedWriter));
        }

        #endregion

        #region method Dispose

        /// <summary>
        /// Releases cached logger instances held by the provider.
        /// </summary>
        public void Dispose()
        {
            _loggers.Clear();
        }

        #endregion

        #region method NormalizeCategory

        /// <summary>
        /// Maps raw logger category names into concise protocol or short type names for filtering in the admin UI.
        /// </summary>
        /// <param name="categoryName">The full logger category name.</param>
        /// <returns>A normalized category label such as <c>"SMTP"</c>, <c>"Relay"</c>, <c>"IMAP"</c>, <c>"POP3"</c>, or the short class name.</returns>
        private static string NormalizeCategory(string categoryName)
        {
            if (categoryName.Contains("Smtp", StringComparison.OrdinalIgnoreCase)) return "SMTP";
            if (categoryName.Contains("Relay", StringComparison.OrdinalIgnoreCase)) return "Relay";
            if (categoryName.Contains("Imap", StringComparison.OrdinalIgnoreCase)) return "IMAP";
            if (categoryName.Contains("Pop3", StringComparison.OrdinalIgnoreCase)) return "POP3";

            int lastDot = categoryName.LastIndexOf('.');
            return lastDot >= 0 && lastDot < categoryName.Length - 1
                ? categoryName[(lastDot + 1)..]
                : categoryName;
        } 
        
        #endregion
    }
}
