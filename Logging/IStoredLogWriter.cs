using lsMailServer.RestApi;

namespace lsMailServer.Logging 
{
    /// <summary>
    /// Writes persisted log entries (such as warnings and errors) to backing storage.
    /// </summary>
    public interface IStoredLogWriter
    {
        /// <summary>
        /// Persists a single structured log entry.
        /// </summary>
        /// <param name="entry">The log entry to write to storage.</param>
        void Write(LogEntryDto entry);
    }
}
