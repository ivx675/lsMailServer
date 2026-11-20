using lsMailServer.RestApi;
using System.Text;
using System.Text.Json;

namespace lsMailServer.Logging 
{
    /// <summary>
    /// Appends warning and error log entries as newline-delimited JSON (NDJSON) to daily log files on disk.
    /// </summary>
    public sealed class StoredLogWriter : IStoredLogWriter
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false
        };

        private readonly string _logDirectory;
        private readonly object _syncRoot = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="StoredLogWriter"/> class and ensures the target directory exists.
        /// </summary>
        /// <param name="logDirectory">Directory path where daily stored log files are written.</param>
        public StoredLogWriter(string logDirectory)
        {
            _logDirectory = Path.GetFullPath(logDirectory);
            Directory.CreateDirectory(_logDirectory);
        }


        #region method Write

        /// <summary>
        /// Appends <paramref name="entry"/> to <c>errors-yyyy-MM-dd.log</c> in UTC if its severity is <see cref="LogLevel.Warning"/> or higher.
        /// </summary>
        /// <param name="entry">The log entry to evaluate and persist.</param>
        public void Write(LogEntryDto entry)
        {
            if (!ShouldPersist(entry.Level))
                return;

            string fileName = $"errors-{entry.Timestamp.ToUniversalTime():yyyy-MM-dd}.log";
            string fullPath = Path.Combine(_logDirectory, fileName);
            string jsonLine = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;

            lock (_syncRoot) {
                Directory.CreateDirectory(_logDirectory);
                using var fs = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                sw.Write(jsonLine);
            }
        }

        #endregion

        #region method ShouldPersist

        /// <summary>
        /// Determines whether a log level name represents <see cref="LogLevel.Warning"/> or a more severe level.
        /// </summary>
        /// <param name="level">The log level name to parse.</param>
        /// <returns><see langword="true"/> if the level is valid and at least <see cref="LogLevel.Warning"/>; otherwise <see langword="false"/>.</returns>
        private static bool ShouldPersist(string level)
        {
            if (!Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed))
                return false;

            return parsed >= LogLevel.Warning;
        }

        #endregion
    }
}
