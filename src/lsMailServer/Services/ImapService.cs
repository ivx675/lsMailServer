using lsMailServer.Configuration;
using lsMailServer.RestApi;
using LumiSoft.Net.AUTH;
using LumiSoft.Net.IMAP;
using LumiSoft.Net.IMAP.Server;
using LumiSoft.Net.Log;
using LumiSoft.Net.TCP;
using System.Text.Json;

namespace lsMailServer.Services
{
    /// <summary>
    /// Hosted background service that manages the inbound IMAP4 server lifecycle,
    /// listener endpoints, TLS certificates, authentication, and mailbox/message operations.
    /// </summary>
    public class ImapService : IHostedService, IDisposable
    {
        private readonly ILogger<ImapService> _logger;
        private readonly IConfiguration _configuration;
        private readonly FailedAuthLimiterService _failedAuthLimiterService;
        private readonly IDatabaseService _dbService;
        private readonly MailStoreService _mailStoreService;
        private readonly IMAP_Server _imapServer;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImapService"/> class.
        /// </summary>
        /// <param name="logger">The logger used to record IMAP server and session diagnostics.</param>
        /// <param name="configuration">The application configuration containing the <c>ImapServer</c> section.</param>
        /// <param name="failedAuthLimiterService">The rate limiter service used to track and block repeated failed authentication attempts.</param>
        /// <param name="dbService">The database service used to authenticate users and query mailbox quota information.</param>
        /// <param name="mailStoreService">The mail store service used to manage folders, message flags, search, and message storage.</param>
        public ImapService(
            ILogger<ImapService> logger,
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

            _imapServer = new IMAP_Server();
            _imapServer.Error += (s, e) => {
                _logger.LogError(e.Exception, "Error in IMAP server.");
            };
            _imapServer.SessionCreatedAsync += SessionCreated;            
            _imapServer.Logger = new Logger();
            _imapServer.Logger.WriteLog += SessionLog;
        }

        #region method Dispose

        /// <summary>
        /// Releases all resources used by the underlying <see cref="IMAP_Server"/> instance.
        /// </summary>
        public void Dispose()
        {
            _imapServer.Dispose();
        }

        #endregion


        #region Event handlers

        #region method SessionCreated

        /// <summary>
        /// Raised when a new client TCP session is accepted by the IMAP server.
        /// Configures session capabilities, error handling, SASL/LOGIN authentication, and IMAP command handlers.
        /// </summary>
        /// <param name="e">The event arguments containing the newly created <see cref="IMAP_Session"/>.</param>
        private Task SessionCreated(TCP_Server_e_SessionCreated<IMAP_Session> e)
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
            sasPlain.Authenticate += async (s,eArgs) => { 
                await AuthPlainAsync(eArgs, e.Session);
            };
            e.Session.Authentications.Add(sasPlain.Name,sasPlain);
                        
            e.Session.AuthLoginAsync += LoginAsync;
            e.Session.NamespaceAsync += NamespaceAsync;
            e.Session.ListAsync += ListAsync;
            e.Session.CreateAsync += CreateAsync;
            e.Session.DeleteAsync += DeleteAsync;
            e.Session.RenameAsync += RenameAsync;
            e.Session.LSubAsync += LSubAsync;
            e.Session.SubscribeAsync += SubscribeAsync;
            e.Session.UnsubscribeAsync += UnsubscribeAsync;
            e.Session.StatusAsync += StatusAsync;
            e.Session.SelectAsync += SelectAsync;
            e.Session.ExamineAsync += ExamineAsync;
            e.Session.FolderSyncAsync += FolderSyncAsync;
            e.Session.AppendBeginAsync += AppendBeginAsync;
            e.Session.AppendCancelAsync += AppendCancelAsync;
            e.Session.AppendCompleteAsync += AppendCompleteAsync;
            e.Session.GetQuotaRootAsync += GetQuotaRootAsync;
            e.Session.GetQuotaAsync += GetQuotaAsync;
            // Selected state commands
            e.Session.CloseAsync += CloseAsync;
            e.Session.FetchAsync += FetchAsync;
            e.Session.SearchAsync += SearchAsync;
            e.Session.StoreAsync += StoreAsync;
            e.Session.CopyAsync += CopyAsync;
            e.Session.MoveAsync += MoveAsync;
            e.Session.ExpungeAsync += ExpungeAsync;

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
        /// Raised when an <c>AUTHENTICATE PLAIN</c> exchange is received from a client session.
        /// Validates user credentials against the database while enforcing failed-attempt rate limiting.
        /// </summary>
        /// <param name="eArgs">The SASL authentication event arguments containing credentials and authentication state.</param>
        /// <param name="session">The active <see cref="IMAP_Session"/> requesting authentication.</param>
        private async Task AuthPlainAsync(AUTH_e_Authenticate eArgs, IMAP_Session session)
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
                _logger.LogError(ex, "Error during IMAP authentication.");
            }
        }

        #endregion

        #region method LoginAsync

        /// <summary>
        /// Raised when an IMAP <c>LOGIN</c> command is received from a client session.
        /// Validates user credentials against the database while enforcing failed-attempt rate limiting.
        /// </summary>
        /// <param name="eArgs">The IMAP <c>LOGIN</c> event arguments containing credentials, session, and server response.</param>
        private async Task LoginAsync(IMAP_e_AuthLogin eArgs)
        {
            try {
                if(_failedAuthLimiterService.IsBlocked(eArgs.Session.RemoteEndPoint.Address, eArgs.UserName, out _)) {
                    // Server reports default authentication failure response automatically
                }
                else if (await _dbService.AuthenticateUserAsync(eArgs.UserName, eArgs.Password) != null) {
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Authenticated successfully.");

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
                _logger.LogError(ex, "Error during IMAP LOGIN command.");
            }
        }

        #endregion

        #region method NamespaceAsync

        /// <summary>
        /// Raised when an IMAP <c>NAMESPACE</c> command is received.
        /// Uses the default personal namespace (<c>""</c> prefix with <c>'/'</c> delimiter).
        /// </summary>
        /// <param name="eArgs">The <c>NAMESPACE</c> command event arguments.</param>
        private async Task NamespaceAsync(IMAP_e_Namespace eArgs)
        {
            // Per IMAP rev2 RFC 9051 NAMESPACE is standard IMAP command.
            // By default RFC-compliant namespace response is provided and represents
            // a single personal namespace with an empty prefix and '/' hierarchy
            // delimiter, and no other-users or shared namespaces.
        }

        #endregion

        #region method ListAsync

        /// <summary>
        /// Raised when an IMAP <c>LIST</c> command is received to enumerate matching folders and hierarchy flags.
        /// </summary>
        /// <param name="eArgs">The <c>LIST</c> command event arguments containing the pattern and target folder list.</param>
        private async Task ListAsync(IMAP_e_List eArgs)
        {
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
        }

        #endregion

        #region method CreateAsync

        /// <summary>
        /// Raised when an IMAP <c>CREATE</c> command is received to create a new mailbox folder.
        /// </summary>
        /// <param name="eArgs">The <c>CREATE</c> command event arguments specifying the folder name.</param>
        private async Task CreateAsync(IMAP_e_Create eArgs)
        {
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
        }

        #endregion

        #region method DeleteAsync

        /// <summary>
        /// Raised when an IMAP <c>DELETE</c> command is received to remove a non-<c>INBOX</c> folder.
        /// </summary>
        /// <param name="eArgs">The <c>DELETE</c> command event arguments specifying the folder to delete.</param>
        private async Task DeleteAsync(IMAP_e_Delete eArgs)
        {
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
                    await _mailStoreService.DeleteFolderAsync(userName,folder);
                    eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder deleted.");
                }
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing DELETE command.");
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
            }
        }

        #endregion

        #region method RenameAsync

        /// <summary>
        /// Raised when an IMAP <c>RENAME</c> command is received to rename an existing non-<c>INBOX</c> folder.
        /// </summary>
        /// <param name="eArgs">The <c>RENAME</c> command event arguments containing current and new folder names.</param>
        private async Task RenameAsync(IMAP_e_Rename eArgs)
        {
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
        }

        #endregion

        #region method LSubAsync

        /// <summary>
        /// Raised when an IMAP <c>LSUB</c> command is received to list subscribed folders and synthetic parent nodes.
        /// </summary>
        /// <param name="eArgs">The <c>LSUB</c> command event arguments containing filter criteria and target results.</param>
        private async Task LSubAsync(IMAP_e_LSub eArgs)
        {
            try {
                string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
        
                // 1. Get explicitly subscribed folder paths from database (relative, normalized)
                HashSet<string> subscribedFolders = new HashSet<string>(
                    (await _mailStoreService.GetSubscribedFoldersAsync(userName)).Select(f => f.Replace('\\', '/')),
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
        }

        #endregion

        #region method SubscribeAsync

        /// <summary>
        /// Raised when an IMAP <c>SUBSCRIBE</c> command is received to subscribe a folder for the authenticated user.
        /// </summary>
        /// <param name="eArgs">The <c>SUBSCRIBE</c> command event arguments specifying the target folder.</param>
        private async Task SubscribeAsync(IMAP_e_Subscribe eArgs)
        {
            try {
                await _mailStoreService.SubscribeFolderAsync(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder);
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder subscribed.");
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing SUBSCRIBE command.");
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
            }
        }

        #endregion

        #region method UnsubscribeAsync

        /// <summary>
        /// Raised when an IMAP <c>UNSUBSCRIBE</c> command is received to unsubscribe a folder for the authenticated user.
        /// </summary>
        /// <param name="eArgs">The <c>UNSUBSCRIBE</c> command event arguments specifying the target folder.</param>
        private async Task UnsubscribeAsync(IMAP_e_Unsubscribe eArgs)
        {
            try {
                await _mailStoreService.UnsubscribeFolderAsync(eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder);
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","Folder unsubscribed.");
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing UNSUBSCRIBE command.");
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
            }
        }

        #endregion

        #region method StatusAsync

        /// <summary>
        /// Raised when an IMAP <c>STATUS</c> command is received to report message counts, <c>UIDVALIDITY</c>, <c>UIDNEXT</c>, and size.
        /// </summary>
        /// <param name="eArgs">The <c>STATUS</c> command event arguments populated with mailbox statistics.</param>
        private async Task StatusAsync(IMAP_e_Status eArgs)
        {
            try {
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
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing STATUS command.");
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
            }
        }

        #endregion

        #region method SelectAsync

        /// <summary>
        /// Raised when an IMAP <c>SELECT</c> command is received to open a mailbox folder in read-write mode.
        /// </summary>
        /// <param name="eArgs">The <c>SELECT</c> command event arguments populated with flags, <c>UIDVALIDITY</c>, and message metadata.</param>
        private async Task SelectAsync(IMAP_e_Select eArgs)
        {
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
        }

        #endregion

        #region method ExamineAsync

        /// <summary>
        /// Raised when an IMAP <c>EXAMINE</c> command is received to open a mailbox folder in read-only mode.
        /// </summary>
        /// <param name="eArgs">The <c>EXAMINE</c> command event arguments populated with flags, <c>UIDVALIDITY</c>, and message metadata.</param>
        private async Task ExamineAsync(IMAP_e_Examine eArgs)
        {
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
        }

        #endregion

        #region method FolderSyncAsync

        /// <summary>
        /// Raised during folder synchronization to detect mailbox state changes and return updated message metadata or UIDs.
        /// </summary>
        /// <param name="eArgs">The folder synchronization event arguments containing the client's known version and output collections.</param>
        private async Task FolderSyncAsync(IMAP_e_FolderSync eArgs)
        {
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
        }

        #endregion

        #region method AppendBeginAsync

        /// <summary>
        /// Raised when an IMAP <c>APPEND</c> command begins to allocate a temporary stream for incoming message data.
        /// </summary>
        /// <param name="eArgs">The <c>APPEND</c> begin event arguments used to assign the destination stream.</param>
        private async Task AppendBeginAsync(IMAP_e_AppendBegin eArgs)
        {
            try {
                eArgs.Stream = _mailStoreService.CreateTempMessageStream();
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing IMAP APPEND command.");
            }
        }

        #endregion

        #region method AppendCancelAsync

        /// <summary>
        /// Raised when an IMAP <c>APPEND</c> operation is canceled or aborted before completion.
        /// </summary>
        /// <param name="eArgs">The <c>APPEND</c> cancel event arguments containing the temporary stream to dispose.</param>
        private async Task AppendCancelAsync(IMAP_e_AppendCancel eArgs)
        {
            try {
                await eArgs.Stream.DisposeAsync();
            }
            catch {
            }
        }

        #endregion

        #region method AppendCompleteAsync

        /// <summary>
        /// Raised when an IMAP <c>APPEND</c> message upload completes to enforce quota and store the message in the target folder.
        /// </summary>
        /// <param name="eArgs">The <c>APPEND</c> complete event arguments containing the message stream, flags, size, and internal date.</param>
        private async Task AppendCompleteAsync(IMAP_e_AppendComplete eArgs)
        {
            try {
                var user = await _dbService.GetUserAsync(eArgs.Session.AuthenticatedUserIdentity!.Name);
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
        }

        #endregion

        #region method GetQuotaRootAsync

        /// <summary>
        /// Raised when an IMAP <c>GETQUOTAROOT</c> command is received to report quota roots and storage usage in kilobytes.
        /// </summary>
        /// <param name="eArgs">The <c>GETQUOTAROOT</c> event arguments populated with quota root and quota entries.</param>
        private async Task GetQuotaRootAsync(IMAP_e_GetQuotaRoot eArgs)
        {
            try {
                 string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                 if (_mailStoreService.FolderExists(userName,eArgs.Folder)) {
                     var userInfo = await _dbService.GetUserAsync(userName);
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
        }

        #endregion

        #region method GetQuotaAsync

        /// <summary>
        /// Retrieves and reports the authenticated user's mailbox quota in kilobytes for an IMAP <c>GETQUOTA</c> request.
        /// </summary>
        /// <remarks>A quota root other than the empty string (<c>""</c>) is reported as nonexistent.</remarks>
        /// <param name="eArgs">The <c>GETQUOTA</c> event arguments containing the requested quota root and output responses.</param>
        private async Task GetQuotaAsync(IMAP_e_GetQuota eArgs)
        {
            try {
                string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;
                if (string.Equals("",eArgs.QuotaRoot,StringComparison.OrdinalIgnoreCase)) {
                    var userInfo = await _dbService.GetUserAsync(userName);
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
        }

        #endregion


        #region method CloseAsync

        /// <summary>
        /// Raised when an IMAP <c>CLOSE</c> command is received to permanently expunge messages marked <c>\Deleted</c> in a read-write mailbox.
        /// </summary>
        /// <param name="eArgs">The <c>CLOSE</c> event arguments containing the folder, read-only flag, and deleted messages.</param>
        private async Task CloseAsync(IMAP_e_Close eArgs)
        {
            try {
                string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;

                if(!eArgs.IsReadOnly){
                    foreach(var message in eArgs.DeletedMessages){
                        try {
                            await _mailStoreService.DeleteMessageAsync(userName, eArgs.Folder, message.UID);
                        }
                        catch (Exception ex) {
                            _logger.LogWarning(
                            ex,
                            "Failed to delete message UID '{MessageUid}' from {Folder} for user '{UserName}' from {RemoteIp} (Session: {SessionId})",
                            message.UID,
                            eArgs.Folder,
                            userName,
                            eArgs.Session.RemoteEndPoint?.ToString() ?? "unknown",
                            eArgs.Session.ID);
                        }
                    }
                }
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing IMAP CLOSE command.");
            }
        }

        #endregion

        #region method FetchAsync

        /// <summary>
        /// Raised when an IMAP <c>FETCH</c> or <c>UID FETCH</c> command is received to retrieve message streams, envelopes, or body structures.
        /// </summary>
        /// <param name="eArgs">The <c>FETCH</c> event arguments specifying requested items and target messages.</param>
        private async Task FetchAsync(IMAP_e_Fetch eArgs)
        {
            try {
                foreach(var message in eArgs.Messages) {
                    string                userName      = eArgs.Session.AuthenticatedUserIdentity!.Name;
                    string                folder        = eArgs.Session.SelectedFolderName!;
                    Stream?               messageStream = null;
                    IMAP_t_Envelope?      envelope      = null;
                    IMAP_t_BodyStructure? bodyStructure = null;
                    try{
                        if(eArgs.RequiresMessageStream){
                            messageStream = await _mailStoreService.GetMessageStreamAsync(userName,folder,message.UID,eArgs.SetSeen);
                            // Message no longer exists.
                            if(messageStream == null) {
                                continue;
                            }
                        }

                        if(eArgs.RequiresEnvelope || eArgs.RequiresBodyStructure){
                            var messageInfo = await _mailStoreService.GetMessageInfoAsync(userName,folder,message.UID);
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
        }

        #endregion

        #region method SearchAsync

        /// <summary>
        /// Raised when an IMAP <c>SEARCH</c> or <c>UID SEARCH</c> command is received to find matching message UIDs.
        /// </summary>
        /// <param name="eArgs">The <c>SEARCH</c> event arguments containing search criteria and output matched UIDs.</param>
        private async Task SearchAsync(IMAP_e_Search eArgs)
        {
            try {
                eArgs.MatchedUids = (await _mailStoreService.SearchAsync(
                    eArgs.Session.AuthenticatedUserIdentity!.Name,eArgs.Folder,eArgs.Criteria)).ToArray();

                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","SEARCH completed.");
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing IMAP SEARCH command.");
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
            }
        }

        #endregion

        #region method StoreAsync

        /// <summary>
        /// Raised when an IMAP <c>STORE</c> or <c>UID STORE</c> command is received to add, remove, or replace message flags.
        /// </summary>
        /// <param name="eArgs">The <c>STORE</c> event arguments specifying target messages, flags, and update mode.</param>
        private async Task StoreAsync(IMAP_e_Store eArgs)
        {
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

                    await _mailStoreService.StoreMessageFlagsAsync(userName,eArgs.Folder,message.UID,eArgs.Flags,storeMode);
                }

                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","STORE completed.");
            }
            catch(Exception x) {
                _logger.LogError(x, "Unexpected error processing IMAP STORE command.");
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
            }
        }

        #endregion

        #region method CopyAsync

        /// <summary>
        /// Raised when an IMAP <c>COPY</c> or <c>UID COPY</c> command is received to copy messages to a destination folder with quota and rollback support.
        /// </summary>
        /// <param name="eArgs">The <c>COPY</c> event arguments containing source/target folders, messages, and response data.</param>
        private async Task CopyAsync(IMAP_e_Copy eArgs)
        {
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
        
                UserInfo? userInfo = await _dbService.GetUserAsync(userName);
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

                int copiedCount = 0;
                foreach (var m in eArgs.Messages) {
                    try {
                        // Gracefully handle messages deleted concurrently by another session
                        long? newUid = await _mailStoreService.CopyMessageAsync(userName, eArgs.SourceFolder, m.UID, eArgs.TargetFolder);
                        if (newUid.HasValue) {
                            sourceUids.Add(m.UID);
                            copiedUids.Add(newUid.Value);
                        }
                        copiedCount++;
                    }
                    catch (Exception ex) {
                        _logger.LogWarning(
                            ex,
                            "Failed to copy message UID '{Uid}' from '{SourceFolder}' to '{TargetFolder}' for user '{UserName}'. Rolling back {RollbackCount} copied message(s).",
                            m.UID,
                            eArgs.SourceFolder,
                            eArgs.TargetFolder,
                            userName,
                            copiedCount);
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
                            _logger.LogError(
                                cleanupEx,
                                "Failed to rollback copied message UID '{Uid}' in '{TargetFolder}' for user '{UserName}'.",
                                uid,
                                eArgs.TargetFolder,
                                userName);
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
        }

        #endregion

        #region method MoveAsync

        /// <summary>
        /// Raised when an IMAP <c>MOVE</c> or <c>UID MOVE</c> command is received to copy messages to a destination folder and remove or flag source messages.
        /// </summary>
        /// <param name="eArgs">The <c>MOVE</c> event arguments containing source/target folders, messages, and response data.</param>
        private async Task MoveAsync(IMAP_e_Move eArgs)
        {
            try {
                string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;

                // 1. Quota & Target Folder Validation
                long totalMessagesSize = eArgs.Messages.Sum(m => m.Size);
                var userInfo = await _dbService.GetUserAsync(userName);
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

                int copiedCount = 0;
                foreach (var m in eArgs.Messages) {
                    try {
                        long? newUid = await _mailStoreService.CopyMessageAsync(userName, eArgs.SourceFolder, m.UID, eArgs.TargetFolder);
                        if (newUid.HasValue) {
                            sourceUids.Add(m.UID);
                            copiedUids.Add(newUid.Value);
                        }
                        copiedCount++;
                    }
                    catch (Exception ex) {
                        _logger.LogWarning(
                            ex,
                            "Failed to move message UID '{Uid}' from '{SourceFolder}' to '{TargetFolder}' for user '{UserName}'. Rolling back {RollbackCount} copied message(s).",
                            m.UID,
                            eArgs.SourceFolder,
                            eArgs.TargetFolder,
                            userName,
                            copiedCount);
                        rollback = true;
                        break;
                    }
                }

                // If copy failed, roll back destination copies and return NO
                if (rollback) {
                    foreach (long uid in copiedUids) {
                        try { 
                            await _mailStoreService.DeleteMessageAsync(userName, eArgs.TargetFolder, uid); 
                        } 
                        catch (Exception cleanupEx) {
                            _logger.LogError(
                                cleanupEx,
                                "Failed to rollback moved message UID '{Uid}' in '{TargetFolder}' for user '{UserName}'.",
                                uid,
                                eArgs.TargetFolder,
                                userName);
                        }
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
                        _logger.LogWarning(
                            deleteEx,
                            "Moved message UID '{Uid}' to '{TargetFolder}' for user '{UserName}', but failed to delete source from '{SourceFolder}'; marking source as \\Deleted fallback.",
                            m.UID,
                            eArgs.TargetFolder,
                            userName,
                            eArgs.SourceFolder);
            
                        try {
                            // Fallback: Set \Deleted flag so standard expunge/cleaner handles it later
                            await _mailStoreService.StoreMessageFlagsAsync(userName, eArgs.SourceFolder, m.UID, ["\\Deleted"], FlagStoreMode.Replace);
                        }
                        catch (Exception flagEx) {
                            _logger.LogWarning(
                                flagEx,
                                "Failed to apply fallback \\Deleted flag to source message UID '{Uid}' in '{SourceFolder}' for user '{UserName}'.",
                                m.UID,
                                eArgs.SourceFolder,
                                userName);

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
        }

        #endregion

        #region method ExpungeAsync

        /// <summary>
        /// Raised when an IMAP <c>EXPUNGE</c> or <c>UID EXPUNGE</c> command is received to permanently delete expunged messages.
        /// </summary>
        /// <param name="eArgs">The <c>EXPUNGE</c> event arguments containing the folder and messages to delete.</param>
        private async Task ExpungeAsync(IMAP_e_Expunge eArgs)
        {
            try {
                string userName = eArgs.Session.AuthenticatedUserIdentity!.Name;

                foreach(var message in eArgs.Messages){
                    try {
                        await _mailStoreService.DeleteMessageAsync(userName, eArgs.Folder, message.UID);
                    }
                    catch (Exception ex) {
                        _logger.LogWarning(
                            ex,
                            "Failed to delete message UID '{MessageUid}' from {Folder} for user '{UserName}' from {RemoteIp} (Session: {SessionId})",
                            message.UID,
                            eArgs.Folder,
                            userName,
                            eArgs.Session.RemoteEndPoint?.ToString() ?? "unknown",
                            eArgs.Session.ID);
                    }
                }

                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"OK","EXPUNGE completed.");
            }
            catch (Exception x) {
                _logger.LogError(x, "Unexpected error processing IMAP EXPUNGE command.");
                eArgs.Response = new IMAP_r_ServerStatus(eArgs.Response.CommandTag,"NO","An unexpected server error occurred while processing your request.");
            }
        }

        #endregion

        #endregion


        #region method StartAsync

        /// <summary>
        /// Applies current <c>ImapServer</c> configuration settings, initializes TLS listen endpoints, and starts the IMAP server.
        /// </summary>
        /// <param name="cancellationToken">A token that signals when the start operation should be canceled.</param>
        /// <returns>A completed <see cref="Task"/> once the IMAP server has been configured and started.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_imapServer.IsRunning) {
                _imapServer.Stop();
            }            

            var settings = _configuration.GetSection("ImapServer").Get<ImapServerSettings>() ?? new();

            if (!settings.Enabled) {
                return Task.CompletedTask;
            }

            var imapEndpoints = new List<TCP_ServerEndpoint>();
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
                    
                    imapEndpoints.Add(new TCP_ServerEndpoint(
                        hostName,
                        listenEp.IP,
                        listenEp.Port,
                        tlsMode,
                        cert
                    ));

                    _logger.LogInformation(
                        "Configured IMAP endpoint {HostName} on {IP}:{Port} (TLS: {TlsMode})",
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
                        "CRITICAL: Failed to initialize IMAP listener endpoint. " +
                        "Offending Config: {EndpointJson} | Reason: {Error}", 
                        JsonSerializer.Serialize(safeLogPayload), x.Message);
                }
            }

            _imapServer.SessionIdleTimeout = settings.SessionIdleTimeout;
            _imapServer.MaxConnections = settings.MaxConnections;
            _imapServer.MaxConnectionsPerIP = settings.MaxConnectionsPerIP;
            _imapServer.ListenEndpoints = imapEndpoints.ToArray();
            _imapServer.Start();

            _logger.LogInformation("IMAP server started.");

            return Task.CompletedTask;
        }

        #endregion

        #region method StopAsync

        /// <summary>
        /// Stops the IMAP server listener while allowing active sessions to finish gracefully.
        /// </summary>
        /// <param name="cancellationToken">A token that signals when the stop operation should be canceled.</param>
        /// <returns>A completed <see cref="Task"/> once the IMAP server listener has stopped.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if(_imapServer.IsRunning) {
                _imapServer.Stop();
                _logger.LogInformation("IMAP server stopped.");
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
                _imapServer!.Sessions[id].Disconnect();
            }
            catch {
            }
        }

        #endregion
    }
}
