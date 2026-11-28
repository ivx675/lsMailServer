using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace lsMailServer.Configuration
{
    /// <summary>
    /// Root configuration model representing the mail server and web host settings.
    /// </summary>
    public sealed class MailServerSettings
    {
        /// <summary>
        /// Gets or sets the Kestrel web server configuration, including HTTPS endpoints and TLS certificate settings.
        /// </summary>
        public KestrelSettings? Kestrel { get; set; }

        /// <summary>
        /// Gets or sets general mail server settings, such as custom DNS resolver addresses.
        /// </summary>
        public GeneralSettings General { get; set; } = new();

        /// <summary>
        /// Gets or sets logging configuration settings for the application and ASP.NET Core components.
        /// </summary>
        public LoggingSettings Logging { get; set; } = new();

        /// <summary>
        /// Gets or sets authentication credentials used to protect the Admin Web UI and REST API.
        /// </summary>
        public AdminAuthSettings AdminAuth { get; set; } = new();

        /// <summary>
        /// Gets or sets access control settings for the web host, such as allowed IP addresses and CIDR ranges.
        /// </summary>
        public WebServerSettings WebServer { get; set; } = new();

        /// <summary>
        /// Gets or sets configuration settings for the inbound SMTP server and its listen endpoints.
        /// </summary>
        public SmtpServerSettings SmtpServer { get; set; } = new();

        /// <summary>
        /// Gets or sets configuration settings for the POP3 retrieval server and its listen endpoints.
        /// </summary>
        public Pop3ServerSettings Pop3Server { get; set; } = new();

        /// <summary>
        /// Gets or sets configuration settings for the IMAP4 mail access server and its listen endpoints.
        /// </summary>
        public ImapServerSettings ImapServer { get; set; } = new();

        /// <summary>
        /// Gets or sets configuration settings for outbound SMTP relay queue processing and optional smart host forwarding.
        /// </summary>
        public SmtpRelaySettings SmtpRelay { get; set; } = new();
    }

    #region class KestrelSettings

    /// <summary>
    /// Represents Kestrel web server hosting configuration settings.
    /// </summary>
    public sealed class KestrelSettings
    {
        /// <summary>
        /// Gets or sets the configured network endpoints (such as HTTPS) exposed by Kestrel.
        /// </summary>
        public KestrelEndpointsSettings? Endpoints { get; set; }
    }

    #endregion

    #region class KestrelEndpointsSettings

    /// <summary>
    /// Represents the collection of network endpoints configured for Kestrel.
    /// </summary>
    public sealed class KestrelEndpointsSettings
    {
        /// <summary>
        /// Gets or sets the HTTPS endpoint settings (listen URL and TLS certificate) for Kestrel.
        /// </summary>
        public HttpsEndpointSettings? Https { get; set; }
    }

    #endregion

    #region class HttpsEndpointSettings

    /// <summary>
    /// Represents configuration settings for an HTTPS Kestrel endpoint.
    /// </summary>
    public sealed class HttpsEndpointSettings
    {
        /// <summary>
        /// Gets or sets the listen URL (scheme, bind address, and port) for the HTTPS endpoint.
        /// </summary>
        public string Url { get; set; } = "https://0.0.0.0:5001";

        /// <summary>
        /// Gets or sets the PKCS#12 certificate reference used to secure the HTTPS endpoint.
        /// </summary>
        public CertificateReference? Certificate { get; set; }
    }

    #endregion

    #region class CertificateReference

    /// <summary>
    /// References a PKCS#12 (<c>.pfx</c> / <c>.p12</c>) certificate file for Kestrel HTTPS.
    /// </summary>
    public sealed class CertificateReference
    {
        /// <summary>
        /// Gets or sets the certificate file path (for example, <c>"Data/Certificates/kestrel.pfx"</c>).
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the certificate password.
        /// When serialized to the UI, this value is always <see langword="null"/>; when saving from the UI,
        /// <see langword="null"/> preserves the existing stored password while empty/whitespace values are rejected.
        /// </summary>
        public string? Password { get; set; }
    }

    #endregion

    #region class GeneralSettings

    /// <summary>
    /// Represents general server-wide settings.
    /// </summary>
    public sealed class GeneralSettings
    {
        /// <summary>
        /// Gets or sets the ordered list of upstream DNS resolver IP addresses used for MX, SPF, PTR, and DKIM queries.
        /// </summary>
        public List<IPAddress> DnsServers { get; set; } = new()
        {
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("8.8.8.8")
        };
    }

    #endregion

    #region class LoggingSettings

    /// <summary>
    /// Represents logging configuration levels.
    /// </summary>
    public sealed class LoggingSettings
    {
        /// <summary>
        /// Gets or sets minimum log levels keyed by logger category name.
        /// </summary>
        public LogLevelSettings LogLevel { get; set; } = new();
    }

    #endregion

    #region class LogLevelSettings

    /// <summary>
    /// Represents minimum log severity levels for core logging categories.
    /// </summary>
    public sealed class LogLevelSettings
    {
        /// <summary>
        /// Gets or sets the default minimum log level across all categories (e.g., <c>"Information"</c>).
        /// </summary>
        public string Default { get; set; } = "Information";

        /// <summary>
        /// Gets or sets the minimum log level for <c>Microsoft.AspNetCore</c> components (e.g., <c>"Warning"</c>).
        /// </summary>
        [JsonPropertyName("Microsoft.AspNetCore")]
        public string MicrosoftAspNetCore { get; set; } = "Warning";
    }

    #endregion

    #region class AdminAuthSettings

    /// <summary>
    /// Represents administrator authentication credentials for the Admin Web UI and REST API.
    /// </summary>
    public sealed class AdminAuthSettings
    {
        /// <summary>
        /// Gets or sets the administrator username.
        /// </summary>
        public string Username { get; set; } = "admin";

        /// <summary>
        /// Gets or sets the administrator password.
        /// Stripped to <see langword="null"/> when sent to the UI; <see langword="null"/> on save keeps the existing password,
        /// and empty/whitespace values are rejected.
        /// </summary>
        public string? Password { get; set; }
    }

    #endregion

    #region class WebServerSettings

    /// <summary>
    /// Represents access control settings for the Admin Web UI and REST API.
    /// </summary>
    public sealed class WebServerSettings
    {
        /// <summary>
        /// Gets or sets the list of client IP addresses or CIDR ranges permitted to access the Admin Web UI and REST API.
        /// </summary>
        public List<string> AllowedIPs { get; set; } = new()
        {
            "127.0.0.1",
            "::1",
            "192.168.0.0/16",
            "10.0.0.0/8",
            "172.16.0.0/12"
        };
    }

    #endregion

    #region class ProtocolServerSettingsBase

    /// <summary>
    /// Base configuration shared by TCP mail protocol servers (SMTP, POP3, and IMAP4).
    /// </summary>
    public abstract class ProtocolServerSettingsBase
    {
        /// <summary>
        /// Gets or sets a value indicating whether this protocol server is enabled on startup.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of concurrent client connections allowed across all endpoints.
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// Gets or sets the maximum number of concurrent client connections permitted from a single remote IP address.
        /// </summary>
        public int MaxConnectionsPerIP { get; set; } = 10;

        /// <summary>
        /// Gets or sets the idle connection timeout in seconds before an inactive session is disconnected.
        /// </summary>
        public int SessionIdleTimeout { get; set; } = 300;

        /// <summary>
        /// Gets or sets the collection of IP/Port/TLS bindings on which this protocol server listens.
        /// </summary>
        public List<ListenEndpointSettings> ListenEndpoints { get; set; } = new();
    }

    #endregion

    #region class SmtpServerSettings

    /// <summary>
    /// Represents inbound SMTP server configuration.
    /// </summary>
    public sealed class SmtpServerSettings : ProtocolServerSettingsBase
    {
        /// <summary>
        /// Gets or sets the maximum accepted message size in bytes (defaults to 25 MB / 26,214,400 bytes).
        /// </summary>
        public long MaxMessageSize { get; set; } = 26_214_400;

        /// <summary>
        /// Gets or sets a value indicating whether inbound unauthenticated senders are validated via PTR, SPF, and MX/A fallback.
        /// </summary>
        public bool EnableSenderValidation { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether inbound messages undergo DKIM cryptographic signature verification.
        /// </summary>
        public bool EnableDkimValidation { get; set; } = true;
    }

    #endregion

    #region class Pop3ServerSettings

    /// <summary>
    /// Represents POP3 mail retrieval server configuration.
    /// </summary>
    public sealed class Pop3ServerSettings : ProtocolServerSettingsBase
    {
    }

    #endregion

    #region class ImapServerSettings

    /// <summary>
    /// Represents IMAP4 mail access server configuration.
    /// </summary>
    public sealed class ImapServerSettings : ProtocolServerSettingsBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImapServerSettings"/> class with IMAP-appropriate defaults.
        /// </summary>
        public ImapServerSettings()
        {
            MaxConnections = 200;
            MaxConnectionsPerIP = 15;
            SessionIdleTimeout = 1800;
        }
    }

    #endregion

    #region class ListenEndpointSettings

    /// <summary>
    /// Represents a single TCP listening endpoint binding for SMTP, POP3, or IMAP4.
    /// </summary>
    public sealed class ListenEndpointSettings
    {
        /// <summary>
        /// Gets or sets the greeting/advertised hostname for this endpoint.
        /// </summary>
        public string HostName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the local IPv4 or IPv6 address to bind (e.g., <see cref="IPAddress.Any"/> or <see cref="IPAddress.IPv6Any"/>).
        /// </summary>
        public IPAddress IP { get; set; } = IPAddress.Any;

        /// <summary>
        /// Gets or sets the TCP port number (1–65535).
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Gets or sets the TLS transport mode: <c>"None"</c>, <c>"Explicit"</c> (STARTTLS/STLS), or <c>"Implicit"</c> (TLS).
        /// </summary>
        public string TlsMode { get; set; } = "Explicit";

        /// <summary>
        /// Gets or sets the <c>.pfx</c> certificate file name in <c>Data/Certificates</c> used when TLS is enabled.
        /// </summary>
        public string CertFileName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the password for <see cref="CertFileName"/>.
        /// Stripped to <see langword="null"/> when returned to the UI; <see langword="null"/> on save preserves the existing password,
        /// while empty/whitespace values are rejected.
        /// </summary>
        public string? CertPassword { get; set; }
    }

    #endregion

    #region class SmtpRelaySettings

    /// <summary>
    /// Represents outbound SMTP relay queue, retry, DSN notification, and Smart Host configuration.
    /// </summary>
    public sealed class SmtpRelaySettings
    {
        /// <summary>
        /// Gets or sets the EHLO/HELO hostname presented to remote MX servers during outbound delivery.
        /// </summary>
        public string HostName { get; set; } = "host.com";

        /// <summary>
        /// Gets or sets the maximum number of simultaneous outbound SMTP delivery connections across all domains.
        /// </summary>
        public int MaxOutboundConnections { get; set; } = 20;

        /// <summary>
        /// Gets or sets the maximum number of simultaneous outbound SMTP connections to a single destination domain.
        /// </summary>
        public int MaxConnectionsPerDomain { get; set; } = 2;

        /// <summary>
        /// Gets or sets the maximum duration of a single outbound SMTP session formatted as a <see cref="TimeSpan"/> string (e.g., <c>"00:05:00"</c>).
        /// </summary>
        public string MaxSessionTimeout { get; set; } = "00:05:00";

        /// <summary>
        /// Gets or sets a value indicating whether opportunistic STARTTLS should be negotiated when supported by the remote server.
        /// </summary>
        public bool UseTlsIfPossible { get; set; }

        /// <summary>
        /// Gets or sets the queue scan interval formatted as a <see cref="TimeSpan"/> string (e.g., <c>"00:05:00"</c>).
        /// </summary>
        public string RetryInterval { get; set; } = "00:05:00";

        /// <summary>
        /// Gets or sets the elapsed queue time after which a delayed Delivery Status Notification (DSN) is sent to the sender (e.g., <c>"00:15:00"</c>).
        /// </summary>
        public string DelayWarningAfter { get; set; } = "00:15:00";

        /// <summary>
        /// Gets or sets the maximum queue age after which an undeliverable message is bounced to the sender (e.g., <c>"0.12:00:00"</c>).
        /// </summary>
        public string UndeliveredAfter { get; set; } = "0.12:00:00";

        /// <summary>
        /// Gets or sets optional upstream SMTP Smart Host relay settings.
        /// </summary>
        public SmartHostSettings SmartHost { get; set; } = new();
    }

    #endregion

    #region class SmartHostSettings

    /// <summary>
    /// Represents upstream SMTP Smart Host relay configuration.
    /// </summary>
    public sealed class SmartHostSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether outbound mail is routed through the Smart Host instead of direct MX lookup.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the Smart Host FQDN or IP address.
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Smart Host TCP port (typically <c>587</c>, <c>465</c>, or <c>25</c>).
        /// </summary>
        public int Port { get; set; } = 587;

        /// <summary>
        /// Gets or sets the optional SMTP AUTH username for authenticating with the Smart Host.
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the optional SMTP AUTH password for authenticating with the Smart Host.
        /// Stripped to <see langword="null"/> when returned to the UI; <see langword="null"/> on save preserves the existing password,
        /// while empty/whitespace values are rejected.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// Gets or sets the TLS transport mode used when connecting to the Smart Host: <c>"None"</c>, <c>"Explicit"</c>, or <c>"Implicit"</c>.
        /// </summary>
        public string TlsMode { get; set; } = "Explicit";
    }

    #endregion

    #region class MailServerSettingsStore

    /// <summary>
    /// Thread-safe persistence store for <see cref="MailServerSettings"/> that reads and atomically writes
    /// configuration JSON while ensuring raw passwords are never exposed to the UI.
    /// </summary>
    public sealed class MailServerSettingsStore : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new IPAddressJsonConverter() }
        };

        private readonly string _settingsFilePath;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="MailServerSettingsStore"/> class.
        /// </summary>
        /// <param name="settingsFilePath">Full file path to the persisted JSON configuration file (e.g., <c>Data/settings.json</c>).</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="settingsFilePath"/> is <see langword="null"/> or whitespace.</exception>
        public MailServerSettingsStore(string settingsFilePath)
        {
            if (string.IsNullOrWhiteSpace(settingsFilePath))
                throw new ArgumentException("Settings file path cannot be empty.", nameof(settingsFilePath));

            _settingsFilePath = settingsFilePath;
        }

        #region mehtod LoadRawAsync

        /// <summary>
        /// Loads the raw <see cref="MailServerSettings"/> (including stored secrets) from disk,
        /// returning default settings if the file does not yet exist.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The deserialized <see cref="MailServerSettings"/> instance.</returns>
        public async Task<MailServerSettings> LoadRawAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                return await LoadRawInternalAsync(cancellationToken).ConfigureAwait(false);
            }
            finally {
                _ioLock.Release();
            }
        }

        #endregion

        #region method GetSanitizedForUiAsync

        /// <summary>
        /// Returns a sanitized copy of <see cref="MailServerSettings"/> suitable for sending to the Admin UI,
        /// with all password fields set to <see langword="null"/> so secrets are never transmitted over the API.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A sanitized <see cref="MailServerSettings"/> instance with all secret fields nulled out.</returns>
        public async Task<MailServerSettings> GetSanitizedForUiAsync(CancellationToken cancellationToken = default)
        {
            MailServerSettings current = await LoadRawAsync(cancellationToken).ConfigureAwait(false);

            current.AdminAuth.Password = null;
            if (current.Kestrel?.Endpoints?.Https?.Certificate is not null) {
                current.Kestrel.Endpoints.Https.Certificate.Password = null;
            }
            current.SmtpRelay.SmartHost.Password = null;

            foreach (ListenEndpointSettings ep in current.SmtpServer.ListenEndpoints) {
                ep.CertPassword = null;
            }

            foreach (ListenEndpointSettings ep in current.Pop3Server.ListenEndpoints) {
                ep.CertPassword = null;
            }

            foreach (ListenEndpointSettings ep in current.ImapServer.ListenEndpoints) {
                ep.CertPassword = null;
            }

            return current;
        }

        #endregion

        #region method SaveFromUiAsync

        /// <summary>
        /// Validates and atomically persists settings submitted from the Admin UI, merging existing on-disk secrets:
        /// <list type="bullet">
        ///   <item><description><see langword="null"/>: keeps the existing stored password unchanged.</description></item>
        ///   <item><description>Empty or whitespace (<c>""</c> / <c>"   "</c>): throws <see cref="ArgumentException"/> (never allowed).</description></item>
        ///   <item><description>Non-empty string: updates the stored password.</description></item>
        /// </list>
        /// </summary>
        /// <param name="incoming">The settings payload received from the Admin UI.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="incoming"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when any password field is empty/whitespace or when <c>AdminAuth.Password</c> is missing.</exception>
        public async Task SaveFromUiAsync(MailServerSettings incoming, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(incoming);
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                MailServerSettings existing = await LoadRawInternalAsync(cancellationToken).ConfigureAwait(false);

                incoming.AdminAuth.Password = ResolveRequiredSecret(
                    incoming.AdminAuth.Password,
                    existing.AdminAuth.Password,
                    "AdminAuth.Password");

                if (incoming.Kestrel?.Endpoints?.Https?.Certificate is not null) {
                    incoming.Kestrel.Endpoints.Https.Certificate.Password = ResolveOptionalSecret(
                        incoming.Kestrel.Endpoints.Https.Certificate.Password,
                        existing.Kestrel?.Endpoints?.Https?.Certificate?.Password,
                        "Kestrel.Endpoints.Https.Certificate.Password");
                }

                incoming.SmtpRelay.SmartHost.Password = ResolveOptionalSecret(
                    incoming.SmtpRelay.SmartHost.Password,
                    existing.SmtpRelay.SmartHost.Password,
                    "SmtpRelay.SmartHost.Password");

                ResolveEndpointSecrets(
                    incoming.SmtpServer.ListenEndpoints,
                    existing.SmtpServer.ListenEndpoints,
                    "SmtpServer");

                ResolveEndpointSecrets(
                    incoming.Pop3Server.ListenEndpoints,
                    existing.Pop3Server.ListenEndpoints,
                    "Pop3Server");

                ResolveEndpointSecrets(
                    incoming.ImapServer.ListenEndpoints,
                    existing.ImapServer.ListenEndpoints,
                    "ImapServer");

                string? directory = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(directory)) {
                    Directory.CreateDirectory(directory);
                }

                string tempPath = _settingsFilePath + ".tmp";
                string json = JsonSerializer.Serialize(incoming, JsonOptions);
                await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, _settingsFilePath, overwrite: true);
            }
            finally {
                _ioLock.Release();
            }
        }

        #endregion

        #region method LoadRawInternalAsync

        /// <summary>
        /// Reads and deserializes the settings file without acquiring <see cref="_ioLock"/>.
        /// Caller must hold <see cref="_ioLock"/>.
        /// </summary>
        private async Task<MailServerSettings> LoadRawInternalAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_settingsFilePath)) {
                return new MailServerSettings();
            }

            string json = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) {
                return new MailServerSettings();
            }

            return JsonSerializer.Deserialize<MailServerSettings>(json, JsonOptions) ?? new MailServerSettings();
        }

        #endregion

        #region method ResolveRequiredSecret

        /// <summary>
        /// Resolves a mandatory password field where <see langword="null"/> retains the existing non-empty value
        /// and empty/whitespace values are rejected.
        /// </summary>
        private static string ResolveRequiredSecret(string? incoming, string? existing, string fieldName)
        {
            if (incoming is null) {
                if (string.IsNullOrWhiteSpace(existing)) {
                    throw new ArgumentException($"{fieldName} cannot be empty.", fieldName);
                }
                return existing;
            }

            if (string.IsNullOrWhiteSpace(incoming)) {
                throw new ArgumentException($"{fieldName} cannot be empty or whitespace.", fieldName);
            }

            return incoming;
        }

        #endregion

        #region method ResolveOptionalSecret

        /// <summary>
        /// Resolves an optional password field where <see langword="null"/> retains the existing value
        /// and empty/whitespace values are rejected.
        /// </summary>
        private static string? ResolveOptionalSecret(string? incoming, string? existing, string fieldName)
        {
            if (incoming is null) {
                return existing;
            }

            if (string.IsNullOrWhiteSpace(incoming)) {
                throw new ArgumentException(
                    $"{fieldName} cannot be empty or whitespace. Send null to keep unchanged.",
                    fieldName);
            }

            return incoming;
        }

        #endregion

        #region method ResolveEndpointSecrets

        /// <summary>
        /// Resolves <see cref="ListenEndpointSettings.CertPassword"/> values across a list of protocol endpoints,
        /// matching existing endpoints by <c>(IP, Port)</c> first and falling back to list index.
        /// </summary>
        private static void ResolveEndpointSecrets(
            List<ListenEndpointSettings> incoming,
            List<ListenEndpointSettings> existing,
            string sectionName)
        {
            for (int i = 0; i < incoming.Count; i++)
            {
                ListenEndpointSettings? match = existing.Find(e =>
                    e.Port == incoming[i].Port &&
                    Equals(e.IP, incoming[i].IP))
                    ?? (i < existing.Count ? existing[i] : null);

                incoming[i].CertPassword = ResolveOptionalSecret(
                    incoming[i].CertPassword,
                    match?.CertPassword,
                    $"{sectionName}.ListenEndpoints[{i}].CertPassword");
            }
        }

        #endregion

        #region method Dispose

        /// <summary>
        /// Releases resources used by the <see cref="MailServerSettingsStore"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) {
                return;
            }

            _disposed = true;
            _ioLock.Dispose();
        }

        #endregion
    }

    #endregion

    #region class IPAddressJsonConverter

    /// <summary>
    /// Serializes and deserializes <see cref="IPAddress"/> values to and from JSON strings.
    /// </summary>
    public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
    {
        /// <summary>
        /// Reads a JSON string token and parses it into an <see cref="IPAddress"/> instance.
        /// </summary>
        /// <param name="reader">The UTF-8 JSON reader positioned at the token to deserialize.</param>
        /// <param name="typeToConvert">The target type being converted (<see cref="IPAddress"/>).</param>
        /// <param name="options">Serializer options governing the deserialization operation.</param>
        /// <returns>
        /// The parsed <see cref="IPAddress"/> instance, or <see langword="null"/> when the JSON value is <see langword="null"/> or empty/whitespace.
        /// </returns>
        /// <exception cref="JsonException">
        /// Thrown when the JSON string value cannot be parsed as a valid IPv4 or IPv6 address.
        /// </exception>
        public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return IPAddress.TryParse(value.Trim(), out IPAddress? address)
                ? address
                : throw new JsonException($"Invalid IP address value: '{value}'.");
        }

        /// <summary>
        /// Writes an <see cref="IPAddress"/> instance as a JSON string value.
        /// </summary>
        /// <param name="writer">The UTF-8 JSON writer receiving the serialized value.</param>
        /// <param name="value">The <see cref="IPAddress"/> instance to serialize.</param>
        /// <param name="options">Serializer options governing the serialization operation.</param>
        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }

    #endregion
}