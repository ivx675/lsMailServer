using LumiSoft.Net.AUTH;
using LumiSoft.Net.IMAP;
using LumiSoft.Net.IMAP.Server;
using LumiSoft.Net.Log;
using LumiSoft.Net.Mail;
using LumiSoft.Net.POP3;
using LumiSoft.Net.POP3.Server;
using LumiSoft.Net.SMTP;
using LumiSoft.Net.SMTP.Server;
using LumiSoft.Net.TCP;
using lsMailServer.RestApi;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace lsMailServer.Services 
{
    /// <summary>
    /// A hosted background service responsible for initializing, starting, and gracefully shutting down 
    /// the self-hosted mail server protocols (SMTP, POP3, and IMAP) based on dynamic configuration endpoints.
    /// </summary>
    public class MailServerHostedService : IHostedService
    {
        private readonly ILogger<MailServerHostedService> _logger;
        private readonly IConfiguration _configuration;
        private readonly DatabaseService _dbService;
        private readonly MailStoreService _mailStoreService;

        private SMTP_Server? _smtpServer;
        private POP3_Server? _pop3Server;
        private IMAP_Server? _imapServer;
        private SmtpRelayService _relayServer;

        /// <summary>
        /// Initializes a new instance of the <see cref="MailServerHostedService"/> class with required 
        /// infrastructure, configuration, database, mailstore, and SMTP relay dependencies.
        /// </summary>
        /// <param name="logger">The logger instance used for tracking background service lifecycle events, requests, and errors.</param>
        /// <param name="configuration">The application configuration provider used to read server endpoints and security settings.</param>
        /// <param name="dbService">The database service handling user metadata, quotas, and system-level database operations.</param>
        /// <param name="mailStoreService">The mailstore service responsible for mailbox folder operations and message stream management.</param>
        /// <param name="relayService">The SMTP relay service responsible for queuing and delivering outbound messages to remote mail servers.</param>
        public MailServerHostedService(
            ILogger<MailServerHostedService> logger,
            IConfiguration configuration,
            DatabaseService dbService,
            MailStoreService mailStoreService,
            SmtpRelayService relayService)
        {
            _logger = logger;
            _configuration = configuration;
            _dbService = dbService;
            _mailStoreService = mailStoreService;
            _relayServer = relayService;
        }


        #region Event Handlers

        #region SmtpServer_SessionCreatedAsync

        private async Task SmtpServer_SessionCreatedAsync(TCP_Server_e_SessionCreated<SMTP_Session> e)
        {
            try{
                // Raised when error occurs in session.
                e.Session.ErrorAsync += async (eArgsError) => {
                    _logger.LogError(eArgsError.Exception, "SMTP session error.");
                };

                // Add SASL plain authentication.
                var sasPlain = new AUTH_SASL_ServerMechanism_Plain(true);
                sasPlain.Authenticate += (s,eArgs) => {
                    try {
                        eArgs.IsAuthenticated = _dbService.AuthenticateUser(eArgs.UserName, eArgs.Password,out _);
                    }
                    catch (Exception ex) {
                        _logger.LogError(ex, "Error during SMTP authentication.");
                    }
                };
                e.Session.Authentications.Add(sasPlain.Name,sasPlain);

                // Raised when a new SMTP session is established, before the greeting banner is sent.
                e.Session.StartedAsync += async (eArgsStarted) => {
                    // Reject session.
                    //eArgsStarted.Response = new SMTP_ServerResponse(554,null,"Session rejected.");
                };

                // HELO command received.
                e.Session.HeloAsync += async (eArgsHelo) => {
                    //eArgsHelo.Response = new SMTP_ServerResponse(502,null,"HELO Disabled.");
                };

                // EHLO command received.
                e.Session.EhloAsync += async (eArgsEhlo) => {
                    // Reject EHLO.
                    //eArgsEhlo.Response = new SMTP_ServerResponse(550,null,"EHLO rejected by policy.");
                };

                #region MailFromAsync

                // MAIL FROM: command received.
                e.Session.MailFromAsync += async (eArgs) => {
                    IPAddress remoteIP = eArgs.Session.RemoteEndPoint.Address;

                    // Check user daily send qouta limit.
                    if (eArgs.Session.AuthenticatedUserIdentity != null) {
                        var user = _dbService.GetUser(eArgs.Session.AuthenticatedUserIdentity.Name);
                        if (user != null) {
                            if (user.Value.QuotaSentCountToday >= user.Value.QuotaMaxDailySends) {
                                eArgs.Response = new (552,new SMTP_t_EnhancedStatusCode(5,5,2),"Daily sending quota exceeded. Please try again tomorrow.");

                                return;
                            }
                        }
                    }
                    /*
                    // Check non-authetnticated users for PTR(HELO name match) -> SPF -> MX -> domain.
                    if(!eArgsMailFrom.Session.IsAuthenticated && !IsLanOrLocalIp(remoteIP)){
                        try{
                            // Run validation
                            await SMTP_SenderValidator.ValidateAsync(remoteIP,eArgsMailFrom.Session.EhloHost!,eArgsMailFrom.MailFrom.Mailbox);
            
                            // If no exception is thrown, validation succeeded (SMTP 250 OK)
                            eArgsMailFrom.Response = new (250,new SMTP_t_EnhancedStatusCode(2,1,0),"Sender validation PASSED successfully.");
                        }
                        catch (SMTP_ValidatorException ex){
                            // Map to professional SMTP response codes based on IsTemporary flag
                            if(ex.IsTemporary){
                                eArgsMailFrom.Response = new (451,new SMTP_t_EnhancedStatusCode(4,3,0),$"Temporary Error (Try Again Later): {ex.ErrorText}");
                            }
                            else{
                                eArgsMailFrom.Response = new (550,new SMTP_t_EnhancedStatusCode(5,7,1),$"Permanent Rejection (Policy Violation): {ex.ErrorText}");
                            }
                        }
                        catch (Exception ex){
                            eArgsMailFrom.Response = new (554,new SMTP_t_EnhancedStatusCode(5,3,0),$"Unexpected error: {ex.Message}");
                        }
                    }*/
                };

                #endregion

                #region RcptToAsync

                // RCPT TO: command received.
                e.Session.RcptToAsync += async (eArgs) => {
                    try {
                        // Check if the email exists and is domain and user is enabled
                        if (_dbService.EmailAddressExists(eArgs.RcptTo.Mailbox)) {
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
                            if (_dbService.DomainExists(eArgs.RcptTo.Domain)) {
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
                        _logger.LogError(ex, "Error during RCPT TO validation.");
                        eArgs.Response = new SMTP_ServerResponse(451, null, "Temporary error during recipient validation.");
                    }
                };

                #endregion

                #region MessageStoringBeginAsync MessageStoringCancelAsync MessageStoringCompleteAsync

                // Raised when DATA or BDAT needs stream where to store message
                e.Session.MessageStoringBeginAsync += async (eArgsStoringBegin) => {
                    try {
                        // Create a temporary file stream in the server-wide temporary directory
                        eArgsStoringBegin.StoreStream = _mailStoreService.CreateTempMessageStream();
                    }
                    catch (Exception ex) {
                        _logger.LogError(ex, "Error creating temporary message stream.");
                    }
                };

                // Raised when: Client disconnects,message reading errors or session inactivity timeout.
                e.Session.MessageStoringCancelAsync += async (eArgsStoringCancel) => {
                    // Release stream.
                    eArgsStoringCancel.Stream.Dispose();
                };

                // Raised when message successfully stored by server.
                e.Session.MessageStoringCompleteAsync += async (eArgs) => {
                    try {
                        var from = eArgs.Session.From!;
                        foreach(var recipient in eArgs.Session.To) {
                            UserInfo? user = await _dbService.ResolveUserAsync(recipient.Mailbox);
                            // Local user
                            if (user != null) {
                                await _mailStoreService.StoreMessageAsync(user.Value.UserName, "Inbox", eArgs.Stream, [], DateTime.Now);
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
                            _dbService.IncrementDailySendCount(eArgs.Session.AuthenticatedUserIdentity.Name);
                        }
                    }
                    catch (Exception ex) {
                        _logger.LogError(ex, "Error processing stored message.");
                        eArgs.Response = new SMTP_ServerResponse(554, new SMTP_t_EnhancedStatusCode(5,7,1), "Transaction failed: " + ex.Message);
                    }

                    eArgs.Stream.Dispose();
                };

                #endregion
            }
            catch (Exception ex) {
                _logger.LogError(ex,"Error in SMTP session creation.");
            }
        }

        #endregion

        #region method Pop3Server_SessionCreated

        private async Task Pop3Server_SessionCreated(TCP_Server_e_SessionCreated<POP3_Session> e)
        {
            // Raised when error occurs in session.
            e.Session.ErrorAsync += async (eArgsError) => {
                _logger.LogError(eArgsError.Exception, "POP3 session error.");
            };

            // Add SASL plain authentication.
            var sasPlain = new AUTH_SASL_ServerMechanism_Plain(true);
            sasPlain.Authenticate += (s,eArgs) => { 
                try {
                    eArgs.IsAuthenticated = _dbService.AuthenticateUser(eArgs.UserName, eArgs.Password,out _);
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error during POP3 authentication.");
                }
            };
            e.Session.Authentications.Add(sasPlain.Name,sasPlain);

            #region AuthUserPassAsync

            // Raised when USER/PASS authentication is received.
            e.Session.AuthUserPassAsync += async (eArgsAuthUserPass) => {
                try {
                    if (_dbService.AuthenticateUser(eArgsAuthUserPass.UserName, eArgsAuthUserPass.Password,out _)) {
                        eArgsAuthUserPass.Response = new POP3_ServerResponse("+OK",null,"Authenticated successfully.");
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error during POP3 authentication.");
                }
            };

            #endregion

            #region StartedAsync

            // Raised when a new POP3 session is established, before the greeting banner is sent.
            e.Session.StartedAsync += async (eArgsStarted) => {
            };

            #endregion

            #region LoadMessagesInfoAsync

            // Raised when user authentication is completed and the session needs to load the messages info for the authenticated user.
            e.Session.LoadMessagesInfoAsync += async (eArgs) => {
                try {
                    // Load message summaries for the authenticated user and populate eArgs.Messages
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
            };

            #endregion

            #region GetMessageStreamAsync

            // Raised when TOP or RETR command is received, the session needs to provide a stream
            // containing the message data for the requested message.
            e.Session.GetMessageStreamAsync += async (eArgs) => {
                try {
                    // Retrieve the message stream for the requested message UID
                    var messageStream = _mailStoreService.GetMessageStream(eArgs.Session.AuthenticatedUserIdentity!.Name, "Inbox", long.Parse(eArgs.Message.UID), true);
                    eArgs.MessageStream = messageStream;
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error retrieving message stream for UID: {MessageUid}", eArgs.Message.UID);
                }
            };

            #endregion

            #region DeleteMessagesAsync

            // Raised when POP3 session is about to be closed, messages marked for deletion must be deleted.
            e.Session.DeleteMessagesAsync += async (eArgsDeleteMessages) => {
                try {
                    // Delete messages marked for deletion
                    foreach (var msg in eArgsDeleteMessages.Messages) {
                        await _mailStoreService.DeleteMessageAsync(e.Session.AuthenticatedUserIdentity!.Name, "Inbox", long.Parse(msg.UID));
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error deleting messages for user: {UserName}", eArgsDeleteMessages.Session.AuthenticatedUserIdentity?.Name);
                }
            };

            #endregion
        }

        #endregion

        #region method ImapServer_SessionCreated

        private async Task ImapServer_SessionCreated(TCP_Server_e_SessionCreated<IMAP_Session> e)
        {
            e.Session.Capabilities.Clear();
            e.Session.Capabilities.AddRange([
                "IMAP4rev2",
                "NAMESPACE",
                "QUOTA",
              //"ACL",
                "IDLE",
                "ENABLE",
                "UTF8=ACCEPT",
                "SASL-IR",
                "UIDPLUS"
            ]);

            // Raised when error occurs in session.
            e.Session.ErrorAsync += async (eArgsError) => {
                _logger.LogError(eArgsError.Exception, "IMAP session error.");
            };

            // Add SASL plain authentication.
            var sasPlain = new AUTH_SASL_ServerMechanism_Plain(true);
            sasPlain.Authenticate += (s,eArgs) => { 
                try {
                    eArgs.IsAuthenticated = _dbService.AuthenticateUser(eArgs.UserName, eArgs.Password,out _);
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error during IMAP authentication.");
                }
            };
            e.Session.Authentications.Add(sasPlain.Name,sasPlain);
            
            // Raised when LOGIN authentication is received.
            e.Session.AuthLoginAsync += async (eArgs) => {
                try {
                    if (_dbService.AuthenticateUser(eArgs.UserName, eArgs.Password,out _)) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Authenticated successfully.");
                    }
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error during IMAP authentication.");
                }
            };

            // Raised when a new IMAP session is established, before the greeting banner is sent.
            e.Session.StartedAsync += async (eArgsStarted) => {
            };

            #region NamespaceAsync

            // Raised when a NAMESPACE command is received.
            e.Session.NamespaceAsync += async (eArgsNamespace) => {
                // Per IMAP rev2 RFC 9051 NAMESPACE is standard IMAP command.
                // By default RFC-compliant namespace response is provided and represents
                // a single personal namespace with an empty prefix and '/' hierarchy
                // delimiter, and no other-users or shared namespaces.

                // You can override default response here.
                //eArgsNamespace.NamespaceResponse = ...
            };

            #endregion

            #region ListAsync

            // Raised when a LIST command is received.
            e.Session.ListAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    string[] folders = _mailStoreService.GetFolders(userName);

                    foreach (string folder in folders) {
                        if (eArgs.IsMatch(folder, '/')) {
                            // Determine if any other folder is a subfolder (e.g., starts with "FolderName/")
                            string prefix = folder + '/';
                            bool hasChildren = folders.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                            string[] flags = hasChildren ? new[] { "\\HasChildren" } : new[] { "\\HasNoChildren" };

                            eArgs.Folders.Add(new IMAP_r_u_List(folder, '/', flags));
                        }
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP LIST command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region CreateAsync

            // Raised when a CREATE command is received.
            e.Session.CreateAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    string folder   = eArgs.Folder;
                    if (_mailStoreService.FolderExists(userName,folder)) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","[ALREADYEXISTS] Folder already exists.");
                    }
                    else {                        
                        _mailStoreService.CreateFolder(userName,folder);
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder created.");
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing CREATE command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region DeleteAsync

            // Raised when a DELETE command is received.
            e.Session.DeleteAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    string folder   = eArgs.Folder;
                    if (string.Equals("inbox",folder,StringComparison.OrdinalIgnoreCase)) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","Cannot delete INBOX.");
                    }
                    else if (!_mailStoreService.FolderExists(userName,folder)) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","[NONEXISTENT] Folder does not exist.");
                    }
                    else {                        
                        _mailStoreService.DeleteFolder(userName,folder);
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder deleted.");
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing DELETE command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region RenameAsync

            // Raised when a RENAME command is received.
            e.Session.RenameAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    string folder   = eArgs.CurrentFolder;
                    string newFolder = eArgs.NewFolder;
                    if (string.Equals("inbox",folder,StringComparison.OrdinalIgnoreCase)) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","Cannot rename INBOX.");
                        return;
                    }
                    if (!_mailStoreService.FolderExists(userName,folder)) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","[NONEXISTENT] Folder does not exist.");
                        return;
                    }
                    if (_mailStoreService.FolderExists(userName,newFolder)) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","[ALREADYEXISTS] Destination folder already exists.");
                        return;
                    }
                    
                    _mailStoreService.RenameFolder(userName,folder,newFolder);
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder renamed.");
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing RENAME command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region LSubAsync

            // Raised when a LSUB command is received.
            e.Session.LSubAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
        
                    // 1. Get explicitly subscribed folder paths from database (relative, normalized)
                    HashSet<string> subscribedFolders = new HashSet<string>(
                        _mailStoreService.GetSubscribedFolders(userName).Select(f => f.Replace('\\', '/')),
                        StringComparer.OrdinalIgnoreCase
                    );

                    // 2. Generate all target folders (subscribed + missing parent branches derived from paths)
                    var targetFolders = new HashSet<string>(subscribedFolders, StringComparer.OrdinalIgnoreCase);
        
                    foreach (string sub in subscribedFolders) {
                        string current = sub;
                        while (true) {
                            int lastSlash = current.LastIndexOf('/');
                            if (lastSlash < 0) break;
                
                            current = current.Substring(0, lastSlash);
                            if (!string.IsNullOrEmpty(current)) {
                                targetFolders.Add(current);
                            }
                        }
                    }

                    // 3. Sort alphabetically/hierarchically so parents always precede children
                    var sortedFolders = targetFolders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

                    foreach (string folderPath in sortedFolders) {
                        // Check if this folder matches the client's wildcard pattern (e.g. "*", "INBOX*")
                        if (eArgs.IsMatch(folderPath, '/')) {
                            bool isSubscribed = subscribedFolders.Contains(folderPath);

                            // Determine if any other target folder starts with this folder's path + "/"
                            string prefix = folderPath + '/';
                            bool hasChildren = targetFolders.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                            // Build flags list
                            var flags = new List<string>();

                            // RFC rule: Unsubscribed parent placeholders MUST have \Noselect
                            if (!isSubscribed) {
                                flags.Add("\\Noselect");
                            }

                            if (hasChildren) {
                                flags.Add("\\HasChildren");
                            } else {
                                flags.Add("\\HasNoChildren");
                            }

                            eArgs.Folders.Add(new IMAP_r_u_LSub(folderPath, '/', flags.ToArray()));
                        }
                    }

                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "OK", "LSUB completed.");
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP LSUB command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region SubscribeAsync

            // Raised when a SUBSCRIBE command is received.
            e.Session.SubscribeAsync += async (eArgs) => {
                try {
                    _mailStoreService.SubscribeFolder(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder);
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder subscribed.");
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing SUBSCRIBE command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region UnsubscribeAsync

            // Raised when a UNSUBSCRIBE command is received.
            e.Session.UnsubscribeAsync += async (eArgs) => {
                try {
                    _mailStoreService.UnsubscribeFolder(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder);
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder unsubscribed.");
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing UNSUBSCRIBE command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region StatusAsync

            // Raised when a STATUS command is received.
            e.Session.StatusAsync += async (eArgs) => {
                var folderInfo = await _mailStoreService.GetFolderInfoAsync(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder);
                if(folderInfo != null){
                    int unseenCount = 0;
                    int deletedCount = 0;
                    long totalSize = 0;
                    long lastUid = 0;
                    foreach(var m in folderInfo.Messages){
                        totalSize += m.MessageSize;
                        lastUid = m.MessageUid;

                        if(!m.MessageFlags.Contains("\\Seen", StringComparer.OrdinalIgnoreCase)){
                            unseenCount++;
                        }

                        if(m.MessageFlags.Contains("\\Deleted", StringComparer.OrdinalIgnoreCase)){
                            deletedCount++;
                        }
                    }

                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","STATUS completed.");
                    eArgs.UidValidity = folderInfo.UidValidity;
                    eArgs.UidNext = lastUid + 1;
                    eArgs.MessagesCount = folderInfo.Messages.Count;
                    eArgs.UnseenCount = unseenCount;
                    eArgs.DeletedCount = deletedCount;                    
                    eArgs.Size = totalSize;                    
                }
            };

            #endregion

            #region SelectAsync

            // Raised when a SELECT command is received.
            e.Session.SelectAsync += async (eArgs) => {
                try {
                    var folderInfo = await _mailStoreService.GetFolderInfoAsync(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder);
                    if(folderInfo != null){
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK",new IMAP_t_orc_ReadWrite(),"SELECT completed.");
                        eArgs.Flags = ["\\Answered","\\Flagged","\\Deleted","\\Seen","\\Draft"];
                        eArgs.PermanentFlags = ["\\Answered","\\Flagged","\\Deleted","\\Seen","\\Draft"];
                        eArgs.UidValidity = folderInfo.UidValidity;
                        eArgs.Version = folderInfo.Version;
                    
                        foreach(var message in folderInfo.Messages){
                            eArgs.MessagesInfo.Add(new IMAP_MessageInfo(message.MessageUid.ToString(),message.MessageUid,message.MessageFlags,message.MessageSize,message.MessageInternalDate));
                        }
                    }
                    else{
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","No such mailbox.");
                    }
                }
                catch (Exception x) {
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO",$"Select failure. Error:{x.Message}.");
                }
            };

            #endregion

            #region ExamineAsync

            // Raised when a EXAMINE command is received.
            e.Session.ExamineAsync += async (eArgs) => {
                try {
                    var folderInfo = await _mailStoreService.GetFolderInfoAsync(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder);
                    if(folderInfo != null){
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK",new IMAP_t_orc_ReadOnly(),"EXAMINE completed.");
                        eArgs.Flags = ["\\Answered","\\Flagged","\\Deleted","\\Seen","\\Draft"];
                        eArgs.UidValidity = folderInfo.UidValidity;
                        eArgs.Version = folderInfo.Version;
                    
                        foreach(var message in folderInfo.Messages){
                            eArgs.MessagesInfo.Add(new IMAP_MessageInfo(message.MessageUid.ToString(),message.MessageUid,message.MessageFlags,message.MessageSize,message.MessageInternalDate));
                        }
                    }
                    else{
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","No such mailbox.");
                    }
                }
                catch (Exception x) {
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO",$"Examine failure. Error:{x.Message}.");
                }
            };

            #endregion

            #region FolderSyncAsync

            // Raised when IMAP session wants to know if folder messages count or existing messages flags has changed.
            e.Session.FolderSyncAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;

                    var folderInfo = await _mailStoreService.GetFolderInfoAsync(userName,eArgs.Folder, loadMessagesInfo: false);
                    if(folderInfo == null){
                        return; // Folder does not exist any more.
                    }

                    if(folderInfo.Version != eArgs.KnownVersion) {
                        folderInfo = await _mailStoreService.GetFolderInfoAsync(userName,eArgs.Folder, loadMessagesInfo: true);
                        if (folderInfo == null) {
                            return; // Folder does not exist any more.
                        }

                        var uids = new List<long>();
                        var changedMessages = new List<IMAP_MessageInfo>();
                        foreach(var message in folderInfo.Messages){
                            uids.Add(message.MessageUid);
                            if(message.Version > eArgs.KnownVersion){
                                changedMessages.Add(new IMAP_MessageInfo(message.MessageUid.ToString(),message.MessageUid,message.MessageFlags,message.MessageSize,message.MessageInternalDate));
                            }
                        }
                    
                        eArgs.Uids = uids.ToArray();
                        eArgs.Messages = changedMessages.ToArray();
                    }
                
                    eArgs.Version = folderInfo.Version;
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP FolderSyncAsync.");
                }
            };

            #endregion

            #region AppendBeginAsync AppendCancelAsync AppendCompleteAsync

            // Raised when a APPEND command starts processing.
            e.Session.AppendBeginAsync += async (eArgs) => {
                try {
                    eArgs.Stream = _mailStoreService.CreateTempMessageStream();
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP APPEND command.");
                }
            };

            // Raised when a APPEND command cancels(Session timeout,client closes connection, ...) processing.
            e.Session.AppendCancelAsync += async (eArgs) => {
                try {
                    await eArgs.Stream.DisposeAsync();
                }
                catch {
                }
            };

            // Raised when a APPEND command has completed processing.
            e.Session.AppendCompleteAsync += async (eArgs) => {
                try {
                    var user = _dbService.GetUser(eArgs.Session.AuthenticatedUserIdentity!.Name);
                    if (user == null) {
                        _logger.LogError("Unexpected error processing IMAP APPEND command.");
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");

                        return;
                    }
                    if ((user.Value.QuotaCurrentUsageBytes + eArgs.Size) > user.Value.QuotaMailboxSize) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","[QUOTA] Mailbox storage quota exceeded.");

                        return;
                    }

                    eArgs.Stream.Position = 0;
                    await _mailStoreService.StoreMessageAsync(
                        eArgs.Session.AuthenticatedUserIdentity!.Name,
                        eArgs.Folder,
                        eArgs.Stream,eArgs.Flags,
                        eArgs.InternalDate == null ? DateTime.Now : eArgs.InternalDate.Value
                        );                    
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP APPEND command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
                finally {
                    await eArgs.Stream.DisposeAsync();
                }
            };

            #endregion

            #region GetQuotaRootAsync

            // Raised when a GETQUOTAROOT command is received.
            e.Session.GetQuotaRootAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    if (_mailStoreService.FolderExists(userName,eArgs.Folder)) {
                        var userInfo = _dbService.GetUser(userName);
                        if (userInfo != null) {
                            // IMAP quota expects KB, not bytes.
                            long quotaLimitInKb = userInfo.Value.QuotaMailboxSize / 1024;
                            long usageInKb = userInfo.Value.QuotaCurrentUsageBytes / 1024;

                            eArgs.QuotaRootResponse = new IMAP_r_u_QuotaRoot(eArgs.Folder,[""]);
                            eArgs.QuotaResponses.Add(new IMAP_r_u_Quota("",[new IMAP_t_Quota_Entry("STORAGE",usageInKb,quotaLimitInKb)]));
                            eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","GETQUOTAROOT completed.");
                        }
                        else {
                            _logger.LogError("Unexpected error processing IMAP GETQUOTAROOT command.");
                            eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                        }                        
                    }
                    else {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","No such mailbox.");
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP GETQUOTAROOT command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region GetQuotaAsync

            // Raised when a GETQUOTA command is received.
            e.Session.GetQuotaAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    if (string.Equals("",eArgs.QuotaRoot,StringComparison.OrdinalIgnoreCase)) {
                        var userInfo = _dbService.GetUser(userName);
                        if (userInfo != null) {
                            // IMAP quota expects KB, not bytes.
                            long quotaLimitInKb = userInfo.Value.QuotaMailboxSize / 1024;
                            long usageInKb = userInfo.Value.QuotaCurrentUsageBytes / 1024;

                            eArgs.QuotaResponses.Add(new IMAP_r_u_Quota("",[new IMAP_t_Quota_Entry("STORAGE",usageInKb,quotaLimitInKb)]));
                            eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","GETQUOTA completed.");
                        }
                        else {
                            _logger.LogError("Unexpected error processing IMAP GETQUOTA command.");
                            eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                        }                        
                    }
                    else {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","No such quota root.");
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP GETQUOTA command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region GetAclAsync

            // Raised when a GETACL command is received.
            e.Session.GetAclAsync += async (eArgsGetAcl) => {
            };

            #endregion

            #region SetAclAsync

            // Raised when a SETACL command is received.
            e.Session.SetAclAsync += async (eArgsSetAcl) => {
            };

            #endregion

            #region DeleteAclAsync

            // Raised when a DELETEACL command is received.
            e.Session.DeleteAclAsync += async (eArgsDeleteAcl) => {
            };

            #endregion

            #region ListRightsAsync

            // Raised when a LISTRIGHTS command is received.
            e.Session.ListRightsAsync += async (eArgsListRights) => {
            };

            #endregion

            #region MyRightsAsync

            // Raised when a MYRIGHTS command is received.
            e.Session.MyRightsAsync += async (eArgsMyRights) => {
            };

            #endregion


            #region CloseAsync

            // Raised when a CLOSE command is received.
            e.Session.CloseAsync += async (eArgs) => {
                try {
                    if(!eArgs.IsReadOnly){
                        foreach(var message in eArgs.DeletedMessages){
                            await _mailStoreService.DeleteMessageAsync(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder,message.UID);
                        }
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP CLOSE command.");
                }
            };

            #endregion

            #region FetchAsync

            // Raised when a FETCH command is received.
            e.Session.FetchAsync += async (eArgs) => {
                try {
                    foreach(var message in eArgs.Messages) {
                        string                userName      = eArgs.Session.AuthenticatedUserIdentity!.Name;
                        string                folder        = eArgs.Session.SelectedFolderName!;
                        Stream?               messageStream = null;
                        IMAP_t_Envelope?      envelope      = null;
                        IMAP_t_BodyStructure? bodyStructure = null;
                        try{
                            if(eArgs.RequiresMessageStream){
                                messageStream = _mailStoreService.GetMessageStream(userName,folder,message.UID,eArgs.SetSeen);
                                // Message no longer exists.
                                if(messageStream == null) {
                                    continue;
                                }
                            }

                            if(eArgs.RequiresEnvelope || eArgs.RequiresBodyStructure){
                                var messageInfo = _mailStoreService.GetMessageInfo(userName,folder,message.UID);
                                // Message no longer exists.
                                if(messageInfo == null) {
                                    continue;
                                }

                                envelope = messageInfo.Value.Envelope;
                                bodyStructure = messageInfo.Value.BodyStructure;
                            }

                            // Pass data to IMAP session.
                            await eArgs.AddDataAsync(message,messageStream,envelope,bodyStructure);
                        }                    
                        finally{
                            messageStream?.Dispose();
                        }
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP FETCH command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region SearchAsync

            // Raised when a SEARCH or UID SEARCH command is received.
            e.Session.SearchAsync += async (eArgs) => {
                try {
                    eArgs.MatchedUids = _mailStoreService.Search(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder,eArgs.Criteria).ToArray();

                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","SEARCH completed.");
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP SEARCH command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region StoreAsync

            // Raised when a STORE or UID STORE command is received.
            e.Session.StoreAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    foreach (var message in eArgs.Messages) {
                        FlagStoreMode storeMode;
                        if (eArgs.FlagsSetType == IMAP_Flags_SetType.Add) {
                            storeMode = FlagStoreMode.Add;
                        }
                        else if (eArgs.FlagsSetType == IMAP_Flags_SetType.Remove) {
                            storeMode = FlagStoreMode.Remove;
                        }
                        else {
                            storeMode = FlagStoreMode.Replace;
                        }

                        _mailStoreService.StoreMessageFlags(userName,eArgs.Folder,message.UID,eArgs.Flags,storeMode);
                    }

                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","STORE completed.");
                }
                catch(Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP STORE command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region CopyAsync

            // Raised when a COPY or UID COPY command is received.
            e.Session.CopyAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;

                    var targetFolderInfo = await _mailStoreService.GetFolderInfoAsync(userName, eArgs.TargetFolder, false);
                    if (targetFolderInfo == null) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "[NONEXISTENT] Target folder does not exist.");
                        return;
                    }
                    long targetFolderUidValidity = targetFolderInfo.UidValidity;

                    long totalMessagesSize = 0;
                    foreach (var m in eArgs.Messages) {
                        totalMessagesSize += m.Size;
                    }
        
                    UserInfo? userInfo = _dbService.GetUser(userName);
                    if (userInfo == null) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "An unexpected server error occurred while processing your request.");
                        return;
                    }
        
                    long userFreeSpace = userInfo.Value.QuotaMailboxSize - userInfo.Value.QuotaCurrentUsageBytes;
                    if (totalMessagesSize > userFreeSpace) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "[OVERQUOTA] Storage quota exceeded. Cannot complete copy operation.");
                        return;
                    }

                    bool rollback = false;
                    var copiedUids = new List<long>();
                    var sourceUids = new List<long>();

                    foreach (var m in eArgs.Messages) {
                        try {
                            // Gracefully handle messages deleted concurrently by another session
                            long? newUid = await _mailStoreService.CopyMessageAsync(userName, eArgs.SourceFolder, m.UID, eArgs.TargetFolder);
                            if (newUid.HasValue) {
                                sourceUids.Add(m.UID);
                                copiedUids.Add(newUid.Value);
                            }
                        }
                        catch (Exception ex) {
                            _logger.LogWarning(ex, "Failed to copy message UID {Uid} during batch. Initiating rollback.", m.UID);
                            rollback = true;
                            break;
                        }
                    }

                    if (rollback) {
                        foreach (long uid in copiedUids) {
                            try {
                                await _mailStoreService.DeleteMessageAsync(userName, eArgs.TargetFolder, uid);
                            }
                            catch (Exception cleanupEx) {
                                _logger.LogError(cleanupEx, "Failed to roll back copied message UID {Uid} in target folder.", uid);
                            }
                        }

                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "Copy failed; operation aborted and rolled back.");
                        return;
                    }

                    // Generate COPYUID response tokens if we successfully copied items
                    if (copiedUids.Count > 0) {
                        var sourceSet = new IMAP_t_SeqSet(sourceUids.ToArray());
                        var destSet = new IMAP_t_SeqSet(copiedUids.ToArray());
                        
                        eArgs.Response = new IMAP_r_ServerStatus(
                            eArgs.Response.CommandTag,
                            "OK",
                            new IMAP_t_orc_CopyUid(targetFolderUidValidity,sourceSet,destSet),
                            "COPY completed."
                        );
                    } 
                    else {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "OK", "COPY completed.");
                    }
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP COPY command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "An unexpected server error occurred while processing your request.");
                }
            };

            #endregion

            #region MoveAsync

            // Raised when a MOVE or UID MOVE command is received.
            e.Session.MoveAsync += async (eArgs) => {
                try {
                    string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;

                    // 1. Pre-flight Quota & Target Folder Validation
                    long totalMessagesSize = eArgs.Messages.Sum(m => m.Size);
                    var userInfo = _dbService.GetUser(userName);
                    if (userInfo == null) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "Server error.");
                        return;
                    }

                    long userFreeSpace = userInfo.Value.QuotaMailboxSize - userInfo.Value.QuotaCurrentUsageBytes;
                    if (totalMessagesSize > userFreeSpace) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "[OVERQUOTA] Storage quota exceeded.");
                        return;
                    }

                    var targetFolderInfo = await _mailStoreService.GetFolderInfoAsync(userName, eArgs.TargetFolder, false);
                    if (targetFolderInfo == null) {
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "[NONEXISTENT] Target folder does not exist.");
                        return;
                    }
                    long targetFolderUidValidity = targetFolderInfo.UidValidity;

                    // 2. Execution Phase: Copy all messages to target first
                    bool rollback = false;
                    var copiedUids = new List<long>();
                    var sourceUids = new List<long>();

                    foreach (var m in eArgs.Messages) {
                        try {
                            long? newUid = await _mailStoreService.CopyMessageAsync(userName, eArgs.SourceFolder, m.UID, eArgs.TargetFolder);
                            if (newUid.HasValue) {
                                sourceUids.Add(m.UID);
                                copiedUids.Add(newUid.Value);
                            }
                        }
                        catch (Exception ex) {
                            _logger.LogWarning(ex, "MOVE failed during copy phase for UID {Uid}. Rolling back.", m.UID);
                            rollback = true;
                            break;
                        }
                    }

                    // If copy failed, roll back destination copies and return NO
                    if (rollback) {
                        foreach (long uid in copiedUids) {
                            try { await _mailStoreService.DeleteMessageAsync(userName, eArgs.TargetFolder, uid); } catch { }
                        }
                        eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "Move failed; operation aborted.");
                        return;
                    }

                    // 3. Cleanup Phase: Delete source messages
                    foreach (var m in eArgs.Messages) {
                        try {
                            await _mailStoreService.DeleteMessageAsync(userName, eArgs.SourceFolder, m.UID);
                        }
                        catch (Exception deleteEx) {
                            _logger.LogWarning(deleteEx, "Could not immediately delete source message UID {Uid} due to lock/error. Marking as \\Deleted fallback.", m.UID);
                
                            try {
                                // Fallback: Set \Deleted flag so standard expunge/cleaner handles it later
                                _mailStoreService.StoreMessageFlags(userName, eArgs.SourceFolder, m.UID, ["\\Deleted"], FlagStoreMode.Replace);
                            }
                            catch (Exception flagEx) {
                                _logger.LogError(flagEx, "Failed to apply fallback \\Deleted flag to source message UID {Uid}.", m.UID);
                            }
                        }
                    }

                    // 4. Success Response (MOVE uses COPYUID per RFC 6851)
                    var sourceSet = new IMAP_t_SeqSet(sourceUids.ToArray());
                    var destSet = new IMAP_t_SeqSet(copiedUids.ToArray());
                    
                    eArgs.Response = new IMAP_r_ServerStatus(
                        eArgs.Response.CommandTag,
                        "OK",
                        new IMAP_t_orc_CopyUid(targetFolderUidValidity,sourceSet,destSet),
                        "MOVE completed."
                    );
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP MOVE command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag, "NO", "An unexpected server error occurred.");
                }
            };

            #endregion

            #region ExpungeAsync

            // Raised when a EXPUNGE or UID EXPUNGE command is received.
            e.Session.ExpungeAsync += async (eArgs) => {
                try {
                    foreach(var message in eArgs.Messages){
                        await _mailStoreService.DeleteMessageAsync(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder,message.UID);
                    }

                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","EXPUNGE completed.");
                }
                catch (Exception x) {
                    _logger.LogError(x, "Unexpected error processing IMAP EXPUNGE command.");
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
                }
            };

            #endregion
        }

        #endregion

        #endregion


        #region method StartAsync

        /// <summary>
        /// Triggered when the application host is ready to start the mail server.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Mail Server instance from directory: {BaseDir}", AppContext.BaseDirectory);

            // 1. Verify database connection and schema on startup
            using var connection = _dbService.CreateConnection();
            _logger.LogInformation("SQLite database initialized and WAL mode active.");

            // 2. Initialize and start servers

            #region SMTP server

            var smtpEndpoints = new List<TCP_ServerEndpoint>();
            foreach (var child in _configuration.GetSection("SmtpServer:ListenEndpoints").GetChildren()) {
                try {
                    var hostName = child["HostName"] ?? System.Net.Dns.GetHostName();
                    var tlsMode = TCP_ServerTlsMode.Explicit;
                    if (child["TlsMode"] != null && string.Equals(child["TlsMode"],"Implicit",StringComparison.OrdinalIgnoreCase)) {
                        tlsMode = TCP_ServerTlsMode.Implicit;
                    }
                    var certPassword = child["CertPassword"] ?? "";
                    
                    smtpEndpoints.Add(new TCP_ServerEndpoint(
                        hostName,
                        IPAddress.Parse(child["IP"] ?? "0.0.0.0"),
                        int.Parse(child["Port"] ?? "25"),
                        tlsMode,
                        LoadOrCreateCert(certPassword,hostName)
                    ));

                    _logger.LogInformation("Successfully started SMTP listener on {Host}:{Port}", child["IP"], child["Port"]);
                }
                catch (Exception x) {
                    // Capture the raw configuration values from this specific child node for logging
                    var safeLogPayload = new {
                        HostName = child["HostName"],
                        IP = child["IP"],
                        Port = child["Port"],
                        TlsMode = child["TlsMode"],
                        CertPassword = string.IsNullOrEmpty(child["CertPassword"]) ? "[NOT SET]" : "[REDACTED]"
                    };

                    _logger.LogError(x, 
                        "CRITICAL: Failed to initialize SMTP listener endpoint. " +
                        "Offending Config: {EndpointJson} | Reason: {Error}", 
                        JsonSerializer.Serialize(safeLogPayload), x.Message);
                }
            }
            
            _smtpServer = new SMTP_Server();
            _smtpServer.SessionIdleTimeout = _configuration.GetValue<int>("SmtpServer:SessionIdleTimeout", defaultValue: 300);
            _smtpServer.MaxMessageSize = _configuration.GetValue<int>("SmtpServer:MaxMessageSize", defaultValue: 26214400);
            _smtpServer.Logger = new Logger();
            _smtpServer.Logger.WriteLog += (s,e) => { 
                //Console.WriteLine(e.LogEntry.EntryType + " " + e.LogEntry.Text);
            };
            _smtpServer.Error += (s, e) => {
                _logger.LogError(e.Exception, "Error in SMTP server.");
            };
            _smtpServer.ListenEndpoints = smtpEndpoints.ToArray();
            _smtpServer.SessionCreatedAsync += SmtpServer_SessionCreatedAsync;
            if (_configuration.GetValue<bool>("SmtpServer:Enabled", defaultValue: true)) {
                _smtpServer.Start();
            }

            #endregion

            #region POP3 server

            var pop3Endpoints = new List<TCP_ServerEndpoint>();
            foreach (var child in _configuration.GetSection("Pop3Server:ListenEndpoints").GetChildren()) {
                try {
                    var hostName = child["HostName"] ?? System.Net.Dns.GetHostName();
                    var tlsMode = TCP_ServerTlsMode.Explicit;
                    if (child["TlsMode"] != null && string.Equals(child["TlsMode"],"Implicit",StringComparison.OrdinalIgnoreCase)) {
                        tlsMode = TCP_ServerTlsMode.Implicit;
                    }
                    var certPassword = child["CertPassword"] ?? "";
                    
                    pop3Endpoints.Add(new TCP_ServerEndpoint(
                        hostName,
                        IPAddress.Parse(child["IP"] ?? "0.0.0.0"),
                        int.Parse(child["Port"] ?? "25"),
                        tlsMode,
                        LoadOrCreateCert(certPassword,hostName)
                    ));

                    _logger.LogInformation("Successfully started POP3 listener on {Host}:{Port}", child["IP"], child["Port"]);
                }
                catch (Exception x) {
                    // Capture the raw configuration values from this specific child node for logging
                    var safeLogPayload = new {
                        HostName = child["HostName"],
                        IP = child["IP"],
                        Port = child["Port"],
                        TlsMode = child["TlsMode"],
                        CertPassword = string.IsNullOrEmpty(child["CertPassword"]) ? "[NOT SET]" : "[REDACTED]"
                    };

                    _logger.LogError(x, 
                        "CRITICAL: Failed to initialize POP3 listener endpoint. " +
                        "Offending Config: {EndpointJson} | Reason: {Error}", 
                        JsonSerializer.Serialize(safeLogPayload), x.Message);
                }
            }

            _pop3Server = new POP3_Server();
            _pop3Server.SessionIdleTimeout = _configuration.GetValue<int>("Pop3Server:SessionIdleTimeout", defaultValue: 300);
            _pop3Server.Logger = new Logger();
            _pop3Server.Logger.WriteLog += (s,e) => { 
                //Console.WriteLine(e.LogEntry.EntryType + " " + e.LogEntry.Text);
            };
            _pop3Server.Error += (s, e) => {
                _logger.LogError(e.Exception, "Error in POP3 server.");
            };
            _pop3Server.ListenEndpoints = pop3Endpoints.ToArray();
            _pop3Server.SessionCreatedAsync += Pop3Server_SessionCreated;
            if (_configuration.GetValue<bool>("Pop3Server:Enabled", defaultValue: true)) {
                _pop3Server.Start();
            }

            #endregion

            #region IMAP server

            var imapEndpoints = new List<TCP_ServerEndpoint>();
            foreach (var child in _configuration.GetSection("ImapServer:ListenEndpoints").GetChildren()) {
                try {
                    var hostName = child["HostName"] ?? System.Net.Dns.GetHostName();
                    var tlsMode = TCP_ServerTlsMode.Explicit;
                    if (child["TlsMode"] != null && string.Equals(child["TlsMode"],"Implicit",StringComparison.OrdinalIgnoreCase)) {
                        tlsMode = TCP_ServerTlsMode.Implicit;
                    }
                    var certPassword = child["CertPassword"] ?? "";
                    
                    imapEndpoints.Add(new TCP_ServerEndpoint(
                        hostName,
                        IPAddress.Parse(child["IP"] ?? "0.0.0.0"),
                        int.Parse(child["Port"] ?? "25"),
                        tlsMode,
                        LoadOrCreateCert(certPassword,hostName)
                    ));

                    _logger.LogInformation("Successfully started IMAP listener on {Host}:{Port}", child["IP"], child["Port"]);
                }
                catch (Exception x) {
                    // Capture the raw configuration values from this specific child node for logging
                    var safeLogPayload = new {
                        HostName = child["HostName"],
                        IP = child["IP"],
                        Port = child["Port"],
                        TlsMode = child["TlsMode"],
                        CertPassword = string.IsNullOrEmpty(child["CertPassword"]) ? "[NOT SET]" : "[REDACTED]"
                    };

                    _logger.LogError(x, 
                        "CRITICAL: Failed to initialize IMAP listener endpoint. " +
                        "Offending Config: {EndpointJson} | Reason: {Error}", 
                        JsonSerializer.Serialize(safeLogPayload), x.Message);
                }
            }

            _imapServer = new IMAP_Server();
            _imapServer.SessionIdleTimeout = _configuration.GetValue<int>("ImapServer:SessionIdleTimeout", defaultValue: 300);
            _imapServer.Logger = new Logger();
            _imapServer.Logger.WriteLog += (s,e) => { 
                //Console.WriteLine(e.LogEntry.EntryType + " " + e.LogEntry.Text);
            };
            _imapServer.Error += (s, e) => {
                _logger.LogError(e.Exception, "Error in IMAP server.");
            };
            _imapServer.ListenEndpoints = imapEndpoints.ToArray();
            _imapServer.SessionCreatedAsync += ImapServer_SessionCreated;
            if (_configuration.GetValue<bool>("ImapServer:Enabled", defaultValue: true)) {
                _imapServer.Start();
            }

            #endregion

            return Task.CompletedTask;
        }        

        #endregion

        #region method StopAsync

        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown. Stops all active 
        /// protocol listeners and disposes of networking resources.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Gracefully stopping Mail Server instance...");

            // Stop protocol servers and release network listeners
            _smtpServer?.Stop();
            _pop3Server?.Stop();
            _imapServer?.Stop();

            _logger.LogInformation("Mail Server instance stopped successfully.");

            return Task.CompletedTask;
        }

        #endregion


        #region method GetSessions

        /// <summary>
        /// Retrieves a snapshot of all currently active inbound (SMTP, POP3, IMAP) and outbound (Relay) sessions.
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

                foreach(var session in _imapServer!.Sessions.ToArray()) {
                    try {
                        retValue.Add(new SessionsController.SessionDto(
                            session.ID,
                            "IMAP",
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

                foreach(var session in _relayServer!.Sessions.Values.ToArray()) {
                    try {
                        retValue.Add(new SessionsController.SessionDto(
                            session.Tags["ID"].ToString() ?? "",
                            "Relay",
                            session.IsConnected ? session.RemoteEndPoint.ToString() : "",
                            session.IsConnected ? session.LocalEndPoint.ToString() : "",
                            session.IsConnected ? session.IsSecureConnection : false,
                            session.IsConnected ? session.IsAuthenticated : false,
                            session.IsConnected ? session.AuthenticatedUserIdentity?.Name : "",
                            session.Tags["StatusText"].ToString(),
                            session.IsConnected ? session.ConnectTime : DateTime.UtcNow,
                            session.IsConnected ? session.LastActivity : DateTime.UtcNow
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
        /// Disconnects an active inbound or outbound session identified by its protocol and session ID.
        /// </summary>
        /// <param name="protocol">The protocol name of the session to disconnect (<c>"SMTP"</c>, <c>"POP3"</c>, <c>"IMAP"</c>, or <c>"Relay"</c>).</param>
        /// <param name="id">The unique identifier of the session to disconnect.</param>
        public void DisconnectSession(string protocol, string id)
        {
            if (protocol.Equals("SMTP",StringComparison.OrdinalIgnoreCase)) {
                try{
                    _smtpServer!.Sessions[id].Disconnect();
                }
                catch {
                }
            }
            else if (protocol.Equals("POP3",StringComparison.OrdinalIgnoreCase)) {
                try{
                    _pop3Server!.Sessions[id].Disconnect();
                }
                catch {
                }
            }
            else if (protocol.Equals("IMAP",StringComparison.OrdinalIgnoreCase)) {
                try{
                    _imapServer!.Sessions[id].Disconnect();
                }
                catch {
                }
            }
            else if (protocol.Equals("Relay",StringComparison.OrdinalIgnoreCase)) {
                try{
                    _relayServer!.Sessions[id].Disconnect();
                }
                catch {
                }
            }
        }

        #endregion


        #region method LoadOrCreateCert

        /// <summary>
        /// Loads an existing SSL/TLS certificate from disk, or automatically generates, 
        /// saves, and loads a new self-signed certificate if the file does not exist.
        /// </summary>
        /// <param name="password">The password used to encrypt and protect the PKCS#12 (.pfx) certificate file.</param>
        /// <param name="hostName">The server hostname used for the certificate's Subject Name and Subject Alternative Names (SAN).</param>
        /// <returns>An initialized <see cref="X509Certificate2"/> instance configured with persistent key storage and exportable keys.</returns>
        private X509Certificate2 LoadOrCreateCert(string password, string hostName)
        {
            string cerFile = Path.Combine(AppContext.BaseDirectory, "Data" , "Certificates", $"{hostName}.pfx");
            if (!File.Exists(cerFile)) {
                var cert = CreateSelfSignedCert(password,hostName);
                File.WriteAllBytes(cerFile,cert.Export(X509ContentType.Pfx,password));
                
                _logger.LogInformation(
                    "Generated new self-signed certificate successfully. " +
                    "Subject: {Subject} | Thumbprint: {Thumbprint} | Expires: {Expires:yyyy-MM-dd}",
                    cert.Subject,
                    cert.Thumbprint,
                    cert.NotAfter);
            }

            return X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(cerFile),
                password, 
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
            );
        }

        #endregion

        #region static method CreateSelfSignedCert

        /// <summary>
        /// Creates a cryptographically secure, self-signed SSL/TLS certificate configured for server authentication 
        /// with comprehensive Subject Alternative Names (SANs) and persistent key storage flags.
        /// </summary>
        /// <param name="password">The password used to encrypt the exported PKCS#12 (.pfx) data.</param>
        /// <param name="hostName">The primary domain name or host name assigned to the certificate's Common Name (CN) and SAN collection.</param>
        /// <returns>
        /// An initialized, exportable <see cref="X509Certificate2"/> instance with persistent key storage, 
        /// safe for Windows Schannel binding and mail server listener encryption.
        /// </returns>
        public static X509Certificate2 CreateSelfSignedCert(string password,string hostName)
        {
            var dn = new X500DistinguishedName($"CN={hostName}");

            using var rsa = RSA.Create(2048);

            var req = new CertificateRequest(
                dn,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Basic constraints: not a CA
            req.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));

            // Key usage: required for TLS server
            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature |
                    X509KeyUsageFlags.KeyEncipherment,
                    false));

            // Subject Key Identifier
            req.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            // REQUIRED: Server Authentication EKU
            var eku = new OidCollection {
                new Oid("1.3.6.1.5.5.7.3.1") // Server Authentication
            };
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(eku, false));

            // REQUIRED: Subject Alternative Name (SAN)
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName(hostName);
            sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
            sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            // Add the local machine name if it differs from the primary hostname
            string machineName = System.Net.Dns.GetHostName();
            if (!string.IsNullOrEmpty(machineName) && 
                !string.Equals(machineName, hostName, StringComparison.OrdinalIgnoreCase))
            {
                sanBuilder.AddDnsName(machineName);
            }
            req.CertificateExtensions.Add(sanBuilder.Build());

            var cert = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(5));

            // Export to PFX and re-import to resolve Windows Schannel ephemeral key limitations
            var pfxBytes = cert.Export(X509ContentType.Pfx,password);
    
            // Dispose the original ephemeral certificate to free resources
            cert.Dispose();

            cert = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

            return cert;
        }

        #endregion
    }        
}
