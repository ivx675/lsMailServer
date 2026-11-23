using LumiSoft.Net;
using LumiSoft.Net.DNS;
using LumiSoft.Net.DNS.Client;
using LumiSoft.Net.IO;
using LumiSoft.Net.Mail;
using LumiSoft.Net.MIME;
using LumiSoft.Net.SMTP;
using LumiSoft.Net.SMTP.Client;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static lsMailServer.RestApi.RelayQueueController;

namespace lsMailServer.Services 
{ 
    /// <summary>
    /// Manages high-performance asynchronous outbound mail relay and retry delivery queues, 
    /// governed by configurable intervals, global concurrency limits, active domain tracking with skip logic, 
    /// explicit recipient addressing, and Delivery Status Notification (DSN) tracking.
    /// </summary>
    public class SmtpRelayService : BackgroundService
    {
        #region enum QueueAction

        /// <summary>
        /// Specifies the file system action the queue loop should perform after processing a queued message.
        /// </summary>
        private enum QueueAction
        {
            /// <summary>
            /// Processing is complete (delivered, permanently failed, or expired); delete the message and metadata files.
            /// </summary>
            Delete,

            /// <summary>
            /// Delivery failed transiently during initial relay; move the message and updated metadata to the retry queue.
            /// </summary>
            MoveToRetry,

            /// <summary>
            /// Delivery failed transiently during retry; retain the message in the current queue with updated retry metadata.
            /// </summary>
            RequeueInPlace,

            /// <summary>
            /// Leave the message untouched for a future polling cycle (e.g., not yet due or concurrency limit reached).
            /// </summary>
            Skip
        }

        #endregion

        #region class QueueResult

        /// <summary>
        /// Represents the processing outcome of a queued message and the resulting <see cref="QueueAction"/> to apply.
        /// </summary>
        /// <param name="Action">The file system action to execute on the queued message.</param>
        private readonly record struct QueueResult(QueueAction Action)
        {
            /// <summary>
            /// Creates a result indicating that message processing is complete and its queue files should be deleted.
            /// </summary>
            /// <returns>A <see cref="QueueResult"/> configured with <see cref="QueueAction.Delete"/>.</returns>
            public static QueueResult Done() => new(QueueAction.Delete);

            /// <summary>
            /// Creates a result indicating that the message should be skipped until a subsequent polling cycle.
            /// </summary>
            /// <returns>A <see cref="QueueResult"/> configured with <see cref="QueueAction.Skip"/>.</returns>
            public static QueueResult Skip() => new(QueueAction.Skip);

            /// <summary>
            /// Creates a result indicating that the message should remain in its current queue folder with updated retry metadata.
            /// </summary>
            /// <returns>A <see cref="QueueResult"/> configured with <see cref="QueueAction.RequeueInPlace"/>.</returns>
            public static QueueResult RequeueInPlace() => new(QueueAction.RequeueInPlace);

            /// <summary>
            /// Creates a result indicating that the message and its updated metadata should be moved to the retry queue.
            /// </summary>
            /// <returns>A <see cref="QueueResult"/> configured with <see cref="QueueAction.MoveToRetry"/>.</returns>
            public static QueueResult MoveToRetry() => new(QueueAction.MoveToRetry);
        }

        #endregion

        #region class QueueMetadata

        /// <summary>
        /// Represents the serialized companion metadata (<c>.json</c>) for a queued outbound message,
        /// tracking envelope parameters, RFC 3461 DSN options, and retry scheduling state.
        /// </summary>
        private class QueueMetadata
        {
            /// <summary>
            /// Gets or sets a value indicating whether the message requires the <c>SMTPUTF8</c> extension (RFC 6531)
            /// for internationalized email addresses or headers.
            /// </summary>
            public bool SmtpUtf8 { get; set; }

            /// <summary>
            /// Gets or sets the SMTP reverse-path (<c>MAIL FROM</c>) address, or an empty string (<c>&lt;&gt;</c>) for bounce/DSN messages.
            /// </summary>
            public string EnvelopeSender { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the target recipient email address (<c>RCPT TO</c>) for this queue entry.
            /// </summary>
            public string RecipientAddress { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the destination domain extracted from <see cref="RecipientAddress"/>,
            /// used for MX resolution and per-domain connection throttling.
            /// </summary>
            public string RecipientDomain { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the original recipient address specified via the <c>ORCPT</c> parameter (RFC 3461), if provided.
            /// </summary>
            public string? OriginalRecipient { get; set; }

            /// <summary>
            /// Gets or sets the envelope identifier specified via the <c>ENVID</c> parameter (RFC 3461) for correlating DSN reports.
            /// </summary>
            public string? EnvId { get; set; }

            /// <summary>
            /// Gets or sets the requested content return option (<c>RET=FULL</c> or <c>RET=HDRS</c>) for generated DSN reports (RFC 3461).
            /// </summary>
            public SMTP_t_DSN_Ret ReturnOption { get; set; } = SMTP_t_DSN_Ret.NotSpecified;

            /// <summary>
            /// Gets or sets the DSN notification conditions (<c>NEVER</c>, <c>SUCCESS</c>, <c>FAILURE</c>, <c>DELAY</c>) requested by the sender (RFC 3461).
            /// </summary>
            public SMTP_t_DSN_Notify Notify { get; set; } = SMTP_t_DSN_Notify.NotSpecified;

            /// <summary>
            /// Gets or sets the total number of delivery attempts made for this message so far.
            /// </summary>
            public int AttemptCount { get; set; }

            /// <summary>
            /// Gets or sets the UTC timestamp when the message was first queued or attempted,
            /// used to evaluate delay notice and expiration (<c>_undeliveredAfter</c>) thresholds.
            /// </summary>
            public DateTime FirstAttemptTime { get; set; }

            /// <summary>
            /// Gets or sets the earliest UTC timestamp when the retry queue processor should attempt delivery again.
            /// </summary>
            public DateTime NextRetryTime { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether a transient delay warning DSN (<c>Action: delayed</c>)
            /// has already been dispatched for this message.
            /// </summary>
            public bool DelayedNoticeSent { get; set; }

            /// <summary>
            /// Gets or sets the diagnostic error text or SMTP reply from the most recent failed delivery attempt.
            /// </summary>
            public string? LastError { get; set; }
        }

        #endregion

        #region class RelayTarget

        /// <summary>
        /// Represents a destination SMTP endpoint (MX host or smart host) for outbound message delivery.
        /// </summary>
        private class RelayTarget
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="RelayTarget"/> class.
            /// </summary>
            /// <param name="hostName">The target mail exchanger (MX) or smart host FQDN or IP address.</param>
            /// <param name="port">The TCP port used for the SMTP connection (e.g., 25, 465, or 587).</param>
            public RelayTarget(string hostName, int port)
            {
                HostName = hostName;
                Port = port;
            }

            #region Properties Implementation

            /// <summary>
            /// Gets the target mail exchanger (MX) or smart host FQDN or IP address.
            /// </summary>
            public string HostName { get; }

            /// <summary>
            /// Gets the TCP port used for the SMTP connection.
            /// </summary>
            public int Port { get; }

            #endregion
        }

        #endregion

        #region enum TlsMode

        /// <summary>
        /// Specifies how Transport Layer Security (TLS) is negotiated for outbound SMTP connections.
        /// </summary>
        private enum TlsMode
        {
            /// <summary>
            /// Plaintext connection with no TLS encryption.
            /// </summary>
            None = 0,

            /// <summary>
            /// Explicit TLS upgraded via the SMTP <c>STARTTLS</c> command after an initial plaintext greeting (typically port 587 or 25).
            /// </summary>
            Explicit = 1,

            /// <summary>
            /// Implicit TLS negotiated immediately upon TCP connection before any SMTP banner is exchanged (typically port 465, RFC 8314).
            /// </summary>
            Implicit = 2
        }

        #endregion

        private readonly ILogger<SmtpRelayService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IDatabaseService _dbService;
        private readonly string _relayDir;
        private readonly string _retryDir;

        // Internal polling interval (non-configurable)
        private static readonly TimeSpan _relayInterval = TimeSpan.FromSeconds(10);
    
        // Loaded fields (with sensible defaults)
        private readonly string _hostName;
        private readonly int _maxOutboundConnections;
        private readonly int _maxConnectionsPerDomain;
        private readonly TimeSpan _maxSessionTimeout;
        private readonly bool _useTlsIfPossible;
        private readonly TimeSpan _retryInterval;
        private readonly TimeSpan _delayWarningAfter;
        private readonly TimeSpan _undeliveredAfter;
        private readonly bool _useSmartHost;
        private readonly string? _smartHostHost;
        private readonly int _smartHostPort;
        private readonly string? _smartHostUserName;
        private readonly string? _smartHostPassword;
        private readonly TlsMode _smartHostTlsMode;

        // Tracks currently active outbound deliveries per domain to implement skip-on-limit throttling
        private readonly ConcurrentDictionary<string, int> _activeDomainCounts = new(StringComparer.OrdinalIgnoreCase);

        // Thread-safe tracker to prevent duplicate processing of files currently in-flight across timer ticks
        private readonly ConcurrentDictionary<string, byte> _activeFiles = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, SMTP_Client> _activeSessions = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="SmtpRelayService"/> class, setting up 
        /// queue directories and binding configurable relay intervals, timeout thresholds, global concurrency, 
        /// and per-domain connection limits.
        /// </summary>
        /// <param name="logger">The logger instance for tracking outbound relay operations and delivery statuses.</param>
        /// <param name="configuration">The application configuration provider for reading relay settings.</param>
        /// <param name="dbService">The database service handling user metadata, quotas, and system-level database operations.</param>
        /// <param name="baseStoragePath">The root physical storage path on disk where queues reside.</param>
        public SmtpRelayService(
            ILogger<SmtpRelayService> logger, 
            IConfiguration configuration,
            IDatabaseService dbService,
            string baseStoragePath)
        {
            _logger = logger;
            _configuration = configuration;
            _dbService = dbService;

            _relayDir = Path.Combine(baseStoragePath, "_queues", "relay");
            _retryDir = Path.Combine(baseStoragePath, "_queues", "retry");

            Directory.CreateDirectory(_relayDir);
            Directory.CreateDirectory(_retryDir);

            IConfigurationSection section = _configuration.GetSection("SmtpRelay");

            string? configuredHostName = section.GetValue<string>("HostName");
            _hostName = !string.IsNullOrWhiteSpace(configuredHostName)
                ? configuredHostName.Trim()
                : Dns.GetHostName();

            _maxOutboundConnections = Math.Max(1, section.GetValue("MaxOutboundConnections", 20));
            _maxConnectionsPerDomain = Math.Max(1, section.GetValue("MaxConnectionsPerDomain", 2));
            _maxSessionTimeout = section.GetValue("MaxSessionTimeout", TimeSpan.FromMinutes(5));
            _useTlsIfPossible = section.GetValue("UseTlsIfPossible", true);
            _retryInterval = section.GetValue("RetryInterval", TimeSpan.FromMinutes(15));
            _delayWarningAfter = section.GetValue("DelayWarningAfter", TimeSpan.FromHours(4));
            _undeliveredAfter = section.GetValue("UndeliveredAfter", TimeSpan.FromDays(2));

            IConfigurationSection smartHost = section.GetSection("SmartHost");
            _useSmartHost = smartHost.GetValue("Enabled", false);
            _smartHostHost = smartHost.GetValue<string>("Host");
            _smartHostPort = smartHost.GetValue("Port", 587);
            _smartHostUserName = smartHost.GetValue<string>("UserName");
            _smartHostPassword = smartHost.GetValue<string>("Password");
            _smartHostTlsMode = smartHost.GetValue("TlsMode", TlsMode.Explicit);
        }


        #region override method ExecuteAsync

        /// <summary>
        /// Executes the background service, running concurrent timer loops for fresh relay items 
        /// and retrying deferred deliveries governed by global concurrency and per-domain skip logic.
        /// </summary>
        /// <param name="stoppingToken">A token that indicates when the background service is shutting down.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "OutboundRelayService started. Interval: {RelayInterval}s, RetryInterval: {RetryInterval}s, " +
                "MaxGlobalConnections: {MaxGlobal}, MaxPerDomainConnections: {MaxPerDomain}",
                _relayInterval.TotalSeconds, _retryInterval.TotalSeconds, _maxOutboundConnections, _maxConnectionsPerDomain);

            var relayTask = ProcessQueueLoopAsync(_relayDir, ProcessRelayItemAsync, _relayInterval, stoppingToken);
            var retryTask = ProcessQueueLoopAsync(_retryDir, ProcessRetryItemAsync, _retryInterval, stoppingToken);

            await Task.WhenAll(relayTask, retryTask);
        }

        #endregion

        #region method ProcessQueueLoopAsync

        /// <summary>
        /// Continuously polls the specified queue directory and dispatches due messages to the provided processor delegate.
        /// </summary>
        /// <param name="folderPath">The file system path of the queue directory containing message (<c>.eml</c>) and metadata files.</param>
        /// <param name="processor">
        /// The asynchronous delegate invoked for each queued message ready for delivery, accepting the message file path,
        /// associated <see cref="QueueMetadata"/>, and a <see cref="CancellationToken"/>, and returning a <see cref="QueueResult"/>.
        /// </param>
        /// <param name="interval">The delay between polling cycles when scanning the queue directory.</param>
        /// <param name="cancellationToken">A token monitored to gracefully stop the queue polling loop.</param>
        /// <returns>A <see cref="Task"/> representing the lifetime of the background polling loop.</returns>
        private async Task ProcessQueueLoopAsync(
            string folderPath, 
            Func<string, QueueMetadata, CancellationToken, Task<QueueResult>> processor, 
            TimeSpan interval, 
            CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(interval);
        
            while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken)) {
                try {
                    var emlFiles = Directory.GetFiles(folderPath, "*.eml");
                    foreach (var emlFile in emlFiles) {
                        if (cancellationToken.IsCancellationRequested) break;

                        // 1. Global connection cap: Stop launching tasks until active slots free up
                        if (_activeFiles.Count >= _maxOutboundConnections) {
                            break;
                        }

                        // 2. Deduplication guard: Skip if already being processed
                        if (!_activeFiles.TryAdd(emlFile, 0)) {
                            continue;
                        }

                        string metaPath = Path.ChangeExtension(emlFile, ".json");
                        if (!File.Exists(metaPath)) {
                            _logger.LogWarning("Missing metadata file for {File}, cleaning up orphaned .eml", emlFile);
                            File.Delete(emlFile);
                            _activeFiles.TryRemove(emlFile, out _);
                            continue;
                        }

                        QueueMetadata? metadata = null;
                        try {
                            metadata = JsonSerializer.Deserialize<QueueMetadata>(await File.ReadAllTextAsync(metaPath, cancellationToken));
                        }
                        catch {
                            _activeFiles.TryRemove(emlFile, out _);
                            continue;
                        }

                        if (metadata == null) {
                            _activeFiles.TryRemove(emlFile, out _);
                            continue;
                        }

                        // 3. Per-domain connection cap: Skip this message if target domain is saturated
                        int activeDomainCount = _activeDomainCounts.GetOrAdd(metadata.RecipientDomain, 0);
                        if (activeDomainCount >= _maxConnectionsPerDomain) {
                            _activeFiles.TryRemove(emlFile, out _);
                            continue;
                        }

                        _activeDomainCounts.AddOrUpdate(metadata.RecipientDomain, 1, (_, count) => count + 1);

                        _ = Task.Run(async () =>
                        {
                            try {
                                var result = await processor(emlFile, metadata, cancellationToken);
                                HandleQueueResult(emlFile, metaPath, result);
                            }
                            catch (Exception ex) {
                                _logger.LogError(ex, "Unhandled exception processing queue item: {File}", emlFile);
                            }
                            finally {
                                int updatedCount = _activeDomainCounts.AddOrUpdate(
                                    metadata.RecipientDomain, 
                                    0, 
                                    (_, count) => Math.Max(0, count - 1));

                                if (updatedCount == 0) {
                                    _activeDomainCounts.TryRemove(new KeyValuePair<string, int>(metadata.RecipientDomain, 0));
                                }

                                _activeFiles.TryRemove(emlFile, out _);
                            }
                        }, CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    break;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error scanning queue directory: {Directory}", folderPath);
                }
            }
        }

        #endregion

        #region method ProcessRelayItemAsync

        /// <summary>
        /// Processes a newly queued outbound relay item by attempting immediate SMTP delivery.
        /// </summary>
        /// <param name="emlFilePath">The full file system path to the queued <c>.eml</c> message file.</param>
        /// <param name="metadata">The deserialized companion metadata containing envelope, recipient, and DSN parameters.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A <see cref="QueueResult"/> indicating the disposition of the queue item:
        /// <list type="bullet">
        ///   <item>
        ///     <description>
        ///       <see cref="QueueResult.Done"/> when delivery succeeds, or when a permanent failure occurs (after dispatching a DSN if permitted) so the item is purged.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <description>
        ///       <see cref="QueueResult.MoveToRetry"/> when a temporary or transport failure occurs, after persisting <c>AttemptCount</c>, <c>LastError</c>, and <c>NextRetryTime</c> to disk.
        ///     </description>
        ///   </item>
        /// </list>
        /// </returns>
        /// <exception cref="IOException">Thrown if updating the companion <c>.json</c> metadata file fails prior to moving the item to the retry queue.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via <paramref name="cancellationToken"/>.</exception>
        private async Task<QueueResult> ProcessRelayItemAsync(string emlFilePath, QueueMetadata metadata, CancellationToken cancellationToken)
        {
            metadata.AttemptCount = 1;
            _logger.LogInformation("Delivering message {File} for recipient {Recipient} (Attempt 1)", 
                Path.GetFileName(emlFilePath), metadata.RecipientAddress);

            var (success, isPermanent, error) = await SendViaSmtpAsync(emlFilePath, metadata, cancellationToken).ConfigureAwait(false);
            if (success) {
                return QueueResult.Done();
            }

            metadata.LastError = error;

            if (isPermanent) {
                _logger.LogWarning("Permanent failure delivering {File} to {Recipient}: {Error}", 
                    Path.GetFileName(emlFilePath), metadata.RecipientAddress, error);

                await SendDsnIfAllowedAsync(emlFilePath, metadata, isDelayed: false, cancellationToken).ConfigureAwait(false);

                return QueueResult.Done();
            }

            // Temporary failure: persist AttemptCount, LastError, and NextRetryTime before moving to retry queue
            metadata.NextRetryTime = DateTime.UtcNow.Add(_retryInterval);

            string metaPath = Path.ChangeExtension(emlFilePath, ".json");
            await File.WriteAllTextAsync(
                metaPath, 
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), 
                cancellationToken).ConfigureAwait(false);

            return QueueResult.MoveToRetry();
        }

        #endregion

        #region method ProcessRetryItemAsync

        /// <summary>
        /// Processes a queued retry item by checking backoff windows, expiration thresholds, and delay notifications
        /// before attempting redelivery via SMTP.
        /// </summary>
        /// <param name="emlFilePath">The full file system path to the queued <c>.eml</c> message file in the retry directory.</param>
        /// <param name="metadata">The deserialized companion metadata containing attempt history, backoff schedule, and DSN parameters.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A <see cref="QueueResult"/> indicating the disposition of the retry item:
        /// <list type="bullet">
        ///   <item>
        ///     <description>
        ///       <see cref="QueueResult.Skip"/> when the scheduled <c>NextRetryTime</c> backoff window has not yet elapsed.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <description>
        ///       <see cref="QueueResult.Done"/> when delivery succeeds, the <c>_undeliveredAfter</c> threshold expires, or a permanent SMTP failure occurs (after dispatching a DSN if permitted) so the item is purged.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <description>
        ///       <see cref="QueueResult.RequeueInPlace"/> when a temporary or transport failure occurs, after persisting updated <c>AttemptCount</c>, <c>LastError</c>, <c>DelayedNoticeSent</c>, and exponential backoff <c>NextRetryTime</c> to disk.
        ///     </description>
        ///   </item>
        /// </list>
        /// </returns>
        /// <exception cref="IOException">Thrown if updating the companion <c>.json</c> metadata file fails prior to requeuing.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via <paramref name="cancellationToken"/>.</exception>
        private async Task<QueueResult> ProcessRetryItemAsync(string emlFilePath, QueueMetadata metadata, CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;

            // 1. Skip if backoff window has not elapsed yet
            if (now < metadata.NextRetryTime) {
                return QueueResult.Skip();
            }

            // 2. Check if message has exceeded the permanent undelivered threshold (final NDR trigger)
            if (now - metadata.FirstAttemptTime > _undeliveredAfter) {
                _logger.LogWarning("Message {File} exceeded UndeliveredAfter threshold ({Hours}h). Dropping message.", 
                    Path.GetFileName(emlFilePath), _undeliveredAfter.TotalHours);

                metadata.LastError ??= $"451 4.4.7 Message expired after {_undeliveredAfter.TotalHours:0.#} hours in queue";
                await SendDsnIfAllowedAsync(emlFilePath, metadata, isDelayed: false, cancellationToken).ConfigureAwait(false);

                return QueueResult.Done();
            }

            // 3. Trigger one-time delayed delivery notification if threshold is exceeded
            if (!metadata.DelayedNoticeSent && (now - metadata.FirstAttemptTime > _delayWarningAfter)) {
                _logger.LogInformation("Message {File} delivery delayed past {Hours}h threshold. Evaluating delay DSN.", 
                    Path.GetFileName(emlFilePath), _delayWarningAfter.TotalHours);

                await SendDsnIfAllowedAsync(emlFilePath, metadata, isDelayed: true, cancellationToken).ConfigureAwait(false);
                metadata.DelayedNoticeSent = true;
            }

            // 4. Attempt redelivery
            metadata.AttemptCount++;
            _logger.LogInformation("Retrying message {File} for recipient {Recipient} (Attempt {Attempt})", 
                Path.GetFileName(emlFilePath), metadata.RecipientAddress, metadata.AttemptCount);

            var (success, isPermanent, error) = await SendViaSmtpAsync(emlFilePath, metadata, cancellationToken).ConfigureAwait(false);
            if (success) {
                return QueueResult.Done();
            }

            metadata.LastError = error;

            // 5. Drop immediately on permanent 5xx error
            if (isPermanent) {
                _logger.LogWarning("Permanent failure delivering {File} to {Recipient}: {Error}", 
                    Path.GetFileName(emlFilePath), metadata.RecipientAddress, error);

                await SendDsnIfAllowedAsync(emlFilePath, metadata, isDelayed: false, cancellationToken).ConfigureAwait(false);

                return QueueResult.Done();
            }

            // 6. Temporary failure: calculate exponential backoff and persist updated metadata
            var backoffDelay = TimeSpan.FromSeconds(_retryInterval.TotalSeconds * Math.Pow(2, metadata.AttemptCount - 1));
            metadata.NextRetryTime = DateTime.UtcNow.Add(backoffDelay);

            string metaPath = Path.ChangeExtension(emlFilePath, ".json");
            await File.WriteAllTextAsync(
                metaPath, 
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), 
                cancellationToken).ConfigureAwait(false);

            return QueueResult.RequeueInPlace();
        }

        #endregion

        #region method SendViaSmtpAsync

        /// <summary>
        /// Resolves target mail servers (either a configured Smart Host or direct MX/A records with lazy per-host resolution)
        /// and attempts outbound SMTP delivery within a single session timeout, enforcing TLS mode, SMTP authentication,
        /// ESMTP capability checks (<c>DSN</c>, <c>SMTPUTF8</c>), and permanent vs. temporary failure classification.
        /// </summary>
        /// <param name="emlFilePath">The full file system path to the queued <c>.eml</c> message file.</param>
        /// <param name="metadata">The companion metadata containing envelope parameters, recipient details, and DSN options.</param>
        /// <param name="cancellationToken">A token to monitor for service shutdown or cancellation requests.</param>
        /// <returns>
        /// A tuple indicating whether delivery succeeded (<c>Success</c>), whether a failure is permanent
        /// and should trigger an immediate bounce (<c>IsPermanent</c>), and the raw SMTP reply or transport error string (<c>Error</c>).
        /// </returns>
        private async Task<(bool Success, bool IsPermanent, string? Error)> SendViaSmtpAsync(
            string emlFilePath,
            QueueMetadata metadata,
            CancellationToken cancellationToken)
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionCts.CancelAfter(_maxSessionTimeout);
            CancellationToken token = sessionCts.Token;

            // 1. Determine target host(s): Smart Host mode vs. Direct DNS-MX mode
            var targets = new List<RelayTarget>();
            bool isDirectDomainFallback = false;
            bool useSmartHost = _useSmartHost && !string.IsNullOrWhiteSpace(_smartHostHost);

            if (useSmartHost) {
                targets.Add(new RelayTarget(_smartHostHost!, _smartHostPort));
            }
            else {
                try {
                    var response = await DNS_Client.Static.QueryAsync(metadata.RecipientDomain, DNS_RecordType.MX, 2000).ConfigureAwait(false);
                    if (response == null) {
                        return (Success: false, IsPermanent: false, Error: $"DNS query timed out for {metadata.RecipientDomain}");
                    }

                    if (response.ResponseCode == DNS_ResponseCode.NameError) {
                        // Authoritative NXDOMAIN -> stop relay immediately and generate permanent DSN
                        return (Success: false, IsPermanent: true, Error: $"550 5.1.2 Host or domain name not found: {metadata.RecipientDomain} (NXDOMAIN)");
                    }

                    if (response.ResponseCode != DNS_ResponseCode.NoError) {
                        // SERVFAIL, Refused, etc. -> transient DNS failure, retry later
                        return (Success: false, IsPermanent: false, Error: $"DNS MX lookup failed for {metadata.RecipientDomain} ({response.ResponseCode})");
                    }

                    var mxRecords = response.Answers.MX.OrderBy(mx => mx.Preference).ToList();

                    // RFC 7505: Single Null MX ("0 .") explicitly signals the domain accepts no email
                    if (mxRecords.Count == 1 && mxRecords[0].Preference == 0 && string.IsNullOrWhiteSpace(mxRecords[0].Host?.TrimEnd('.'))) {
                        return (Success: false, IsPermanent: true, Error: $"550 5.1.0 Domain {metadata.RecipientDomain} does not accept mail (Null MX)");
                    }

                    foreach (var mx in mxRecords) {
                        string? host = mx.Host?.TrimEnd('.');
                        if (!string.IsNullOrEmpty(host)) {
                            targets.Add(new RelayTarget(host, 25));
                        }
                    }

                    // RFC 5321 Section 5.1: Only when MX query returned NOERROR with 0 MX records, fall back to domain A/AAAA
                    if (targets.Count == 0) {
                        targets.Add(new RelayTarget(metadata.RecipientDomain, 25));
                        isDirectDomainFallback = true;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    throw;
                }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "DNS MX query failed for domain {Domain}", metadata.RecipientDomain);
                    return (Success: false, IsPermanent: false, Error: $"DNS MX query failed for {metadata.RecipientDomain}: {ex.Message}");
                }
            }

            // 2. Lazily resolve A/AAAA and connect in priority order
            using var smtpClient = new SMTP_Client();
            string? lastConnectError = null;            

            string sessionId = Guid.NewGuid().ToString("N");
            smtpClient.Tags["ID"] = sessionId;
            _activeSessions[sessionId] = smtpClient;
            smtpClient.Tags["StatusText"] = "Executing CONNECT";

            try {
                foreach (var target in targets) {
                    IPAddress[]? ips;
                    try {
                        ips = await DNS_Client.Static.GetHostAddressesAsync(target.HostName).ConfigureAwait(false);
                    }
                    catch (Exception ex) {
                        lastConnectError = $"{target.HostName}: DNS resolution failed ({ex.Message})";
                        _logger.LogWarning("Failed to resolve host '{Host}': {Message}", target.HostName, ex.Message);
                        continue;
                    }

                    if (ips == null || ips.Length == 0) {
                        if (!useSmartHost && isDirectDomainFallback) {
                            return (Success: false, IsPermanent: true, Error: $"550 5.4.4 No MX or A/AAAA records found for {metadata.RecipientDomain}");
                        }

                        lastConnectError = $"{target.HostName}: No A/AAAA records found";
                        _logger.LogWarning("No A/AAAA records found for host '{Host}'", target.HostName);
                        continue;
                    }

                    try {
                        bool useImplicitSsl = useSmartHost && _smartHostTlsMode == TlsMode.Implicit;

                        // Pass target.HostName as local/remote host parameter if required for TLS SNI certificate validation
                        await smtpClient.ConnectAsync(null, ips, target.Port, useImplicitSsl, null, token).ConfigureAwait(false);
                        if (smtpClient.IsConnected) {
                            break;
                        }
                    }
                    catch (OperationCanceledException) {
                        throw;
                    }
                    catch (Exception ex) {
                        lastConnectError = $"{target.HostName}: {ex.Message}";
                        _logger.LogWarning("Failed to connect to host '{Host}': {Message}", target.HostName, ex.Message);
                    }
                }

                if (!smtpClient.IsConnected) {
                    return (Success: false, IsPermanent: false, Error: lastConnectError ?? $"Unable to connect to any mail server for {metadata.RecipientDomain}");
                }
                
                // 3. ESMTP handshake, STARTTLS upgrade, and authentication
                string localHostName = Dns.GetHostName();
                smtpClient.Tags["StatusText"] = "Executing EHLO";
                await smtpClient.EhloHeloAsync(localHostName, token).ConfigureAwait(false);

                if (!smtpClient.IsSecureConnection) {
                    bool serverSupportsStartTls = smtpClient.EsmtpFeatures.Contains("STARTTLS", StringComparer.OrdinalIgnoreCase);

                    if (useSmartHost && _smartHostTlsMode == TlsMode.Explicit) {
                        if (!serverSupportsStartTls) {
                            return (Success: false, IsPermanent: false, Error: $"Smart host '{_smartHostHost}' does not support required STARTTLS");
                        }

                        smtpClient.Tags["StatusText"] = "Executing STARTTLS";
                        await smtpClient.StartTlsAsync(null, token).ConfigureAwait(false);
                        // RFC 3207 Section 4.2: Client MUST re-issue EHLO after STARTTLS completes
                        smtpClient.Tags["StatusText"] = "Executing EHLO";
                        await smtpClient.EhloHeloAsync(localHostName, token).ConfigureAwait(false);
                    }
                    else if (!useSmartHost && _useTlsIfPossible && serverSupportsStartTls) {
                        try {
                            smtpClient.Tags["StatusText"] = "Executing STARTTLS";
                            await smtpClient.StartTlsAsync(null, token).ConfigureAwait(false);
                            smtpClient.Tags["StatusText"] = "Executing EHLO";
                            await smtpClient.EhloHeloAsync(localHostName, token).ConfigureAwait(false);
                        }
                        catch (Exception tlsEx)  {
                            // Opportunistic TLS failed; log and allow retry (or reconnect without TLS if desired)
                            _logger.LogWarning(tlsEx, "Opportunistic STARTTLS failed for {Domain}: {Message}", metadata.RecipientDomain, tlsEx.Message);
                            throw;
                        }
                    }
                }

                // Authenticate against Smart Host if credentials are configured
                if (useSmartHost && !string.IsNullOrEmpty(_smartHostUserName)) {
                    var authMethod = smtpClient.AuthGetStrongestMethod(_smartHostUserName, _smartHostPassword ?? string.Empty);
                    smtpClient.Tags["StatusText"] = "Executing AUTH";
                    await smtpClient.AuthAsync(authMethod, token).ConfigureAwait(false);
                }

                // 4. Capability checks (evaluated AFTER TLS & AUTH so final EsmtpFeatures are inspected)
                // RFC 6531 Section 3.7.4: SMTPUTF8 required by message but unsupported by remote MTA -> permanent 5.6.7 bounce
                if (metadata.SmtpUtf8 && !smtpClient.EsmtpFeatures.Contains("SMTPUTF8", StringComparer.OrdinalIgnoreCase)) {
                    string utf8Error = "550 5.6.7 SMTPUTF8 is required for this message, but the remote server does not support it";
                    _logger.LogWarning("Permanent SMTPUTF8 capability mismatch delivering {File} to {Recipient}",
                        Path.GetFileName(emlFilePath), metadata.RecipientAddress);

                    return (Success: false, IsPermanent: true, Error: utf8Error);
                }

                // RFC 3461: Only send DSN parameters on the wire if the remote server advertises DSN
                bool isDsnSupported = smtpClient.EsmtpFeatures.Contains("DSN", StringComparer.OrdinalIgnoreCase);

                SMTP_t_DSN_Ret wireRet = isDsnSupported ? metadata.ReturnOption : SMTP_t_DSN_Ret.NotSpecified;
                string? wireEnvId = isDsnSupported ? metadata.EnvId : null;
                SMTP_t_DSN_Notify wireNotify = isDsnSupported ? metadata.Notify : SMTP_t_DSN_Notify.NotSpecified;
                string? wireOrcpt = isDsnSupported ? metadata.OriginalRecipient : null;

                // 5. Envelope and message payload transmission
                await using var stream = File.OpenRead(emlFilePath);
                smtpClient.Tags["StatusText"] = "Executing MAIL";
                await smtpClient.MailFromAsync(
                    metadata.EnvelopeSender,
                    stream.Length,
                    wireRet,
                    wireEnvId,
                    smtpUtf8: metadata.SmtpUtf8,
                    cancellationToken: token).ConfigureAwait(false);

                smtpClient.Tags["StatusText"] = "Executing RCPT";
                await smtpClient.RcptToAsync(
                    metadata.RecipientAddress,
                    wireNotify,
                    wireOrcpt,
                    token).ConfigureAwait(false);

                smtpClient.Tags["StatusText"] = "Executing DATA/BDAT";
                await smtpClient.SendMessageAsync(stream, true, token).ConfigureAwait(false);

                return (Success: true, IsPermanent: false, Error: null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw; // Host application or service is shutting down
            }
            catch (OperationCanceledException) {
                string timeoutError = $"SMTP session timed out after {_maxSessionTimeout.TotalMinutes:0.#} minutes";
                _logger.LogWarning("{Error} delivering {File} to {Recipient}",
                    timeoutError, Path.GetFileName(emlFilePath), metadata.RecipientAddress);

                return (Success: false, IsPermanent: false, Error: timeoutError);
            }
            catch (SMTP_ClientException smtpEx) {
                bool isPermanent = smtpEx.ServerResponse?.IsPermanentError ?? false;
                string errorText = smtpEx.ServerResponse?.ToString().Trim() ?? smtpEx.Message;

                _logger.LogWarning("SMTP protocol error delivering {File} to {Recipient} (Permanent={IsPermanent}): {Error}",
                    Path.GetFileName(emlFilePath), metadata.RecipientAddress, isPermanent, errorText);

                return (Success: false, IsPermanent: isPermanent, Error: errorText);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Transport error delivering {File} to {Recipient}: {Message}",
                    Path.GetFileName(emlFilePath), metadata.RecipientAddress, ex.Message);

                return (Success: false, IsPermanent: false, Error: ex.Message);
            }
            finally {
                _activeSessions.TryRemove(sessionId, out _);
            }
        }

        #endregion

        #region method HandleQueueResult

        /// <summary>
        /// Executes the post-processing file system disposition specified by <paramref name="result"/> for a queued message and its metadata file.
        /// </summary>
        /// <param name="emlFilePath">The full file system path to the queued <c>.eml</c> message file.</param>
        /// <param name="metaPath">The full file system path to the companion <c>.json</c> metadata file.</param>
        /// <param name="result">The <see cref="QueueResult"/> indicating whether to delete, move to retry, requeue in place, or skip the item.</param>
        private void HandleQueueResult(string emlFilePath, string metaPath, QueueResult result)
        {
            if (result.Action == QueueAction.Delete) {
                if (File.Exists(emlFilePath)) File.Delete(emlFilePath);
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            else if (result.Action == QueueAction.MoveToRetry) {
                string destEml = Path.Combine(_retryDir, Path.GetFileName(emlFilePath));
                string destMeta = Path.Combine(_retryDir, Path.GetFileName(metaPath));

                if (File.Exists(emlFilePath)) File.Move(emlFilePath, destEml, true);
                if (File.Exists(metaPath)) File.Move(metaPath, destMeta, true);
            }
        }

        #endregion

        #region method SendDsnIfAllowedAsync

        /// <summary>
        /// Generates and queues a Delivery Status Notification (DSN) to the original envelope sender if permitted by RFC 3461
        /// (<c>NOTIFY</c> parameters) and the message is not itself a null-sender (<c>&lt;&gt;</c>) bounce message.
        /// </summary>
        /// <param name="emlFilePath">The full file system path to the original queued <c>.eml</c> message file, used to extract headers or body content for the DSN report.</param>
        /// <param name="metadata">The deserialized companion metadata containing the envelope sender, recipient DSN options, attempt history, and diagnostic error details.</param>
        /// <param name="isDelayed">
        /// <see langword="true"/> to generate a transient delay warning DSN (<c>Action: delayed</c>);
        /// <see langword="false"/> to generate a permanent non-delivery bounce DSN (<c>Action: failed</c>).
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous DSN evaluation and queuing operation.</returns>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via <paramref name="cancellationToken"/>.</exception>
        private async Task SendDsnIfAllowedAsync(
            string emlFilePath, 
            QueueMetadata metadata, 
            bool isDelayed, 
            CancellationToken cancellationToken)
        {
            // RFC 3461 / RFC 5321: Never generate a DSN for a null reverse-path (<>)
            if (string.IsNullOrWhiteSpace(metadata.EnvelopeSender)) {
                return;
            }

            SMTP_t_DSN_Notify effectiveNotify = metadata.Notify == SMTP_t_DSN_Notify.NotSpecified
                ? SMTP_t_DSN_Notify.Failure | SMTP_t_DSN_Notify.Delay
                : metadata.Notify;

            if (effectiveNotify.HasFlag(SMTP_t_DSN_Notify.Never)) {
                return;
            }

            bool shouldSend = isDelayed
                ? effectiveNotify.HasFlag(SMTP_t_DSN_Notify.Delay)
                : effectiveNotify.HasFlag(SMTP_t_DSN_Notify.Failure);

            if (!shouldSend) {
                return;
            }

            SMTP_t_DSN_Ret effectiveRet = metadata.ReturnOption == SMTP_t_DSN_Ret.NotSpecified
                ? SMTP_t_DSN_Ret.FullMessage
                : metadata.ReturnOption;

            string action = isDelayed ? "delayed" : "failed";
            string diagnostic = string.IsNullOrWhiteSpace(metadata.LastError)
                ? (isDelayed ? "Delivery temporarily delayed" : "Delivery failed")
                : metadata.LastError.Trim();
            
            // Detect whether LastError came from an SMTP server reply (e.g. "550 5.1.1 ...") vs a transport/network error (e.g. "Unable to connect to host")
            bool isSmtpReply = diagnostic.Length >= 3 &&
                               char.IsDigit(diagnostic[0]) &&
                               char.IsDigit(diagnostic[1]) &&
                               char.IsDigit(diagnostic[2]);
                                    
            string defaultStatus = isDelayed ? "4.4.1" : (isSmtpReply ? "5.0.0" : "5.4.1");
            string statusCode = isSmtpReply
                ? diagnostic
                    .Split([' ', '-', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(t =>
                        t.Split('.') is [var c, var s, var d] &&
                        c is "2" or "4" or "5" &&
                        int.TryParse(s, out _) &&
                        int.TryParse(d, out _))
                  ?? defaultStatus
                : defaultStatus;

            string diagnosticType = isSmtpReply ? "smtp" : "X-Transport";
            string localHost = _hostName;
            string subject = isDelayed
                ? $"Delivery Status Notification (Delay): {metadata.RecipientAddress}"
                : $"Undelivered Mail Returned to Sender: {metadata.RecipientAddress}";

            using Mail_Message dsnMessage = new Mail_Message();
            dsnMessage.From = new Mail_t_MailboxList();
            dsnMessage.From.Add(new Mail_t_Mailbox("Mail Delivery System",$"MAILER-DAEMON@{localHost}"));
            dsnMessage.To = new Mail_t_AddressList();
            dsnMessage.To.Add(new Mail_t_Mailbox(null,$"{metadata.EnvelopeSender}"));
            dsnMessage.Subject = subject;
            dsnMessage.Date = DateTime.Now;
            dsnMessage.MimeVersion = "1.0";
            dsnMessage.MessageID = MIME_Utils.CreateMessageID();
            dsnMessage.Header.Add("Auto-Submitted: auto-replied");

            var contentType = new MIME_h_ContentType("multipart/report");
            contentType.Parameters["report-type"] = "delivery-status";
            contentType.Param_Boundary = $"----=_DSN_{Guid.NewGuid():N}";
            var bodyMultipartReport = new MIME_b_MultipartReport(contentType); 

            // --- Part 1: Human-Readable Explanation ---
            if (isDelayed) {
                bodyMultipartReport.BodyParts.Add(MIME_Entity.CreateEntity_Text_Plain("7bit",
                    "This is an automatically generated Delivery Status Notification." +
                    "\r\n" +
                    $"Delivery to the following recipient has been delayed: {metadata.RecipientAddress}" +
                    "The server will continue trying to deliver this message." +
                    "\r\n" +
                    $"Reason: {diagnostic}"
                    ));
            }
            else {
                bodyMultipartReport.BodyParts.Add(MIME_Entity.CreateEntity_Text_Plain("7bit",
                    $"This is the mail system at host {localHost}." +
                    "\r\n" +
                    "I am sorry to have to inform you that your message could not " +
                    $"be delivered to {metadata.RecipientAddress}." +
                    "\r\n" +
                    $"Reason: {diagnostic}"
                    ));
            }
            dsnMessage.Body = bodyMultipartReport;

            // --- Part 2: message/delivery-status ---
            MIME_Entity entityMessageDeliveryStatus = new MIME_Entity();
            MIME_b_MessageDeliveryStatus bodyMessageDeliveryStatus = new MIME_b_MessageDeliveryStatus();
            // Per message reporting block
            if (!string.IsNullOrWhiteSpace(metadata.EnvId)) {
                bodyMessageDeliveryStatus.MessageFields.Add($"Original-Envelope-Id: {metadata.EnvId}");
            }
            bodyMessageDeliveryStatus.MessageFields.Add($"Reporting-MTA: dns; {localHost}");
            var arrivalDate = new DateTimeOffset(DateTime.SpecifyKind(metadata.FirstAttemptTime, DateTimeKind.Utc));
            bodyMessageDeliveryStatus.MessageFields.Add($"Arrival-Date: {arrivalDate.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture)}");
            // Per recipient reporting block
            var recipientBlock = new MIME_h_Collection(new MIME_h_Provider());
            if (!string.IsNullOrWhiteSpace(metadata.OriginalRecipient)) {
                recipientBlock.Add($"Original-Recipient: {metadata.OriginalRecipient}");
            }
            recipientBlock.Add($"Final-Recipient: rfc822; {metadata.RecipientAddress}");
            recipientBlock.Add($"Action: {action}");
            recipientBlock.Add($"Status: {statusCode}");
            recipientBlock.Add($"Diagnostic-Code: {diagnosticType}; {diagnostic}");
            recipientBlock.Add($"Last-Attempt-Date: {DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture)}");
            bodyMessageDeliveryStatus.RecipientBlocks.Add(recipientBlock);
            entityMessageDeliveryStatus.Body = bodyMessageDeliveryStatus;
            bodyMultipartReport.BodyParts.Add(entityMessageDeliveryStatus);

            // --- Part 3: Returned Original Message or Headers (RFC 3461 RET) ---
            using Mail_Message relayMessage = await Mail_Message.ParseFromFileAsync(emlFilePath,cancellationToken);
            if (effectiveRet == SMTP_t_DSN_Ret.Headers) {
                MIME_Entity entityTextRfc822Headers = new MIME_Entity();
                MIME_b_Text bodyTextRfc822Headers = new MIME_b_Text("text/rfc822-headers");
                entityTextRfc822Headers.Body = bodyTextRfc822Headers;
                bodyTextRfc822Headers.SetText("8bit",relayMessage.Header.ToString());
                bodyMultipartReport.BodyParts.Add(entityTextRfc822Headers);
            }
            else {
                MIME_Entity entityMessageRfc822 = new MIME_Entity();
                MIME_b_MessageRfc822 bodyMessageRfc822 = new MIME_b_MessageRfc822();
                entityMessageRfc822.Body = bodyMessageRfc822;
                bodyMessageRfc822.Message = relayMessage;
                bodyMultipartReport.BodyParts.Add(entityMessageRfc822);
            }

            using var tmpStream = new MemoryStreamEx();
            await dsnMessage.ToStreamAsync(tmpStream,cancellationToken: cancellationToken);

            // Queue the DSN with MAIL FROM:<> and NOTIFY=NEVER to prevent bounce loops
            tmpStream.Position = 0;
            await StoreMessageAsync(
                mailFrom: string.Empty,
                rcptTo: metadata.EnvelopeSender,
                smtpUtf8: metadata.SmtpUtf8,
                stream: tmpStream,
                envId: null,
                originalRecipient: null,
                dsnRet: SMTP_t_DSN_Ret.Headers,
                dsnNotify: SMTP_t_DSN_Notify.Never,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region method StoreMessageAsync

        /// <summary>
        /// Persists an outbound MIME message stream and its companion RFC 3461 DSN metadata to the relay queue directory.
        /// </summary>
        /// <param name="mailFrom">The SMTP envelope sender address (<c>MAIL FROM</c>).</param>
        /// <param name="rcptTo">The target recipient address (<c>RCPT TO</c>).</param>
        /// <param name="smtpUtf8">Indicates whether the message requires <c>SMTPUTF8</c> (RFC 6531) extension support during relay.</param>
        /// <param name="stream">The readable stream containing the raw MIME message payload.</param>
        /// <param name="envId">The optional RFC 3461 envelope identifier (<c>ENVID</c>) used to correlate Delivery Status Notifications.</param>
        /// <param name="originalRecipient">The optional RFC 3461 original recipient address (<c>ORCPT</c>) prior to any aliasing or forwarding.</param>
        /// <param name="dsnRet">
        /// Specifies whether the full message or only headers should be returned in a failed Delivery Status Notification (<c>RET</c>).
        /// Preserves <see cref="SMTP_t_DSN_Ret.NotSpecified"/> when omitted by the client so relay behavior stays RFC-compliant.
        /// </param>
        /// <param name="dsnNotify">
        /// Bitwise flags specifying the conditions under which a Delivery Status Notification should be generated (<c>NOTIFY</c>).
        /// Preserves <see cref="SMTP_t_DSN_Notify.NotSpecified"/> when omitted by the client.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The full file system path of the queued <c>.eml</c> message file.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
        /// <exception cref="IOException">Thrown if writing the <c>.eml</c> payload or <c>.json</c> metadata file to disk fails.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via <paramref name="cancellationToken"/>.</exception>
        public async Task<string> StoreMessageAsync(
            string mailFrom, 
            string rcptTo, 
            bool smtpUtf8,
            Stream stream,
            string? envId,
            string? originalRecipient,
            SMTP_t_DSN_Ret dsnRet, 
            SMTP_t_DSN_Notify dsnNotify,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);

            string uniqueId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Random.Shared.Next():x8}";
            string emlPath = Path.Combine(_relayDir, $"{uniqueId}.eml");
            string metaPath = Path.Combine(_relayDir, $"{uniqueId}.json");            

            // 1. Sign message with DKIM, if enabled
            byte[]? dkimHeader = null;
            var domain = await _dbService.GetDomainAsync(new MailAddress(mailFrom).Host);        
            if(domain != null && domain.IsDkimEnabled) {
                if(string.IsNullOrEmpty(domain.DkimPrivateKey)) {
                    _logger.LogWarning("DKIM signing is enabled, but private key is missing.");
                }
                else if(string.IsNullOrEmpty(domain.DkimSelector)) {
                    _logger.LogWarning("DKIM selector value is not specified.");
                } 
                else {
                    stream.Position = 0;
                    string dkimSignature = await DkimService.CreateSignatureHeaderAsync(
                        stream,
                        domain.Name,
                        domain.DkimSelector,
                        domain.DkimPrivateKey,
                        null,
                        cancellationToken);

                    dkimHeader = Encoding.ASCII.GetBytes($"DKIM-Signature: {dkimSignature}\r\n");
                }
            }

            // 2. Stream raw MIME message to disk
            using (var fileStream = new FileStream(emlPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true)) {
                stream.Position = 0;
                if(dkimHeader != null) {
                    await fileStream.WriteAsync(dkimHeader);
                }
                await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            
            // 3. Extract recipient domain directly from the passed rcptTo address
            string recipientDomain = "unknown.local";
            try {
                var parsedMail = new MailAddress(rcptTo);
                recipientDomain = parsedMail.Host.ToLowerInvariant();
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to parse domain from recipient address '{Recipient}', defaulting to 'unknown.local'", rcptTo);
            }

            // 4. Write companion metadata file
            var metadata = new QueueMetadata
            {
                SmtpUtf8 = smtpUtf8,
                EnvelopeSender = mailFrom,
                RecipientAddress = rcptTo,
                RecipientDomain = recipientDomain,
                OriginalRecipient = originalRecipient,
                EnvId = envId,
                ReturnOption = dsnRet,
                Notify = dsnNotify,
                AttemptCount = 0,
                FirstAttemptTime = DateTime.UtcNow,
                NextRetryTime = DateTime.UtcNow,
                DelayedNoticeSent = false
            };

            await File.WriteAllTextAsync(
                metaPath, 
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), 
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Queued outbound message from {Sender} to recipient {Recipient} (Domain: {Domain}) at {Path}", 
                mailFrom, rcptTo, recipientDomain, emlPath);
        
            return emlPath;
        }

        #endregion

        #region method GetQueuedItems

        /// <summary>
        /// Retrieves a snapshot of all outbound messages currently waiting in the retry queue directory,
        /// ordered chronologically by their first delivery attempt time.
        /// </summary>
        /// <returns>
        /// A read-only list of <see cref="RelayQueueItemDto"/> instances representing the deferred messages.
        /// Returns an empty list if the retry directory does not exist or contains no valid queue metadata files.
        /// </returns>
        public IReadOnlyList<RelayQueueItemDto> GetQueuedItems()
        {
            if (!Directory.Exists(_retryDir))
                return Array.Empty<RelayQueueItemDto>();

            /*
            var jsonSerializerOptions  = new JsonSerializerOptions() {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };*/

            var items = new List<RelayQueueItemDto>();
            foreach (string metaPath in Directory.EnumerateFiles(_retryDir, "*.json")) {
                try {
                    string json = File.ReadAllText(metaPath);
                    var meta = JsonSerializer.Deserialize<QueueMetadata>(json);
                    if (meta is null)
                        continue;

                    string id = Path.GetFileNameWithoutExtension(metaPath);
                    string emlPath = Path.ChangeExtension(metaPath, ".eml");
                    long? sizeBytes = File.Exists(emlPath) ? new FileInfo(emlPath).Length : null;

                    items.Add(new RelayQueueItemDto(
                        Id: id,
                        EnvelopeSender: meta.EnvelopeSender,
                        RecipientAddress: meta.RecipientAddress,
                        RecipientDomain: meta.RecipientDomain,
                        AttemptCount: meta.AttemptCount,
                        FirstAttemptTime: meta.FirstAttemptTime,
                        NextRetryTime: meta.NextRetryTime,
                        DelayedNoticeSent: meta.DelayedNoticeSent,
                        LastError: meta.LastError,
                        MessageSizeBytes: sizeBytes
                    ));
                }
                catch (FileNotFoundException) { /* Delivered and deleted mid-scan */ }
                catch (IOException) { /* Currently being updated by relay worker */ }
            }

            // Order by FirstAttemptTime first
            items.Sort((a, b) => a.FirstAttemptTime.CompareTo(b.FirstAttemptTime));
            return items;
        }

        #endregion


        #region Properties implementation

        /// <summary>
        /// Gets the thread-safe collection of currently active outbound SMTP relay sessions, keyed by session ID.
        /// </summary>
        public ConcurrentDictionary<string, SMTP_Client> Sessions
        {
            get => _activeSessions;
        }

        #endregion
        
    }
}
