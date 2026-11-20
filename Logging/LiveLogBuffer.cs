using lsMailServer.RestApi;

namespace lsMailServer.Logging 
{ 
    /// <summary>
    /// Thread-safe bounded ring buffer storing recent live log entries in memory with monotonic sequence IDs.
    /// </summary>
    public sealed class LiveLogBuffer : ILiveLogBuffer
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly Queue<LogEntryDto> _queue;
        private long _nextId;

        /// <summary>
        /// Initializes a new instance of the <see cref="LiveLogBuffer"/> class with the specified maximum entry capacity.
        /// </summary>
        /// <param name="capacity">Maximum number of retained log entries; clamped to a minimum of <c>10</c>.</param>
        public LiveLogBuffer(int capacity = 500)
        {
            _capacity = Math.Max(10, capacity);
            _queue = new Queue<LogEntryDto>(_capacity);
        }


        #region method Add

        /// <summary>
        /// Appends a new log entry with a monotonically increasing ID, evicting the oldest entry if at capacity.
        /// </summary>
        /// <param name="timestamp">The timestamp when the log event occurred.</param>
        /// <param name="level">The severity level of the log entry.</param>
        /// <param name="category">The logging category or subsystem source.</param>
        /// <param name="message">The formatted log message.</param>
        /// <param name="detail">Optional diagnostic or exception details.</param>
        public void Add(DateTimeOffset timestamp, LogLevel level, string category, string message, string? detail = null)
        {
            long id = Interlocked.Increment(ref _nextId);
            var entry = new LogEntryDto(id, timestamp, level.ToString(), category, message, detail);

            lock (_lock) {
                if (_queue.Count >= _capacity)
                    _queue.Dequeue();

                _queue.Enqueue(entry);
            }
        }

        #endregion

        #region method GetEntries

        /// <summary>
        /// Returns up to <paramref name="limit"/> matching entries with <see cref="LogEntryDto.Id"/> greater than <paramref name="afterId"/> in ascending order.
        /// </summary>
        /// <param name="afterId">Exclusive lower-bound sequence ID for delta polling.</param>
        /// <param name="minLevel">Optional minimum <see cref="LogLevel"/> filter.</param>
        /// <param name="category">Optional category filter; ignored when null, empty, or <c>"All"</c>.</param>
        /// <param name="search">Optional case-insensitive substring filter across message, category, and detail.</param>
        /// <param name="limit">Maximum number of matching entries to return.</param>
        /// <returns>A chronologically ordered list of matching <see cref="LogEntryDto"/> entries.</returns>
        public IReadOnlyList<LogEntryDto> GetEntries(long afterId, LogLevel? minLevel, string? category, string? search, int limit)
        {
            LogEntryDto[] snapshot;
            lock (_lock) {
                snapshot = _queue.ToArray();
            }

            var result = new List<LogEntryDto>(Math.Min(snapshot.Length, limit));
            for (int i = snapshot.Length - 1; i >= 0 && result.Count < limit; i--) {
                var item = snapshot[i];
                if (item.Id <= afterId)
                    break; // _queue is strictly ordered by ascending Id

                if (!Matches(item, minLevel, category, search))
                    continue;

                result.Add(item);
            }

            result.Reverse();

            return result;
        }

        #endregion

        #region method Clear

        /// <summary>
        /// Clears all buffered entries while keeping the sequence counter monotonic to avoid ID reuse across polls.
        /// </summary>
        public void Clear()
        {
            lock (_lock) {
                _queue.Clear();
                // Keep _nextId monotonic so in-flight client polls never collide
            }
        }

        #endregion

        #region method Matches

        /// <summary>
        /// Evaluates whether a buffered log entry satisfies the requested level, category, and search filters.
        /// </summary>
        /// <param name="item">The candidate log entry.</param>
        /// <param name="minLevel">Optional minimum severity level.</param>
        /// <param name="category">Optional source category filter.</param>
        /// <param name="search">Optional search substring.</param>
        /// <returns><see langword="true"/> if the entry matches all active filters; otherwise <see langword="false"/>.</returns>
        private static bool Matches(LogEntryDto item, LogLevel? minLevel, string? category, string? search)
        {
            if (minLevel.HasValue && Enum.TryParse<LogLevel>(item.Level, true, out var lvl) && lvl < minLevel.Value)
                return false;

            if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase) &&
                !item.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(search)) {
                bool hit = item.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || item.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (item.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
                if (!hit) return false;
            }

            return true;
        }

        #endregion
    }
}
