using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using lsMailServer.Logging;

namespace lsMailServer.RestApi 
{
    #region Dto and Request types

    /// <summary>
    /// Represents a structured server log entry returned by live or stored log endpoints.
    /// </summary>
    /// <param name="Id">Monotonic sequence identifier for live buffer delta polling, or <c>0</c> for stored log entries.</param>
    /// <param name="Timestamp">The timestamp when the log entry was recorded.</param>
    /// <param name="Level">The log severity level name (such as <c>Information</c>, <c>Warning</c>, or <c>Error</c>).</param>
    /// <param name="Category">The logging category or protocol/subsystem source.</param>
    /// <param name="Message">The formatted log message.</param>
    /// <param name="Detail">Optional diagnostic or exception details associated with the entry.</param>
    public sealed record LogEntryDto(
        long Id,
        DateTimeOffset Timestamp,
        string Level,
        string Category,
        string Message,
        string? Detail = null
    );

    /// <summary>
    /// Metadata describing a persisted log file available for inspection in the admin log viewer.
    /// </summary>
    /// <param name="FileName">The name of the log file (for example, <c>errors-yyyy-MM-dd.log</c>).</param>
    /// <param name="SizeBytes">The current file size in bytes.</param>
    /// <param name="LastWriteTimeUtc">The last modification timestamp of the file in UTC.</param>
    public sealed record StoredLogFileDto(
        string FileName,
        long SizeBytes,
        DateTime LastWriteTimeUtc
    );

    #endregion    

    /// <summary>
    /// Admin API controller providing access to live in-memory server logs and persisted stored log files.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] 
    public sealed class LogsController : ControllerBase
    {
        private const int MaxLimit = 1000;
        private const int DefaultLiveLimit = 250;
        private const int DefaultStoredLimit = 500;

        private readonly ILiveLogBuffer _liveLogs;
        private readonly IStoredLogReader _storedLogs;

        /// <summary>
        /// Initializes a new instance of the <see cref="LogsController"/> class.
        /// </summary>
        /// <param name="liveLogs">The live in-memory log ring buffer.</param>
        /// <param name="storedLogs">The reader for persisted warning and error log files.</param>
        public LogsController(ILiveLogBuffer liveLogs, IStoredLogReader storedLogs)
        {
            _liveLogs = liveLogs;
            _storedLogs = storedLogs;
        }

        /// <summary>
        /// Retrieves incremental live log entries from the in-memory ring buffer after the specified identifier.
        /// </summary>
        /// <param name="afterId">Exclusive lower-bound sequence ID for delta polling; defaults to <c>0</c>.</param>
        /// <param name="minLevel">Minimum log level name to include (such as <c>Information</c> or <c>Warning</c>); defaults to <c>"Information"</c>.</param>
        /// <param name="category">Optional category or source filter.</param>
        /// <param name="search">Optional case-insensitive substring filter across message, category, and detail.</param>
        /// <param name="limit">Maximum number of entries to return, clamped to allowed bounds.</param>
        /// <returns>A list of matching live <see cref="LogEntryDto"/> entries ordered by ascending <see cref="LogEntryDto.Id"/>.</returns>
        [HttpGet("live")]
        public ActionResult<IReadOnlyList<LogEntryDto>> GetLive(
            [FromQuery] long afterId = 0,
            [FromQuery] string? minLevel = "Information",
            [FromQuery] string? category = null,
            [FromQuery] string? search = null,
            [FromQuery] int limit = DefaultLiveLimit)
        {
            int clampedLimit = Math.Clamp(limit, 1, MaxLimit);
            LogLevel? parsedLevel = ParseLogLevel(minLevel);

            var entries = _liveLogs.GetEntries(afterId, parsedLevel, category, search, clampedLimit);

            return Ok(entries);
        }

        /// <summary>
        /// Clears all entries currently held in the in-memory live ring buffer.
        /// </summary>
        [HttpDelete("live")]
        public IActionResult ClearLive()
        {
            _liveLogs.Clear();

            return NoContent();
        }

        /// <summary>
        /// Lists available persisted warning/error log files on disk.
        /// </summary>
        [HttpGet("stored/files")]
        public ActionResult<IReadOnlyList<StoredLogFileDto>> GetStoredFiles()
        {
            return Ok(_storedLogs.GetFiles());
        }

        /// <summary>
        /// Reads persisted warning and error entries from a stored log file.
        /// </summary>
        /// <param name="file">Optional log file name; defaults to the newest file when omitted.</param>
        /// <param name="level">Optional level filter (Warning, Error, Critical, or All).</param>
        /// <param name="category">Optional source/category filter (e.g., SMTP, POP3, IMAP, Relay, System).</param>
        /// <param name="search">Optional case-insensitive substring filter across message, category, and detail.</param>
        /// <param name="limit">Maximum entries to return from the tail of the file (1..1000).</param>
        [HttpGet("stored")]
        public ActionResult<IReadOnlyList<LogEntryDto>> GetStored(
            [FromQuery] string? file = null,
            [FromQuery] string? level = null,
            [FromQuery] string? category = null,
            [FromQuery] string? search = null,
            [FromQuery] int limit = DefaultStoredLimit)
        {
            int clampedLimit = Math.Clamp(limit, 1, MaxLimit);
            try {
                var entries = _storedLogs.ReadEntries(file, level, category, search, clampedLimit);

                return Ok(entries);
            }
            catch (FileNotFoundException) {
                return NotFound(new { error = "Requested log file was not found." });
            }
            catch (ArgumentException ex) {
                return BadRequest(new { error = ex.Message });
            }
        }

        #region method ParseLogLevel

        /// <summary>
        /// Parses a log level query string into a nullable <see cref="LogLevel"/> filter value.
        /// </summary>
        /// <param name="value">The raw log level string from the query parameter (such as <c>"All"</c>, <c>"Warning"</c>, 
        /// or <c>"Error"</c>).</param>
        /// <returns><see langword="null"/> when no level filter or <c>"All"</c> is specified; 
        /// otherwise the parsed <see cref="LogLevel"/>, or <see cref="LogLevel.Information"/> if unrecognized.</returns>
        private static LogLevel? ParseLogLevel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("All", StringComparison.OrdinalIgnoreCase))
                return null;

            return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed)
                ? parsed
                : LogLevel.Information;
        }

        #endregion
    }
}
