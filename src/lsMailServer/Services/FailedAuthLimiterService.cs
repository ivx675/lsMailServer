using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace lsMailServer.Services
{
    /// <summary>
    /// Thread-safe, in-memory brute-force protection manager that tracks failed authentication attempts
    /// by <c>(Client IP + Username)</c> and automatically purges expired entries on a periodic background timer.
    /// Keying by both IP and username prevents a single misconfigured client behind a shared NAT IP
    /// from locking out other legitimate users in the same LAN.
    /// </summary>
    public sealed class FailedAuthLimiterService : IDisposable
    {
        /// <summary>
        /// Represents a tracked failure counter entry in the dictionary.
        /// </summary>
        private sealed class FailureEntry
        {
            public int Count;
            public long LastFailureUtcTicks;
            public long BlockedUntilUtcTicks;
        }

        private readonly ConcurrentDictionary<string, FailureEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _cleanupTimer;
        private readonly ILogger<FailedAuthLimiterService>? _logger;

        private readonly int _maxAttempts;
        private readonly TimeSpan _windowDuration;
        private readonly TimeSpan _lockoutDuration;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="FailedAuthLimiterService"/> class.
        /// </summary>
        /// <param name="logger">Optional logger used to emit a warning when a client reaches the lockout threshold.</param>
        /// <param name="maxAttempts">
        /// Maximum consecutive failed attempts allowed for a single <c>(IP, Username)</c> pair before blocking. Defaults to <c>10</c>.
        /// </param>
        /// <param name="windowDuration">
        /// Sliding inactivity window after which a failure counter expires and is removed. Defaults to 15 minutes.
        /// </param>
        /// <param name="lockoutDuration">
        /// Duration for which a blocked <c>(IP, Username)</c> pair remains locked out after exceeding <paramref name="maxAttempts"/>. Defaults to 15 minutes.
        /// </param>
        /// <param name="cleanupInterval">
        /// Interval at which the background timer sweeps and removes expired dictionary entries. Defaults to 1 minute.
        /// </param>
        public FailedAuthLimiterService(
            ILogger<FailedAuthLimiterService>? logger = null,
            int maxAttempts = 10,
            TimeSpan? windowDuration = null,
            TimeSpan? lockoutDuration = null,
            TimeSpan? cleanupInterval = null)
        {
            if (maxAttempts < 1)
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Must be at least 1.");

            _logger = logger;
            _maxAttempts = maxAttempts;
            _windowDuration = windowDuration ?? TimeSpan.FromMinutes(15);
            _lockoutDuration = lockoutDuration ?? TimeSpan.FromMinutes(15);

            TimeSpan sweepPeriod = cleanupInterval ?? TimeSpan.FromMinutes(1);
            _cleanupTimer = new Timer(OnCleanupTimerTick, null, sweepPeriod, sweepPeriod);
        }

        #region method Dispose

        /// <summary>
        /// Stops the background cleanup timer and clears tracked entries.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cleanupTimer.Dispose();
            _entries.Clear();
        }

        #endregion


        #region method IsBlocked

        /// <summary>
        /// Checks whether the specified <paramref name="clientIp"/> and <paramref name="userName"/>
        /// pair is currently blocked due to excessive failed login attempts.
        /// </summary>
        /// <param name="clientIp">The remote client IP address.</param>
        /// <param name="userName">The username being authenticated.</param>
        /// <param name="remainingLockout">
        /// When this method returns <see langword="true"/>, contains the remaining lockout duration.
        /// </param>
        /// <returns><see langword="true"/> if authentication should be rejected immediately; otherwise <see langword="false"/>.</returns>
        public bool IsBlocked(IPAddress? clientIp, string? userName, out TimeSpan remainingLockout)
        {
            remainingLockout = TimeSpan.Zero;
            string? compositeKey = TryBuildCompositeKey(clientIp, userName);
            if (compositeKey == null)
                return false;

            if (!_entries.TryGetValue(compositeKey, out FailureEntry? entry))
                return false;

            long nowTicks = DateTime.UtcNow.Ticks;
            lock (entry) {
                if (entry.BlockedUntilUtcTicks > nowTicks) {
                    remainingLockout = TimeSpan.FromTicks(entry.BlockedUntilUtcTicks - nowTicks);
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region method RecordFailure

        /// <summary>
        /// Records a failed authentication attempt for <paramref name="clientIp"/> and <paramref name="userName"/>,
        /// incrementing the failure counter and activating a lockout if <see cref="_maxAttempts"/> is reached.
        /// </summary>
        /// <param name="clientIp">The remote client IP address.</param>
        /// <param name="userName">The attempted username.</param>
        /// <returns>
        /// <see langword="true"/> if this failure triggered or extended an active lockout; otherwise <see langword="false"/>.
        /// </returns>
        public bool RecordFailure(IPAddress? clientIp, string? userName)
        {
            string? compositeKey = TryBuildCompositeKey(clientIp, userName);
            if (compositeKey == null)
                return false;

            long nowTicks = DateTime.UtcNow.Ticks;
            long windowTicks = _windowDuration.Ticks;
            long lockoutTicks = _lockoutDuration.Ticks;

            FailureEntry entry = _entries.GetOrAdd(compositeKey, _ => new FailureEntry());
            bool newlyLockedOut = false;
            bool isBlocked;

            lock (entry) {
                // If the previous inactivity window expired and any lockout has elapsed, reset counter.
                if (entry.LastFailureUtcTicks > 0 &&
                    (nowTicks - entry.LastFailureUtcTicks) > windowTicks &&
                    entry.BlockedUntilUtcTicks <= nowTicks)
                {
                    entry.Count = 0;
                    entry.BlockedUntilUtcTicks = 0;
                }

                entry.Count++;
                entry.LastFailureUtcTicks = nowTicks;

                if (entry.Count >= _maxAttempts) {
                    if (entry.BlockedUntilUtcTicks <= nowTicks) {
                        newlyLockedOut = true;
                    }

                    long candidateBlockUntil = nowTicks + lockoutTicks;
                    if (candidateBlockUntil > entry.BlockedUntilUtcTicks) {
                        entry.BlockedUntilUtcTicks = candidateBlockUntil;
                    }
                    isBlocked = true;
                }
                else {
                    isBlocked = entry.BlockedUntilUtcTicks > nowTicks;
                }
            }

            if (newlyLockedOut) {
                IPAddress normalizedIp = clientIp!.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp;
                _logger?.LogWarning(
                    "Authentication rate limit reached for IP {ClientIp} and user '{UserName}' after {MaxAttempts} failed attempts. Locked out for {LockoutMinutes} minute(s).",
                    normalizedIp,
                    userName!.Trim(),
                    _maxAttempts,
                    _lockoutDuration.TotalMinutes);
            }

            return isBlocked;
        }

        #endregion

        #region method RecordSuccess

        /// <summary>
        /// Clears the failure counter for <paramref name="clientIp"/> and <paramref name="userName"/>
        /// upon a successful authentication so occasional typos do not accumulate indefinitely.
        /// </summary>
        /// <param name="clientIp">The remote client IP address.</param>
        /// <param name="userName">The authenticated username.</param>
        public void RecordSuccess(IPAddress? clientIp, string? userName)
        {
            string? compositeKey = TryBuildCompositeKey(clientIp, userName);
            if (compositeKey != null) {
                _entries.TryRemove(compositeKey, out _);
            }
        }

        #endregion

        #region method OnCleanupTimerTick

        /// <summary>
        /// Periodic timer callback that removes expired entries from the dictionary.
        /// </summary>
        private void OnCleanupTimerTick(object? state)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long windowTicks = _windowDuration.Ticks;

            foreach (var kvp in _entries) {
                FailureEntry entry = kvp.Value;
                bool isExpired;

                lock (entry) {
                    isExpired = entry.BlockedUntilUtcTicks <= nowTicks &&
                                (nowTicks - entry.LastFailureUtcTicks) > windowTicks;
                }

                if (isExpired) {
                    _entries.TryRemove(kvp.Key, out _);
                }
            }
        }

        #endregion

        #region method TryBuildCompositeKey

        /// <summary>
        /// Builds the normalized composite key <c>"{ip}|{username}"</c>, or returns <see langword="null"/>
        /// if either <paramref name="clientIp"/> or <paramref name="userName"/> is missing.
        /// </summary>
        private static string? TryBuildCompositeKey(IPAddress? clientIp, string? userName)
        {
            if (clientIp == null || string.IsNullOrWhiteSpace(userName))
                return null;

            IPAddress normalizedIp = clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp;
            string normalizedUser = userName.Trim().ToLowerInvariant();

            return $"{normalizedIp}|{normalizedUser}";
        }

        #endregion
        

        /// <summary>
        /// Gets the current number of tracked <c>(IP, Username)</c> entries in memory.
        /// </summary>
        public int TrackedEntryCount => _entries.Count;
    }
}