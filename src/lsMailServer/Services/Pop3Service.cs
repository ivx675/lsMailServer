using lsMailServer.Configuration;
using lsMailServer.RestApi;
using LumiSoft.Net.AUTH;
using LumiSoft.Net.Log;
using LumiSoft.Net.POP3;
using LumiSoft.Net.POP3.Server;
using LumiSoft.Net.TCP;
using System.Text.Json;

namespace lsMailServer.Services
{
    /// <summary>
    /// Hosted background service that manages the inbound POP3 server lifecycle,
    /// listener endpoints, TLS certificates, authentication, and mailbox message retrieval/deletion.
    /// </summary>
    public class Pop3Service : IHostedService, IDisposable
    {
        private readonly ILogger<Pop3Service> _logger;
        private readonly IConfiguration _configuration;
        private readonly FailedAuthLimiterService _failedAuthLimiterService;
        private readonly IDatabaseService _dbService;
        private readonly MailStoreService _mailStoreService;
        private readonly POP3_Server _pop3Server;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="Pop3Service"/> class.
        /// </summary>
        /// <param name="logger">The logger used to record POP3 server and session diagnostics.</param>
        /// <param name="configuration">The application configuration containing the <c>Pop3Server</c> section.</param>
        /// <param name="failedAuthLimiterService">The rate limiter service used to track and block repeated failed authentication attempts.</param>
        /// <param name="dbService">The database service used to authenticate users.</param>
        /// <param name="mailStoreService">The mail store service used to list, stream, and delete mailbox messages.</param>
        public Pop3Service(
            ILogger<Pop3Service> logger,
            IConfiguration configuration,
            FailedAuthLimiterService failedAuthLimiterService,
            IDatabaseService dbService,
            MailStoreService mailStoreService)
        {
            _logger = logger;
            _configuration = configuration;
            _failedAuthLimiterService = failedAuthLimiterService;
            _dbService = dbService;
            _mailStoreService = mailStoreService;

            _pop3Server = new POP3_Server();
            _pop3Server.Error += (s, e) => {
                _logger.LogError(e.Exception, "Error in POP3 server.");
            };
            _pop3Server.SessionCreatedAsync += SessionCreated;            
            _pop3Server.Logger = new Logger();
            _pop3Server.Logger.WriteLog += SessionLog;
        }

        #region method Dispose

        /// <summary>
        /// Releases all resources used by the underlying <see cref="POP3_Server"/> instance.
        /// </summary>
        public void Dispose()
        {
            _pop3Server.Dispose();
        }

        #endregion


        #region Event handlers

        #region method SessionCreated

        /// <summary>
        /// Raised when a new client TCP session is accepted by the POP3 server.
        /// Configures session-level error logging, SASL authentication mechanisms, and POP3 command handlers.
        /// </summary>
        /// <param name="e">The event arguments containing the newly created <see cref="POP3_Session"/>.</param>
        private Task SessionCreated(TCP_Server_e_SessionCreated<POP3_Session> e)
        {
            // Raised when error occurs in session.
            e.Session.ErrorAsync += (eArgs) => {
                _logger.LogError(eArgs.Exception, "POP3 session error from {RemoteIp} (Session: {SessionId}).", e.Session.RemoteEndPoint?.Address, e.Session.ID);
                return Task.CompletedTask;
            };

            // Add SASL plain authentication.
            var sasPlain = new AUTH_SASL_ServerMechanism_Plain(true);
            sasPlain.Authenticate += async (s,eArgs) => { 
                await AuthPlainAsync(eArgs, e.Session);
            };
            e.Session.Authentications.Add(sasPlain.Name,sasPlain);

            e.Session.AuthUserPassAsync += AuthUserPassAsync;
            e.Session.LoadMessagesInfoAsync += LoadMessagesInfoAsync;
            e.Session.GetMessageStreamAsync += GetMessageStreamAsync;
            e.Session.DeleteMessagesAsync += DeleteMessagesAsync;

            return Task.CompletedTask;
        }

        #endregion

        #region method SessionLog

        /// <summary>
        /// Raised when the underlying protocol logger writes a session log entry,
        /// forwarding protocol wire traffic, diagnostic events, and exceptions to <see cref="_logger"/>.
        /// </summary>
        /// <param name="sender">The source of the log event.</param>
        /// <param name="eArgs">The event arguments containing the <see cref="LogEntry"/> to record.</param>
        private void SessionLog(object? sender, WriteLogEventArgs eArgs)
        {
            var entry = eArgs.LogEntry;
            string remoteEp = "unknown";

            if (entry.RemoteEndPoint != null) {
                var address = entry.RemoteEndPoint.Address.IsIPv4MappedToIPv6
                    ? entry.RemoteEndPoint.Address.MapToIPv4()
                    : entry.RemoteEndPoint.Address;

                remoteEp = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? $"[{address}]:{entry.RemoteEndPoint.Port}"
                    : $"{address}:{entry.RemoteEndPoint.Port}";
            }

            if (entry.EntryType == LogEntryType.Exception) {
                if (_logger.IsEnabled(LogLevel.Debug)) {
                    _logger.LogDebug(
                        entry.Exception,
                        "SMTP protocol error (Session: {SessionId}, Remote: {RemoteEp}, User: {User}): {Text}",
                        entry.ID,
                        remoteEp,
                        entry.UserIdentity?.Name ?? "-",
                        entry.Text);
                }
                return;
            }

            LogLevel targetLevel = entry.EntryType == LogEntryType.Text
                ? LogLevel.Debug
                : LogLevel.Trace;

            if (!_logger.IsEnabled(targetLevel)) {
                return; // Zero allocation when Trace/Debug is disabled
            }

            _logger.Log(
                targetLevel,
                "[{EntryType}] (Session: {SessionId}, Remote: {RemoteEp}, User: {User}, Size: {Size}) {Text}",
                entry.EntryType,
                entry.ID,
                remoteEp,
                entry.UserIdentity?.Name ?? "-",
                entry.Size,
                entry.Text);
        }

        #endregion

        #region method AuthPlainAsync

        /// <summary>
        /// Raised when an <c>AUTH PLAIN</c> command is received from a client session.
        /// Validates user credentials against the database while enforcing failed-attempt rate limiting.
        /// </summary>
        /// <param name="eArgs">The SASL authentication event arguments containing credentials and authentication state.</param>
        /// <param name="session">The active <see cref="POP3_Session"/> requesting authentication.</param>
        private async Task AuthPlainAsync(AUTH_e_Authenticate eArgs, POP3_Session session)
        {
            try {
                if(_failedAuthLimiterService.IsBlocked(session.RemoteEndPoint.Address, eArgs.UserName, out _)) {
                    eArgs.IsAuthenticated = false;
                }
                else if (await _dbService.AuthenticateUserAsync(eArgs.UserName, eArgs.Password) != null) {
                    eArgs.IsAuthenticated = true;

                    _logger.LogInformation(
                        "User '{UserName}' authenticated successfully from {RemoteIp} (Session: {SessionId})",
                        eArgs.UserName,
                        session.RemoteEndPoint.Address,
                        session.ID);
                }
                else if(await _dbService.GetUserAsync(eArgs.UserName) != null) {
                    _failedAuthLimiterService.RecordFailure(session.RemoteEndPoint.Address, eArgs.UserName);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error during POP3 authentication.");
            }
        }

        #endregion

        #region method AuthUserPassAsync

        /// <summary>
        /// Raised when <c>USER</c>/<c>PASS</c> authentication is received from a client session.
        /// Validates user credentials against the database while enforcing failed-attempt rate limiting.
        /// </summary>
        /// <param name="eArgs">The POP3 authentication event arguments containing the session, credentials, and server response.</param>
        private async Task AuthUserPassAsync(POP3_e_AuthUserPass eArgs)
        {
            try {
                if(_failedAuthLimiterService.IsBlocked(eArgs.Session.RemoteEndPoint.Address, eArgs.UserName, out _)) {
                    // Server reports default authentication failure response automatically
                }
                else if (await _dbService.AuthenticateUserAsync(eArgs.UserName, eArgs.Password) != null) {
                    eArgs.Response = new POP3_ServerResponse("+OK",null,"Authenticated successfully.");

                    _logger.LogInformation(
                        "User '{UserName}' authenticated successfully from {RemoteIp} (Session: {SessionId})",
                        eArgs.UserName,
                        eArgs.Session.RemoteEndPoint.Address,
                        eArgs.Session.ID);
                }
                else if(await _dbService.GetUserAsync(eArgs.UserName) != null) {
                    _failedAuthLimiterService.RecordFailure(eArgs.Session.RemoteEndPoint.Address, eArgs.UserName);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error during POP3 authentication.");
            }
        }

        #endregion

        #region method LoadMessagesInfoAsync

        /// <summary>
        /// Raised after authentication succeeds to populate the list of messages and sizes in the user's <c>Inbox</c>.
        /// </summary>
        /// <param name="eArgs">The event arguments containing the active session and target message collection.</param>
        private async Task LoadMessagesInfoAsync(POP3_e_LoadMessagesInfo eArgs)
        {
            try {
                var folderInfo = await _mailStoreService.GetFolderInfoAsync(eArgs.Session.AuthenticatedUserIdentity!.Name, "Inbox");
                if (folderInfo != null) {
                    foreach (var msgInfo in folderInfo.Messages) {
                        eArgs.Messages.Add(new POP3_ServerMessage(msgInfo.MessageUid.ToString(), (int)msgInfo.MessageSize));
                    }
                }                    
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error loading messages for user: {UserName}", eArgs.Session.AuthenticatedUserIdentity?.Name);
            }
        }

        #endregion

        #region method GetMessageStreamAsync

        /// <summary>
        /// Raised when a <c>TOP</c> or <c>RETR</c> command is received and requires a readable stream for the requested message.
        /// </summary>
        /// <param name="eArgs">The event arguments identifying the requested message UID and destination stream.</param>
        private async Task GetMessageStreamAsync(POP3_e_GetMessageStream eArgs)
        {
            try {
                // Retrieve the message stream for the requested message UID
                var messageStream = await _mailStoreService.GetMessageStreamAsync(
                    eArgs.Session.AuthenticatedUserIdentity!.Name, "Inbox", long.Parse(eArgs.Message.UID), true);
                eArgs.MessageStream = messageStream;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error retrieving message stream for UID: {MessageUid}", eArgs.Message.UID);
            }
        }

        #endregion

        #region method DeleteMessagesAsync

        /// <summary>
        /// Raised when a POP3 session enters the <c>UPDATE</c> state on <c>QUIT</c> to permanently delete messages marked with <c>DELE</c>.
        /// </summary>
        /// <param name="eArgs">The event arguments containing the messages marked for deletion.</param>
        private async Task DeleteMessagesAsync(POP3_e_DeleteMessages eArgs)
        {
            try {
                string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;

                // Delete messages marked for deletion
                foreach (var msg in eArgs.Messages) {
                    try {
                        await _mailStoreService.DeleteMessageAsync(userName, "Inbox", long.Parse(msg.UID));
                    }
                    catch (Exception x) {
                        _logger.LogWarning(
                            x,
                            "Failed to delete message UID '{MessageUid}' from Inbox for user '{UserName}' from {RemoteIp} (Session: {SessionId})",
                            msg.UID,
                            userName,
                            eArgs.Session.RemoteEndPoint?.ToString() ?? "unknown",
                            eArgs.Session.ID);
                    }
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error deleting messages for user: {UserName}", eArgs.Session.AuthenticatedUserIdentity?.Name);
            }
        }

        #endregion

        #endregion


        #region method StartAsync

        /// <summary>
        /// Applies current <c>Pop3Server</c> configuration settings, initializes TLS listen endpoints, and starts the POP3 server.
        /// </summary>
        /// <param name="cancellationToken">A token that signals when the start operation should be canceled.</param>
        /// <returns>A completed <see cref="Task"/> once the POP3 server has been configured and started.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_pop3Server.IsRunning) {
                _pop3Server.Stop();
            }            

            var settings = _configuration.GetSection("Pop3Server").Get<Pop3ServerSettings>() ?? new();

            if (!settings.Enabled) {
                _logger.LogInformation("POP3 server is disabled in configuration.");
                return Task.CompletedTask;
            }

            var pop3Endpoints = new List<TCP_ServerEndpoint>();
            foreach(var listenEp in settings.ListenEndpoints) {
                try {
                    var hostName = listenEp.HostName;
                    var tlsMode = TCP_ServerTlsMode.None;
                    if(string.Equals(listenEp.TlsMode, "Implicit", StringComparison.OrdinalIgnoreCase)) {
                        tlsMode = TCP_ServerTlsMode.Implicit;
                    }
                    else if (string.Equals(listenEp.TlsMode, "Explicit", StringComparison.OrdinalIgnoreCase)) {
                        tlsMode = TCP_ServerTlsMode.Explicit;
                    }
                    var certFileName = !string.IsNullOrEmpty(listenEp.CertFileName) ? listenEp.CertFileName : "default.pfx";
                    var certPassword = listenEp.CertPassword ?? "";
                    var cert = tlsMode != TCP_ServerTlsMode.None || !string.IsNullOrEmpty(listenEp.CertFileName)
                        ? Program.LoadOrCreateCert(certFileName, certPassword, hostName, _logger)
                        : null;
                    
                    pop3Endpoints.Add(new TCP_ServerEndpoint(
                        hostName,
                        listenEp.IP,
                        listenEp.Port,
                        tlsMode,
                        cert
                    ));

                    _logger.LogInformation(
                        "Configured POP3 endpoint {HostName} on {IP}:{Port} (TLS: {TlsMode})",
                        listenEp.HostName,
                        listenEp.IP.ToString(),
                        listenEp.Port,
                        listenEp.TlsMode);
                }
                catch (Exception x) {
                    // Capture the raw configuration values from this specific child node for logging
                    var safeLogPayload = new {
                        HostName = listenEp.HostName,
                        IP = listenEp.IP.ToString(),
                        Port = listenEp.Port,
                        TlsMode = listenEp.TlsMode,
                        CertPassword = string.IsNullOrEmpty(listenEp.CertPassword) ? "[NOT SET]" : "[REDACTED]"
                    };

                    _logger.LogError(x, 
                        "CRITICAL: Failed to initialize POP3 listener endpoint. " +
                        "Offending Config: {EndpointJson} | Reason: {Error}", 
                        JsonSerializer.Serialize(safeLogPayload), x.Message);
                }
            }

            _pop3Server.SessionIdleTimeout = settings.SessionIdleTimeout;
            _pop3Server.MaxConnections = settings.MaxConnections;
            _pop3Server.MaxConnectionsPerIP = settings.MaxConnectionsPerIP;            
            _pop3Server.ListenEndpoints = pop3Endpoints.ToArray();
            _pop3Server.Start();

            _logger.LogInformation("POP3 server started.");

            return Task.CompletedTask;
        }

        #endregion

        #region method StopAsync

        /// <summary>
        /// Stops the POP3 server listener while allowing active sessions to finish gracefully.
        /// </summary>
        /// <param name="cancellationToken">A token that signals when the stop operation should be canceled.</param>
        /// <returns>A completed <see cref="Task"/> once the POP3 server listener has stopped.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_pop3Server.IsRunning) {
                _pop3Server.Stop();
                _logger.LogInformation("POP3 server stopped.");
            }

            return Task.CompletedTask;
        }

        #endregion

        
        #region method GetSessions

        /// <summary>
        /// Retrieves a snapshot of all currently active sessions.
        /// </summary>
        /// <returns>
        /// A list of <see cref="SessionsController.SessionDto"/> objects representing each active session and its current state.
        /// </returns>
        public List<SessionsController.SessionDto> GetSessions()
        {
            var retValue = new List<SessionsController.SessionDto>();
            try{
                foreach(var session in _pop3Server!.Sessions.ToArray()) {
                    try {
                        retValue.Add(new SessionsController.SessionDto(
                            session.ID,
                            "POP3",
                            session.RemoteEndPoint.ToString(),
                            session.LocalEndPoint.ToString(),
                            session.IsSecureConnection,
                            session.IsAuthenticated,
                            session.AuthenticatedUserIdentity?.Name,
                            session.StatusText,
                            session.ConnectTime,
                            session.LastActivity
                        ));
                    }
                    catch {
                    }
                }
            }
            catch {
            }

            return retValue;
        }

        #endregion

        #region method DisconnectSession

        /// <summary>
        /// Disconnects an active session identified by its session ID.
        /// </summary>
        /// <param name="id">The unique identifier of the session to disconnect.</param>
        public void DisconnectSession(string id)
        {
            try{
                _pop3Server!.Sessions[id].Disconnect();
            }
            catch {
            }
        }

        #endregion
    }
}
