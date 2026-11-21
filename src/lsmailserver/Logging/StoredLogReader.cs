using lsMailServer.RestApi;
using System.Text.Json;

namespace lsMailServer.Logging 
{
    /// <summary>
    /// Reads and filters persisted warning and error NDJSON log files from the configured log directory.
    /// </summary>
    public sealed class StoredLogReader : IStoredLogReader
    {
        private readonly string _logDirectory;

        /// <summary>
        /// Initializes a new instance of the <see cref="StoredLogReader"/> class.
        /// </summary>
        /// <param name="logDirectory">Directory path containing persisted <c>errors-*.log</c> files.</param>
        public StoredLogReader(string logDirectory)
        {
            _logDirectory = Path.GetFullPath(logDirectory);
        }


        #region method GetFiles

        /// <summary>
        /// Lists matching <c>errors-*.log</c> files ordered by most recent UTC write time first.
        /// </summary>
        /// <returns>An array of <see cref="StoredLogFileDto"/> descriptors, or an empty array if the directory does not exist.</returns>
        public IReadOnlyList<StoredLogFileDto> GetFiles()
        {
            if (!Directory.Exists(_logDirectory))
                return Array.Empty<StoredLogFileDto>();

            return new DirectoryInfo(_logDirectory)
                .GetFiles("errors-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new StoredLogFileDto(f.Name, f.Length, f.LastWriteTimeUtc))
                .ToArray();
        }

        #endregion

        #region method ReadEntries

        /// <summary>
        /// Reads the most recent matching log entries (up to <paramref name="limit"/>) in chronological order from the target log file.
        /// </summary>
        /// <param name="fileName">Target log file name within the log directory, or <see langword="null"/> to read the latest file.</param>
        /// <param name="level">Optional exact log level filter (ignored when <see langword="null"/>, empty, or <c>"All"</c>).</param>
        /// <param name="category">Optional exact category filter (ignored when <see langword="null"/>, empty, or <c>"All"</c>).</param>
        /// <param name="search">Optional case-insensitive substring filter across message, category, and detail.</param>
        /// <param name="limit">Maximum number of matching entries to return.</param>
        /// <returns>A list of matching <see cref="LogEntryDto"/> entries ordered chronologically.</returns>
        /// <exception cref="FileNotFoundException">Thrown when no stored log files exist or the resolved target file is missing.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="fileName"/> attempts path traversal or contains invalid path segments.</exception>
        public IReadOnlyList<LogEntryDto> ReadEntries(string? fileName, string? level, string? category, string? search, int limit)
        {
            string targetPath = ResolveSafeFilePath(fileName);
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("Log file not found.", targetPath);

            var matchedNewestFirst = new List<LogEntryDto>(limit);

            using var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);

            var allLines = new List<string>();
            while (sr.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line))
                    allLines.Add(line);
            }

            for (int i = allLines.Count - 1; i >= 0 && matchedNewestFirst.Count < limit; i--) {
                LogEntryDto? entry = TryParseLine(allLines[i]);
                if (entry is null) continue;

                if (!string.IsNullOrWhiteSpace(level) && !level.Equals("All", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(search)) {
                    bool hit = entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || (entry.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
                    if (!hit) continue;
                }

                matchedNewestFirst.Add(entry);
            }

            matchedNewestFirst.Reverse();
            return matchedNewestFirst;
        }

        #endregion

        #region method ResolveSafeFilePath

        /// <summary>
        /// Validates and resolves a log file name to a safe path inside <see cref="_logDirectory"/>, or selects the latest file when omitted.
        /// </summary>
        /// <param name="fileName">Requested file name or <see langword="null"/> for the newest file.</param>
        /// <returns>The full normalized path to the target log file.</returns>
        /// <exception cref="FileNotFoundException">Thrown when <paramref name="fileName"/> is omitted and no log files exist.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="fileName"/> is not a plain file name within the configured directory.</exception>
        private string ResolveSafeFilePath(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) {
                var latest = GetFiles().FirstOrDefault()
                    ?? throw new FileNotFoundException("No stored log files available.");
                return Path.Combine(_logDirectory, latest.FileName);
            }

            string safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal))
                throw new ArgumentException("Invalid log file name.", nameof(fileName));

            string fullPath = Path.GetFullPath(Path.Combine(_logDirectory, safeName));
            string rootWithSep = _logDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? _logDirectory
                : _logDirectory + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Log file path is outside the log directory.", nameof(fileName));

            return fullPath;
        }

        #endregion

        #region method TryParseLine

        /// <summary>
        /// Attempts to deserialize a single NDJSON log line into a <see cref="LogEntryDto"/>, falling back to a raw error entry on malformed JSON.
        /// </summary>
        /// <param name="line">Raw log line to parse.</param>
        /// <returns>A deserialized or fallback <see cref="LogEntryDto"/> instance.</returns>
        private static LogEntryDto? TryParseLine(string line)
        {
            try {
                return JsonSerializer.Deserialize<LogEntryDto>(line);
            }
            catch (JsonException) {
                return new LogEntryDto(0, DateTimeOffset.UtcNow, "Error", "System", line, null);
            }
        }

        #endregion
    }
}
