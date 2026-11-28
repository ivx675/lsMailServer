using lsMailServer.Configuration;
using lsMailServer.RestApi;
using LumiSoft.Net.AUTH;
using LumiSoft.Net.Log;
using LumiSoft.Net.Mail;
using LumiSoft.Net.SMTP;
using LumiSoft.Net.SMTP.Server;
using LumiSoft.Net.TCP;
using System.Net;
using System.Text.Json;

namespace lsMailServer.Services
{
    /// <summary>
    /// Hosted background service that manages the inbound SMTP server lifecycle,
    /// listener endpoints, TLS certificates, authentication, and message storage/relay routing.
    /// </summary>
    public class SmtpService : IHostedService, IDisposable
    {
        private readonly ILogger<SmtpService> _logger;
        private readonly IConfiguration _configuration;
        private readonly FailedAuthLimiterService _failedAuthLimiterService;
        private readonly IDatabaseService _dbService;
        private readonly MailStoreService _mailStoreService;
        private readonly SMTP_Server _smtpServer;
        private readonly SmtpRelayService _relayServer;

        private bool _validateSender;
        private bool _validateDkimSignature;

        /// <summary>
        /// Initializes a new instance of the <see cref="SmtpService"/> class.
        /// </summary>
        /// <param name="logger">The logger used to record SMTP server and session diagnostics.</param>
        /// <param name="configuration">The application configuration containing the <c>SmtpServer</c> section.</param>
        /// <param name="failedAuthLimiterService">The rate limiter service used to track and block repeated failed authentication attempts.</param>
        /// <param name="dbService">The database service used to validate domains, email addresses, users, and quotas.</param>
        /// <param name="mailStoreService">The mail store service used to create temporary message streams and deliver local messages.</param>
        /// <param name="relayService">The outbound SMTP relay service used to queue messages addressed to external recipients.</param>
        public SmtpService(
            ILogger<SmtpService> logger,
            IConfiguration configuration,
            FailedAuthLimiterService failedAuthLimiterService,
            IDatabaseService dbService,
            MailStoreService mailStoreService,
            SmtpRelayService relayService)
        {
            _logger = logger;
            _configuration = configuration;
            _failedAuthLimiterService = failedAuthLimiterService;
            _dbService = dbService;
            _mailStoreService = mailStoreService;
            _relayServer = relayService;

            _smtpServer = new SMTP_Server();
            _smtpServer.Error += (s, e) => {
                _logger.LogError(e.Exception, "Error in SMTP server.");
            };
            _smtpServer.SessionCreatedAsync += SessionCreated;            
            _smtpServer.Logger = new Logger();
            _smtpServer.Logger.WriteLog += SessionLog;
        }

        #region method Dispose

        /// <summary>
        /// Releases all resources used by the underlying <see cref="SMTP_Server"/> instance.
        /// </summary>
        public void Dispose()
        {
            _smtpServer.Dispose();
        }

        #endregion


        #region Event handlers

        #region method SessionCreated

        /// <summary>
        /// Raised when a new client TCP session is accepted by the SMTP server.
        /// Configures session-level error logging, SASL authentication mechanisms, and SMTP command handlers.
        /// </summary>
        /// <param name="e">The event arguments containing the newly created <see cref="SMTP_Session"/>.</param>
        private Task SessionCreated(TCP_Server_e_SessionCreated<SMTP_Session> e)
        {
            // Raised when error occurs in session.
            e.Session.ErrorAsync += (eArgs) => {
                _logger.LogError(eArgs.Exception, "SMTP session error from {RemoteIp} (Session: {SessionId}).", e.Session.RemoteEndPoint?.Address, e.Session.ID);
                return Task.CompletedTask;
            };

            // Add SASL plain authentication.
            var sasPlain = new AUTH_SASL_ServerMechanism_Plain(true);
            sasPlain.Authenticate += async (s,eArgs) => {
                await AuthPlainAsync(eArgs, e.Session);
            };
            e.Session.Authentications.Add(sasPlain.Name,sasPlain);

            e.Session.MailFromAsync += MailFromAsync;
            e.Session.RcptToAsync += RcptToAsync;
            e.Session.MessageStoringBeginAsync += MessageStoringBeginAsync;
            e.Session.MessageStoringCancelAsync += MessageStoringCancelAsync;
            e.Session.MessageStoringCompleteAsync += MessageStoringCompleteAsync;

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
        /// <param name="session">The active <see cref="SMTP_Session"/> requesting authentication.</param>
        private async Task AuthPlainAsync(AUTH_e_Authenticate eArgs, SMTP_Session session)
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
                _logger.LogError(
                    ex,
                    "Error during SMTP authentication for user '{UserName}' from {RemoteIp} (Session: {SessionId}).",
                    eArgs.UserName,
                    session.RemoteEndPoint?.Address,
                    session.ID);
            }
        }

        #endregion

        #region method MailFromAsync

        /// <summary>
        /// Raised when a <c>MAIL FROM</c> command is received.
        /// Validates local sender existence, authenticated user send-as permissions and daily quotas,
        /// and anti-spam sender checks (PTR, SPF, MX) for unauthenticated external clients.
        /// </summary>
        /// <param name="eArgs">The <c>MAIL FROM</c> event arguments containing the sender address and SMTP response.</param>
        private async Task MailFromAsync(SMTP_e_MailFrom eArgs)
        {
            try{
                IPAddress remoteIP = eArgs.Session.RemoteEndPoint.Address;
                
                // Local domain, check if aaddress exists.
                if (await _dbService.DomainExistsAsync(eArgs.MailFrom.Domain)) {
                    if(!await _dbService.EmailAddressExistsAsync(eArgs.MailFrom.Mailbox)) {
                        eArgs.Response = new (550, new (5, 1, 1), $"Sender address '{eArgs.MailFrom.Mailbox}' does not exist.");

                        return;
                    }
                }
                
                if (eArgs.Session.AuthenticatedUserIdentity != null) {
                    var user = await _dbService.GetUserAsync(eArgs.Session.AuthenticatedUserIdentity.Name);
                    if (user != null) {
                        // Check if the authenticated user is allowed to send as the envelope MAIL FROM address.
                        var result = SendAsPolicy.Evaluate(eArgs.MailFrom.Mailbox, user.Value.EmailAddresses, user.Value.AllowedSendAs, null);
                        if (!result.IsAllowed) {
                            eArgs.Response = result.DenialResponse!;

                            return;
                        }

                        // Check user daily send qouta limit.
                        if (user.Value.QuotaSentCountToday >= user.Value.QuotaMaxDailySends) {
                            eArgs.Response = new (552,new SMTP_t_EnhancedStatusCode(5,5,2),"Daily sending quota exceeded. Please try again tomorrow.");
                            
                            return;
                        }
                    }
                }
                
                // Check non-authetnticated users for PTR(HELO name match) -> SPF -> MX -> domain.
                if(_validateSender && !eArgs.Session.IsAuthenticated && !IsLanOrLocalIp(remoteIP)){
                    var result = await SMTP_SenderValidator.ValidateAsync(remoteIP,eArgs.Session.EhloHost!,eArgs.MailFrom.Mailbox);
                    eArgs.Response = result.Response;
                }
            }
            catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "Error during SMTP MAIL FROM validation for '{MailFrom}' from {RemoteIp} (Session: {SessionId}).",
                    eArgs.MailFrom?.Mailbox,
                    eArgs.Session.RemoteEndPoint?.Address,
                    eArgs.Session.ID);
            }
        }

        #endregion

        #region method RcptToAsync

        /// <summary>
        /// Raised when a <c>RCPT TO</c> command is received.
        /// Validates local recipient existence and mailbox storage quotas, or checks relay authorization for external recipients.
        /// </summary>
        /// <param name="eArgs">The <c>RCPT TO</c> event arguments containing the recipient address and SMTP response.</param>
        private async Task RcptToAsync(SMTP_e_RcptTo eArgs)
        {
            try {
                // Check if the email exists and is domain and user is enabled
                if (await _dbService.EmailAddressExistsAsync(eArgs.RcptTo.Mailbox)) {
                    var user = await _dbService.ResolveUserAsync(eArgs.RcptTo.Mailbox);
                    if (user == null) {
                        _logger.LogError("Error during RCPT TO validation.");
                        eArgs.Response = new SMTP_ServerResponse(451, null, "Temporary error during recipient validation.");

                        return;
                    }
                    // Check storage quota.
                    if (user.Value.QuotaCurrentUsageBytes > user.Value.QuotaMailboxSize) {
                        eArgs.Response = new SMTP_ServerResponse(550, new SMTP_t_EnhancedStatusCode(5,2,2), "Mailbox storage quota exceeded.");

                        return;
                    }

                    eArgs.Response = new SMTP_ServerResponse(250, new SMTP_t_EnhancedStatusCode(2,1,5), "Recipient ok.");
                }
                else{
                    // Check if the domain exists and is enabled
                    if (await _dbService.DomainExistsAsync(eArgs.RcptTo.Domain)) {
                        eArgs.Response = new SMTP_ServerResponse(550, new SMTP_t_EnhancedStatusCode(5,1,1), "User unknown.");
                    }
                    else if (eArgs.Session.IsAuthenticated) {
                        eArgs.Response = new SMTP_ServerResponse(250, new SMTP_t_EnhancedStatusCode(2,1,5), "Recipient ok.");
                    }
                    else {
                        eArgs.Response = new SMTP_ServerResponse(550,new SMTP_t_EnhancedStatusCode(5,7,1), "Relay access denied.");
                    }
                }
            }
            catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "Error during SMTP RCPT TO validation for '{RcptTo}' from {RemoteIp} (Session: {SessionId}).",
                    eArgs.RcptTo?.Mailbox,
                    eArgs.Session.RemoteEndPoint?.Address,
                    eArgs.Session.ID);
                eArgs.Response = new SMTP_ServerResponse(451, null, "Temporary error during recipient validation.");
            }
        }

        #endregion

        #region method MessageStoringBeginAsync

        /// <summary>
        /// Raised when a <c>DATA</c> or <c>BDAT</c> command requires a stream to buffer the incoming message.
        /// </summary>
        /// <param name="eArgs">The event arguments used to assign the destination temporary message stream.</param>
        private Task MessageStoringBeginAsync(SMTP_e_MessageStoringBegin eArgs)
        {
            try {
                // Create a temporary file stream in the server-wide temporary directory
                eArgs.StoreStream = _mailStoreService.CreateTempMessageStream();
            }
            catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "Error creating temporary message stream from {RemoteIp} (Session: {SessionId}).",
                    eArgs.Session.RemoteEndPoint?.Address,
                    eArgs.Session.ID);
            }

            return Task.CompletedTask;
        }

        #endregion

        #region method MessageStoringCancelAsync

        /// <summary>
        /// Raised when message transfer is aborted due to client disconnect, read error, or session inactivity timeout.
        /// </summary>
        /// <param name="eArgs">The event arguments containing the temporary message stream to dispose.</param>
        private async Task MessageStoringCancelAsync(SMTP_e_MessageStoringCancel eArgs)
        {
            try{
                // Release stream.
                await eArgs.Stream.DisposeAsync();
            }
            catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "Error disposing temporary message stream from {RemoteIp} (Session: {SessionId}).",
                    eArgs.Session.RemoteEndPoint?.Address,
                    eArgs.Session.ID);
            }
        }

        #endregion

        #region method MessageStoringCompleteAsync

        /// <summary>
        /// Raised when an incoming message has been completely received by the server.
        /// Parses required RFC 5322 headers, verifies optional DKIM signatures, and delivers the message to local mailboxes or the outbound relay queue.
        /// </summary>
        /// <param name="eArgs">The event arguments containing the stored message stream, session state, and SMTP response.</param>
        private async Task MessageStoringCompleteAsync(SMTP_e_MessageStoringComplete eArgs)
        {
            Mail_Message message;
            try {
                eArgs.Stream.Position = 0;
                message = Mail_Message.ParseFromStream(eArgs.Stream,true,false);
                eArgs.Stream.Position = 0;

                // Per RFC 5322 §3.6 (Internet Message Format), only From and Date fields are strictly required
                if(message.From == null) {
                    eArgs.Response = new (550, new (5, 6, 0), "Message rejected: missing required 'From' header");
                    return;
                }
                if(message.Date == null) {
                    eArgs.Response = new (550, new (5, 6, 0), "Message rejected: missing required 'Date' header");
                    return;
                }
            }
            catch {
                eArgs.Response = new (550, new (5, 6, 0), "Malformed message format or invalid headers.");
                return;
            }

            try {
                var from = eArgs.Session.From!;                        

                if(_validateDkimSignature && message.Header.Contains("DKIM-Signature")) {
                    var result = await DkimService.VerifySignatureAsync(eArgs.Stream);
                    if(!result.IsValid) {
                        eArgs.Response = new (550, new (5, 7, 20), "DKIM signature verification failed.");
                        return;
                    }
                }

                foreach(var recipient in eArgs.Session.To) {
                    UserInfo? user = await _dbService.ResolveUserAsync(recipient.Mailbox);
                    // Local user
                    if (user != null) {
                        await _mailStoreService.StoreMessageAsync(
                            user.Value.UserName, 
                            "Inbox", 
                            eArgs.Stream, 
                            [], 
                            DateTime.Now,
                            message);
                    }
                    // External recipient (relay)
                    else {
                        await _relayServer.StoreMessageAsync(
                            from.Mailbox,
                            recipient.Mailbox,
                            from.SmtpUtf8,
                            eArgs.Stream,
                            from.ENVID,
                            recipient.ORCPT,
                            from.RET,
                            recipient.Notify
                        );
                    }
                }

                if (eArgs.Session.AuthenticatedUserIdentity != null) {
                    await _dbService.IncrementDailySendCountAsync(eArgs.Session.AuthenticatedUserIdentity.Name);
                }
            }
            catch (Exception ex) {
                _logger.LogError(
                    ex,
                    "Error processing stored message from {RemoteIp} (Session: {SessionId}).",
                    eArgs.Session.RemoteEndPoint?.Address,
                    eArgs.Session.ID);
                eArgs.Response = new (554, new (5,7,1), "Transaction failed: " + ex.Message);
            }
            finally{
                await eArgs.Stream.DisposeAsync();
            }
        }

        #endregion

        #endregion


        #region method StartAsync

        /// <summary>
        /// Applies current <c>SmtpServer</c> configuration settings, initializes TLS listen endpoints, and starts the SMTP server.
        /// </summary>
        /// <param name="cancellationToken">A token that signals when the start operation should be canceled.</param>
        /// <returns>A completed <see cref="Task"/> once the SMTP server has been configured and started.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_smtpServer.IsRunning) {
                _smtpServer.Stop();
            }

            var settings = _configuration.GetSection("SmtpServer").Get<SmtpServerSettings>() ?? new();

            if (!settings.Enabled) {
                _logger.LogInformation("SMTP server is disabled in configuration.");
                return Task.CompletedTask;
            }

            _validateSender = settings.EnableSenderValidation;
            _validateDkimSignature = settings.EnableDkimValidation;
           
            var smtpEndpoints = new List<TCP_ServerEndpoint>();
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
                    
                    smtpEndpoints.Add(new TCP_ServerEndpoint(
                        hostName,
                        listenEp.IP,
                        listenEp.Port,
                        tlsMode,
                        cert
                    ));

                    _logger.LogInformation(
                        "Configured SMTP endpoint {HostName} on {IP}:{Port} (TLS: {TlsMode})",
                        listenEp.HostName,
                        listenEp.IP.ToString(),
                        listenEp.Port,
                        listenEp.TlsMode);
                }
                catch (Exception x) {
                    // Capture the raw configuration values from this specific child node for logging
                    var safeLogPayload = new {
                        HostName = listenEp.HostName,
                        IP = listenEp.IP,
                        Port = listenEp.Port,
                        TlsMode = listenEp.TlsMode,
                        CertPassword = string.IsNullOrEmpty(listenEp.CertPassword) ? "[NOT SET]" : "[REDACTED]"
                    };

                    _logger.LogError(x, 
                        "CRITICAL: Failed to initialize SMTP listener endpoint. " +
                        "Offending Config: {EndpointJson} | Reason: {Error}", 
                        JsonSerializer.Serialize(safeLogPayload), x.Message);
                }
            }
            
            _smtpServer.SessionIdleTimeout = settings.SessionIdleTimeout;
            _smtpServer.MaxConnections = settings.MaxConnections;
            _smtpServer.MaxConnectionsPerIP = settings.MaxConnectionsPerIP;
            _smtpServer.MaxMessageSize = (int)Math.Min(settings.MaxMessageSize, int.MaxValue);
            _smtpServer.ListenEndpoints = smtpEndpoints.ToArray();
            _smtpServer.Start();

            _logger.LogInformation("SMTP server started.");

            return Task.CompletedTask;
        }

        #endregion

        #region method StopAsync

        /// <summary>
        /// Stops the SMTP server listener while allowing active sessions to finish gracefully.
        /// </summary>
        /// <param name="cancellationToken">A token that signals when the stop operation should be canceled.</param>
        /// <returns>A completed <see cref="Task"/> once the SMTP server listener has stopped.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_smtpServer.IsRunning) {
                _smtpServer.Stop();
                _logger.LogInformation("SMTP server stopped.");
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
                foreach(var session in _smtpServer!.Sessions.ToArray()) {
                    try {
                        retValue.Add(new SessionsController.SessionDto(
                            session.ID,
                            "SMTP",
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
                _smtpServer!.Sessions[id].Disconnect();
            }
            catch {
            }
        }

        #endregion

        
        #region static method IsLanOrLocalIp

        /// <summary>
        /// Determines whether the specified IP address belongs to a local loopback
        /// or a private local area network (LAN) range (such as RFC 1918 private blocks or link-local addresses).
        /// </summary>
        /// <param name="ip">The <see cref="IPAddress"/> to evaluate.</param>
        /// <returns>
        /// <c>true</c> if the IP address is a loopback, private IPv4, or local IPv6 address; otherwise, <c>false</c>.
        /// </returns>
        private static bool IsLanOrLocalIp(IPAddress ip)
        {
            if (ip == null) return false;

            // Handle IPv4 mapped to IPv6 (e.g., ::ffff:127.0.0.1)
            if(ip.IsIPv4MappedToIPv6){
                ip = ip.MapToIPv4();
            }

            // Loopback (127.0.0.1 / ::1)
            if(IPAddress.IsLoopback(ip)){
                return true;
            }

            byte[] bytes = ip.GetAddressBytes();

            // IPv4 Private Ranges (RFC 1918 & Link-Local)
            if(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork){
                // 10.0.0.0/8
                if (bytes[0] == 10) return true;
        
                // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
                if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31)) return true;
        
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
        
                // 169.254.0.0/16 (Link-Local / APIPA)
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
            // IPv6 Private / Local Ranges
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6){
                // Unique Local Addresses (fc00::/7)
                if ((bytes[0] & 0xfe) == 0xfc) return true;
        
                // Link-Local Addresses (fe80::/10)
                if ((bytes[0] == 0xfe) && ((bytes[1] & 0xc0) == 0x80)) return true;
            }

            return false;
        }

        #endregion
    }
}
