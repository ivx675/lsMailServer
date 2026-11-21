using lsMailServer.RestApi;

namespace lsMailServer.Logging 
{
    /// <summary>
    /// Provides read access to persisted log files and filtered entries on disk.
    /// </summary>
    public interface IStoredLogReader
    {
        /// <summary>
        /// Lists available persisted log files ordered for selection in the admin viewer.
        /// </summary>
        /// <returns>A read-only list of <see cref="StoredLogFileDto"/> metadata entries.</returns>
        IReadOnlyList<StoredLogFileDto> GetFiles();

        /// <summary>
        /// Reads log entries from the specified stored log file (or default file when <see langword="null"/>) matching the supplied filters.
        /// </summary>
        /// <param name="fileName">Target log file name, or <see langword="null"/> to read the default/latest log file.</param>
        /// <param name="level">Optional severity level filter.</param>
        /// <param name="category">Optional category or source filter.</param>
        /// <param name="search">Optional case-insensitive substring filter across entry fields.</param>
        /// <param name="limit">Maximum number of matching entries to return.</param>
        /// <returns>A read-only list of matching <see cref="LogEntryDto"/> entries.</returns>
        IReadOnlyList<LogEntryDto> ReadEntries(string? fileName, string? level, string? category, string? search, int limit);
    }
}
