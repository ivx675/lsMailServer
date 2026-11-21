using lsMailServer.RestApi;

namespace lsMailServer.Logging 
{
    /// <summary>
    /// Thread-safe in-memory ring buffer for live server log entries supporting cursor-based delta polling.
    /// </summary>
    public interface ILiveLogBuffer
    {
        /// <summary>
        /// Appends a new log entry to the live buffer, assigning a monotonic identifier.
        /// </summary>
        /// <param name="timestamp">The UTC timestamp when the log event occurred.</param>
        /// <param name="level">The severity level of the log entry.</param>
        /// <param name="category">The logging category or protocol/subsystem source.</param>
        /// <param name="message">The formatted log message.</param>
        /// <param name="detail">Optional diagnostic or exception details.</param>
        void Add(DateTimeOffset timestamp, LogLevel level, string category, string message, string? detail = null);

        /// <summary>
        /// Retrieves matching log entries added after the specified sequence identifier.
        /// </summary>
        /// <param name="afterId">Exclusive lower-bound entry ID for incremental polling; pass <c>0</c> to fetch from the buffer start.</param>
        /// <param name="minLevel">Optional minimum log severity filter.</param>
        /// <param name="category">Optional category or source filter.</param>
        /// <param name="search">Optional case-insensitive substring filter across message, category, and detail.</param>
        /// <param name="limit">Maximum number of matching entries to return.</param>
        /// <returns>A read-only list of matching log entries ordered by ascending <see cref="LogEntryDto.Id"/>.</returns>
        IReadOnlyList<LogEntryDto> GetEntries(long afterId, LogLevel? minLevel, string? category, string? search, int limit);

        /// <summary>
        /// Removes all currently buffered entries while preserving monotonic sequence IDs.
        /// </summary>
        void Clear();
    }
}
