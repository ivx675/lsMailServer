using LumiSoft.Net.IMAP;
using LumiSoft.Net.Mail;
using LumiSoft.Net.MIME;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace lsMailServer.Services 
{
    /// <summary>
    /// Provides core mailstore management services, handling mailbox folder operations, file storage persistence, 
    /// SQLite-backed folder caching, MIME body parsing, message searching, and optimized stream retrieval 
    /// for the self-hosted mail server.
    /// </summary>
    public class MailStoreService
    {
        private readonly IDatabaseService _dbService;
        private readonly string _baseStoragePath;
        private readonly string _serverTempDirectory;

        private static readonly HashSet<string> WhitelistedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "from", "to", "cc", "bcc", "subject", "date", 
            "message-id", "in-reply-to", "references", "received", "user-agent", "x-mailer"
        };

        private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "return-path",
            "mime-version",
            "dkim-signature",
            "authentication-results",
            "received-spf"
        };

        private const int MaxUnknownHeaderLength = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="MailStoreService"/> class, setting up database dependencies, 
        /// base storage paths, and ensuring the server-wide temporary directory exists for in-progress network streams.
        /// </summary>
        /// <param name="dbService">The database service used for managing user metadata, storage quotas, and system database operations.</param>
        /// <param name="baseStoragePath">The root physical directory path on disk where all mailboxes, folders, and temporary streaming data are stored.</param>
        public MailStoreService(IDatabaseService dbService,string baseStoragePath)
        {
            _dbService = dbService;
            _baseStoragePath = baseStoragePath;

            // Server-wide temporary directory for in-progress network streams
            _serverTempDirectory = Path.Combine(_baseStoragePath, "_temp");  
            Directory.CreateDirectory(_serverTempDirectory);
        }


        #region method CreateTempMessageStream

        /// <summary>
        /// Creates a secure, temporary, seekable stream in the server-wide temporary directory 
        /// while an inbound network transmission (SMTP DATA or IMAP APPEND) is in progress.
        /// </summary>
        /// <returns>A read/write, seekable <see cref="FileStream"/> backed by a temp file that auto-deletes on close.</returns>
        public Stream CreateTempMessageStream()
        {
            string tempFilePath = Path.Combine(_serverTempDirectory, $"{Guid.NewGuid()}.tmp");

            return new FileStream(
                tempFilePath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 8192,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        }

        #endregion

        #region method GetFolders

        /// <summary>
        /// Retrieves a sorted collection of relative folder paths for the specified user's mailbox 
        /// by recursively scanning their storage directory.
        /// </summary>
        /// <param name="userName">The unique username or account identifier whose mailbox folders are being queried.</param>
        /// <returns>
        /// An array of relative folder path strings normalized with forward slash separators (<c>/</c>) 
        /// and sorted in a predictable, case-insensitive order. Returns an empty array if the user's mailbox directory does not exist.
        /// </returns>
        public string[] GetFolders(string userName)
        {
            string userRootPath = GetValidatedUserRootPath(userName);
            if (!Directory.Exists(userRootPath)) {
                return Array.Empty<string>();
            }

            var rootDirInfo = new DirectoryInfo(userRootPath);
    
            return rootDirInfo.EnumerateDirectories("*", SearchOption.AllDirectories)
                .Select(d => Path.GetRelativePath(userRootPath, d.FullName).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase) // <-- Ensures clean, predictable sorting
                .ToArray();
        }

        #endregion

        #region method FolderExists

        /// <summary>
        /// Checks whether a specified folder exists within a user's mailbox directory structure.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder to verify.</param>
        /// <returns>
        /// <see langword="true"/> if the target folder exists on disk; otherwise, <see langword="false"/>.
        /// </returns>
        public bool FolderExists(string userName,string folderName)
        {
            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);
            
            return Directory.Exists(userFolderPath);
        }

        #endregion

        #region method GetFolderInfoAsync

        /// <summary>
        /// Asynchronously retrieves folder-level metadata and (optionally) message summaries 
        /// for a specified user mailbox folder from its SQLite cache. Auto-creates the 
        /// Inbox directory and initializes its cache schema if it does not already exist.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the target folder (e.g., "Inbox").</param>
        /// <param name="loadMessagesInfo">
        /// Specifies whether to query and load individual message metadata (UID, size, flags, internal date) 
        /// into the folder info model. Defaults to <see langword="true"/>.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous operation, containing the populated <see cref="FolderInfo"/> instance, 
        /// or <see langword="null"/> if the specified folder (other than Inbox) does not exist on disk.
        /// </returns>
        /// <exception cref="Exception">
        /// Thrown when the folder exists on disk, but its mandatory metadata row (UIDVALIDITY and Version) 
        /// is missing from the SQLite cache database.
        /// </exception>
        public async Task<FolderInfo?> GetFolderInfoAsync(string userName, string folderName, bool loadMessagesInfo = true)
        {
            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);
            bool folderExists = Directory.Exists(userFolderPath);

            if (folderName.Equals("Inbox", StringComparison.OrdinalIgnoreCase)) {
                if (!folderExists) {
                    Directory.CreateDirectory(userFolderPath);
                    InitializeFolderCacheSchema(userFolderPath);
                }
            }
            else {
                if (!folderExists) {
                    return null;
                }
            }
                        
            using var connection = GetFolderCacheConnection(userFolderPath);
            await connection.OpenAsync();

            // 1. Fetch folder-level metadata (1 row)
            using var infoCmd = new SqliteCommand("SELECT UidValidity, Version FROM FolderInfo LIMIT 1;", connection);
            using var infoReader = await infoCmd.ExecuteReaderAsync();
    
            if (!await infoReader.ReadAsync()) {
                throw new Exception($"Folder '{folderName}' info (UIDVALIDITY,Version) is missing.");
            }
            FolderInfo folderInfo = new FolderInfo(folderName,infoReader.GetInt64(0), infoReader.GetInt64(1));
            await infoReader.CloseAsync();

            if (loadMessagesInfo) {
                // 2. Fetch messages info
                const string summarySql = @"
                    SELECT Version, MessageUid, MessageSize, MessageFlags, MessageInternalDate 
                    FROM FolderMessageCache 
                    ORDER BY MessageUid ASC;
                ";
    
                using var sumCmd = new SqliteCommand(summarySql, connection);
                using var sumReader = await sumCmd.ExecuteReaderAsync();

                while (await sumReader.ReadAsync()) {
                    folderInfo.Messages.Add(new MessageInfo(
                        Version: sumReader.GetInt64(0),
                        MessageUid: sumReader.GetInt64(1),
                        MessageSize: sumReader.GetInt64(2),
                        MessageFlags: sumReader.IsDBNull(3) ? [] : sumReader.GetString(3).Split(' ',StringSplitOptions.RemoveEmptyEntries),
                        MessageInternalDate: DateTime.Parse(sumReader.GetString(4))
                    ));
                }
            }

            return folderInfo;
        }

        #endregion

        #region method CreateFolder

        /// <summary>
        /// Creates a new mailbox folder for the specified user on disk and initializes 
        /// its local SQLite cache schema and metadata store.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder to be created.</param>
        public void CreateFolder(string userName,string folderName)
        {
            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);
            Directory.CreateDirectory(userFolderPath);
            InitializeFolderCacheSchema(userFolderPath);
        }

        #endregion

        #region method DeleteFolderAsync

        /// <summary>
        /// Asynchronously deletes a specified mailbox folder for a user, recursively removing its contents
        /// and local SQLite cache, calculates the freed disk space from stored email files,
        /// and updates the user's total storage usage tracking.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder to be deleted.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when the target folder does not exist on disk.
        /// </exception>
        public async Task DeleteFolderAsync(
            string userName,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);

            var dirInfo = new DirectoryInfo(userFolderPath);
            if (!dirInfo.Exists) {
                throw new DirectoryNotFoundException(folderName);
            }

            // Natively recursive and memory-efficient (deferred execution)
            long totalBytesFreed = dirInfo
                .EnumerateFiles("*.eml", SearchOption.AllDirectories)
                .Sum(file => file.Length);

            Directory.Delete(userFolderPath, recursive: true);

            await _dbService
                .UpdateUserStorageUsageAsync(userName, -totalBytesFreed, cancellationToken)
                .ConfigureAwait(false);
        }

        #endregion

        #region method RenameFolder

        /// <summary>
        /// Renames or moves an existing mailbox folder for a specified user to a new path or name on disk.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The current relative path or name of the folder to be renamed.</param>
        /// <param name="newFolderName">The new relative path or name for the folder.</param>
        public void RenameFolder(string userName,string folderName, string newFolderName)
        {
            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);
            string userNewFolderPath = GetValidatedUserFolderPath(userName, newFolderName);
            Directory.Move(userFolderPath,userNewFolderPath);
        }

        #endregion

        #region mehtod GetSubscribedFoldersAsync

        /// <summary>
        /// Asynchronously retrieves a collection of folder names that the specified user is currently subscribed to
        /// by querying their local user metadata SQLite database.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An array of subscribed folder name strings. Returns an empty array if the user's
        /// metadata database does not exist yet or contains no active subscriptions.
        /// </returns>
        public async Task<string[]> GetSubscribedFoldersAsync(
            string userName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subscribedFolders = new List<string>();

            string userDir = GetValidatedUserRootPath(userName);
            Directory.CreateDirectory(userDir);
            string dbPath = Path.Combine(userDir, "userdata.db");

            if (!File.Exists(dbPath)) {
                return Array.Empty<string>();
            }

            await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string query = "SELECT folder_name FROM Subscriptions;";

            await using var cmd = new SqliteCommand(query, connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                subscribedFolders.Add(reader.GetString(0));
            }

            return subscribedFolders.ToArray();
        }

        #endregion

        #region method SubscribeFolderAsync

        /// <summary>
        /// Asynchronously subscribes a user to a specified mailbox folder by recording it in their local user metadata
        /// SQLite database. Automatically ensures the user directory, database file, and subscriptions table exist.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder to subscribe to.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task SubscribeFolderAsync(
            string userName,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string userDir = GetValidatedUserRootPath(userName);
            Directory.CreateDirectory(userDir);
            string dbPath = Path.Combine(userDir, "userdata.db");

            await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var createCmd = new SqliteCommand(
                "CREATE TABLE IF NOT EXISTS Subscriptions (folder_name TEXT PRIMARY KEY);",
                connection))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insertCmd = new SqliteCommand(
                "INSERT OR IGNORE INTO Subscriptions (folder_name) VALUES (@folderName);",
                connection))
            {
                insertCmd.Parameters.AddWithValue("@folderName", folderName);
                await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        #endregion

        #region method UnsubscribeFolderAsync

        /// <summary>
        /// Asynchronously removes a user's subscription to a specified mailbox folder by deleting the record
        /// from their local user metadata SQLite database. Safely ensures the user directory,
        /// database file, and subscriptions table exist before executing the deletion.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder to unsubscribe from.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task UnsubscribeFolderAsync(
            string userName,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string userDir = GetValidatedUserRootPath(userName);
            Directory.CreateDirectory(userDir);
            string dbPath = Path.Combine(userDir, "userdata.db");

            await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var createCmd = new SqliteCommand(
                "CREATE TABLE IF NOT EXISTS Subscriptions (folder_name TEXT PRIMARY KEY);",
                connection))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var deleteCmd = new SqliteCommand(
                "DELETE FROM Subscriptions WHERE folder_name = @folderName;",
                connection))
            {
                deleteCmd.Parameters.AddWithValue("@folderName", folderName);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        #endregion

        #region method SearchAsync

        /// <summary>
        /// Asynchronously executes an IMAP search query against a specified folder's message cache by recursively
        /// analyzing the search criteria tree, handling sequence set dependencies via a Common Table Expression (CTE),
        /// and translating criteria into a parameterized SQLite command.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder being searched.</param>
        /// <param name="searchKey">The root <see cref="IMAP_t_Search_Key"/> criteria tree specifying the search conditions (e.g., flags, text, dates, sequence sets).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A list of message unique identifiers (<see cref="long"/> representing <c>MessageUid</c>) that match the evaluated search criteria.
        /// </returns>
        public async Task<List<long>> SearchAsync(
            string userName,
            string folderName,
            IMAP_t_Search_Key searchKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if search criteria contains IMAP_t_Search_Key_SeqSet.
            bool needsSeq = false;
            var queue = new Queue<IMAP_t_Search_Key>();
            queue.Enqueue(searchKey);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is IMAP_t_Search_Key_SeqSet) {
                    needsSeq = true;
                    break;
                }

                // Enqueue child nodes for inspection
                if (current is IMAP_t_Search_Key_Not notKey && notKey.SearchKey != null) {
                    queue.Enqueue(notKey.SearchKey);
                }
                else if (current is IMAP_t_Search_Key_Or orKey) {
                    if (orKey.SearchKey1 != null) queue.Enqueue(orKey.SearchKey1);
                    if (orKey.SearchKey2 != null) queue.Enqueue(orKey.SearchKey2);
                }
                else if (current is IMAP_t_Search_Key_Group groupKey && groupKey.Keys != null) {
                    foreach (var k in groupKey.Keys) {
                        if (k != null) queue.Enqueue(k);
                    }
                }
            }

            var parameters = new List<SqliteParameter>();
            int paramIndex = 0;

            // 1. Resolve target folder directory and enforce creation rules
            string userFolderDirectory = GetValidatedUserFolderPath(userName, folderName);

            string whereClause = TranslateKey(searchKey, parameters, ref paramIndex);

            string sql = needsSeq
                ? @"WITH numbered_messages AS (
                        SELECT MessageUid, ROW_NUMBER() OVER (ORDER BY MessageUid ASC) as seq_num,
                               MessageFlags, MessageSize, MessageInternalDate, MessageDate,
                               MessageSubject, MessageSender, MessageRecipient, MessageCc, MessageBcc, HeadersJson
                        FROM FolderMessageCache
                    )
                    SELECT MessageUid FROM numbered_messages"
                : "SELECT MessageUid FROM FolderMessageCache";

            if (!string.IsNullOrEmpty(whereClause) && whereClause != "1 = 1") {
                sql += " WHERE " + whereClause;
            }

            var matchingUids = new List<long>();

            using var connection = GetFolderCacheConnection(userFolderDirectory);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(parameters.ToArray());

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                matchingUids.Add(reader.GetInt64(0));
            }

            return matchingUids;
        }

        #region method TranslateKey

        private static string TranslateKey(IMAP_t_Search_Key key, List<SqliteParameter> parameters, ref int paramIndex)
        {
            if (key == null) return "1 = 1";

            if (key is IMAP_t_Search_Key_All)
                return "1 = 1";

            if (key is IMAP_t_Search_Key_Answered) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Answered%"));
                return $"MessageFlags LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Unanswered) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Answered%"));
                return $"NOT (MessageFlags LIKE @p{idx})";
            }

            if (key is IMAP_t_Search_Key_Deleted) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Deleted%"));
                return $"MessageFlags LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Undeleted) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Deleted%"));
                return $"NOT (MessageFlags LIKE @p{idx})";
            }

            if (key is IMAP_t_Search_Key_Draft) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Draft%"));
                return $"MessageFlags LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Undraft) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Draft%"));
                return $"NOT (MessageFlags LIKE @p{idx})";
            }

            if (key is IMAP_t_Search_Key_Flagged) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Flagged%"));
                return $"MessageFlags LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Unflagged) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Flagged%"));
                return $"NOT (MessageFlags LIKE @p{idx})";
            }

            if (key is IMAP_t_Search_Key_Seen) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Seen%"));
                return $"MessageFlags LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Unseen) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", "%\\Seen%"));
                return $"NOT (MessageFlags LIKE @p{idx})";
            }

            if (key is IMAP_t_Search_Key_Keyword kw) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{kw.Value}%"));
                return $"MessageFlags LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Unkeyword unw) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{unw.Value}%"));
                return $"NOT (MessageFlags LIKE @p{idx})";
            }

            if (key is IMAP_t_Search_Key_Larger larger) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", larger.Value));
                return $"MessageSize > @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Smaller smaller) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", smaller.Value));
                return $"MessageSize < @p{idx}";
            }

            if (key is IMAP_t_Search_Key_Before before) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", before.Date.ToString("yyyy-MM-dd")));
                return $"date(MessageInternalDate) < @p{idx}";
            }
            if (key is IMAP_t_Search_Key_On on) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", on.Date.ToString("yyyy-MM-dd")));
                return $"date(MessageInternalDate) = @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Since since) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", since.Date.ToString("yyyy-MM-dd")));
                return $"date(MessageInternalDate) >= @p{idx}";
            }

            if (key is IMAP_t_Search_Key_SentBefore sBefore) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", sBefore.Date.ToString("yyyy-MM-dd")));
                return $"date(MessageDate) < @p{idx}";
            }
            if (key is IMAP_t_Search_Key_SentOn sOn) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", sOn.Date.ToString("yyyy-MM-dd")));
                return $"date(MessageDate) = @p{idx}";
            }
            if (key is IMAP_t_Search_Key_SentSince sSince) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", sSince.Date.ToString("yyyy-MM-dd")));
                return $"date(MessageDate) >= @p{idx}";
            }

            if (key is IMAP_t_Search_Key_Subject subj) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{subj.Value}%"));
                return $"COALESCE(MessageSubject, '') LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_From frm) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{frm.Value}%"));
                return $"COALESCE(MessageSender, '') LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_To to) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{to.Value}%"));
                return $"COALESCE(MessageRecipient, '') LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Cc cc) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{cc.Value}%"));
                return $"COALESCE(MessageCc, '') LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Bcc bcc) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{bcc.Value}%"));
                return $"COALESCE(MessageBcc, '') LIKE @p{idx}";
            }

            if (key is IMAP_t_Search_Key_Body body) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{body.Value}%"));
                return $"COALESCE(BodyText, '') LIKE @p{idx}";
            }
            if (key is IMAP_t_Search_Key_Text txt) {
                int idx = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{txt.Value}%"));
                return $"(COALESCE(HeadersJson, '') LIKE @p{idx} OR COALESCE(BodyText, '') LIKE @p{idx})";
            }
            if (key is IMAP_t_Search_Key_Header hdr) {
                int idxField = paramIndex++;
                int idxVal = paramIndex++;
                parameters.Add(new SqliteParameter($"@p{idxField}", hdr.FieldName));
                parameters.Add(new SqliteParameter($"@p{idxVal}", $"%{hdr.Value}%"));
                return $"(EXISTS (SELECT 1 FROM json_each(HeadersJson) WHERE lower(key) = lower(@p{idxField}) AND value LIKE @p{idxVal}))";
            }

            if (key is IMAP_t_Search_Key_Uid uidKey) {
                var clauses = new List<string>();
                foreach (var item in uidKey.Value.Items) {
                    if (item.Start != item.End) {
                        int idxStart = paramIndex++;
                        int idxEnd = paramIndex++;
                        parameters.Add(new SqliteParameter($"@p{idxStart}", item.Start));
                        parameters.Add(new SqliteParameter($"@p{idxEnd}", item.End));
                        clauses.Add($"(MessageUid BETWEEN @p{idxStart} AND @p{idxEnd})");
                    }
                    else {
                        int idxVal = paramIndex++;
                        parameters.Add(new SqliteParameter($"@p{idxVal}", item.Start));
                        clauses.Add($"(MessageUid = @p{idxVal})");
                    }
                }
                return clauses.Count == 0 ? "0 = 1" : "(" + string.Join(" OR ", clauses) + ")";
            }

            if (key is IMAP_t_Search_Key_SeqSet seqKey) {
                var clauses = new List<string>();
                foreach (var item in seqKey.Value.Items) {
                    if (item.Start != item.End) {
                        int idxStart = paramIndex++;
                        int idxEnd = paramIndex++;
                        parameters.Add(new SqliteParameter($"@p{idxStart}", item.Start));
                        parameters.Add(new SqliteParameter($"@p{idxEnd}", item.End));
                        clauses.Add($"(seq_num BETWEEN @p{idxStart} AND @p{idxEnd})");
                    }
                    else {
                        int idxVal = paramIndex++;
                        parameters.Add(new SqliteParameter($"@p{idxVal}", item.Start));
                        clauses.Add($"(seq_num = @p{idxVal})");
                    }
                }
                return clauses.Count == 0 ? "0 = 1" : "(" + string.Join(" OR ", clauses) + ")";
            }

            if (key is IMAP_t_Search_Key_Recent) return "0 = 1"; // IMAPrev1 compatibility
            if (key is IMAP_t_Search_Key_New)    return "0 = 1"; // IMAPrev1 compatibility
            if (key is IMAP_t_Search_Key_Old)    return "1 = 1"; // IMAPrev1 compatibility

            if (key is IMAP_t_Search_Key_Not notKey) {
                return $"NOT ({TranslateKey(notKey.SearchKey, parameters, ref paramIndex)})";
            }

            if (key is IMAP_t_Search_Key_Or orKey) {
                string left = TranslateKey(orKey.SearchKey1, parameters, ref paramIndex);
                string right = TranslateKey(orKey.SearchKey2, parameters, ref paramIndex);
                return $"({left} OR {right})";
            }

            if (key is IMAP_t_Search_Key_Group groupKey) {
                if (groupKey.Keys.Length == 0) return "1 = 1";

                var subClauses = new List<string>();
                foreach (var k in groupKey.Keys) {
                    subClauses.Add(TranslateKey(k, parameters, ref paramIndex));
                }
                return "(" + string.Join(" AND ", subClauses) + ")";
            }

            throw new NotSupportedException($"Search key '{key?.GetType().Name}' is not supported.");
        }

        #endregion

        #endregion

        #region method CopyMessageAsync

        /// <summary>
        /// Asynchronously copies a specified message from a source folder to a target folder within a user's mailbox, 
        /// preserving its original message flags and storing it with the current timestamp.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the source folder containing the message.</param>
        /// <param name="messageUid">The unique identifier (UID) of the message to be copied.</param>
        /// <param name="targetFolder">The relative path or name of the destination folder.</param>
        /// <returns>
        /// A task representing the asynchronous operation, containing the new message unique identifier 
        /// assigned in the target folder, or <see langword="null"/> if the source message stream could not be found.
        /// </returns>
        public async Task<long?> CopyMessageAsync(string userName, string folderName, long messageUid, string targetFolder)
        {
            using Stream? msgStream = await GetMessageStreamAsync(userName, folderName, messageUid, false);
            if (msgStream != null) {
                var messageInfo = await GetMessageInfoAsync(userName, folderName, messageUid);
                var messageFlags = messageInfo != null ? messageInfo.Value.MessageFlags : [];

                return await StoreMessageAsync(userName, targetFolder, msgStream, messageFlags, DateTime.Now);
            }

            return null;
        }

        #endregion

        #region method DeleteMessageAsync

        /// <summary>
        /// Deletes an email message from the file system and its corresponding metadata cache, 
        /// updating the user's storage quota and advancing the folder metadata version.
        /// </summary>
        /// <param name="userName">The username owning the mailbox folder.</param>
        /// <param name="folderName">The name or path of the mailbox folder.</param>
        /// <param name="messageUid">The unique identifier (UID) of the message to be deleted.</param>
        /// <returns><c>true</c> if the message was successfully removed from disk and cache; otherwise, <c>false</c>.</returns>
        public async Task<bool> DeleteMessageAsync(string userName, string folderName, long messageUid)
        {
            // 1. Resolve target folder directory and enforce creation rules
            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);

            // Construct the physical file path: MailStore/userName/folderName/uid.eml
            string emlFilePath = Path.Combine(userFolderPath, $"{messageUid}.eml");

            // 2. Attempt to delete the physical .eml file from disk if it exists
            long messageSize = 0;
            if (File.Exists(emlFilePath)) {
                try {
                    messageSize = new FileInfo(emlFilePath).Length;
                    File.Delete(emlFilePath);
                }
                catch (Exception) {
                    // File delete failed -> abort immediately and DO NOT touch the SQLite cache
                    return false;
                }

                await _dbService.UpdateUserStorageUsageAsync(userName, -messageSize);
            }

            const string deleteSql = "DELETE FROM FolderMessageCache WHERE MessageUid = @uid;";
            const string updateFolderSql = "UPDATE FolderInfo SET Version = @version;";

            using var connection = GetFolderCacheConnection(userFolderPath);
            await connection.OpenAsync();

            // 3. Remove message from SQLite cache
            using (var deleteCommand = new SqliteCommand(deleteSql, connection))
            {
                deleteCommand.Parameters.AddWithValue("@uid", messageUid);
                int rowsAffected = await deleteCommand.ExecuteNonQueryAsync();            

                if (rowsAffected > 0)
                {
                    // 4. Update folder metadata version using UTC ticks
                    long newVersion = DateTime.UtcNow.Ticks;

                    using (var updateFolderCommand = new SqliteCommand(updateFolderSql, connection))
                    {
                        updateFolderCommand.Parameters.AddWithValue("@version", newVersion);
                        await updateFolderCommand.ExecuteNonQueryAsync();
                    }

                    return true;
                }

                return false;
            }
        }

        #endregion

        #region method GetMessageStreamAsync

        /// <summary>
        /// Asynchronously prepares and retrieves an optimized, sequential file stream for a specific email message (<c>.eml</c>)
        /// located within a user's mailbox folder, with an optional capability to automatically mark the message as seen.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder containing the message.</param>
        /// <param name="messageUid">The unique identifier (UID) of the message whose stream is being requested.</param>
        /// <param name="markAsSeen">
        /// Specifies whether to automatically append the <c>\Seen</c> system flag to the message
        /// upon successful stream retrieval.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An optimized, sequential <see cref="FileStream"/> configured for asynchronous reading of the raw <c>.eml</c> file,
        /// or <see langword="null"/> if the target message file does not exist on disk.
        /// </returns>
        public async Task<Stream?> GetMessageStreamAsync(
            string userName,
            string folderName,
            long messageUid,
            bool markAsSeen,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Resolve target folder directory and enforce creation rules
            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);

            // Construct the physical file path: MailStore/userName/folderName/uid.eml
            string emlFilePath = Path.Combine(userFolderPath, $"{messageUid}.eml");

            if (!File.Exists(emlFilePath)) {
                return null;
            }

            if (markAsSeen) {
                await StoreMessageFlagsAsync(
                    userName,
                    folderName,
                    messageUid,
                    ["\\Seen"],
                    FlagStoreMode.Add,
                    cancellationToken).ConfigureAwait(false);
            }

            // Open an optimized sequential FileStream for efficient, low-memory streaming
            return new FileStream(
                emlFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        #endregion

        #region method GetMessageInfoAsync

        /// <summary>
        /// Asynchronously retrieves cached metadata and parsing structures for a specific message by its unique identifier (UID)
        /// from the specified folder's local SQLite cache database.
        /// </summary>
        /// <param name="userName">The unique username or account identifier associated with the mailbox.</param>
        /// <param name="folderName">The relative path or name of the folder containing the message.</param>
        /// <param name="uid">The unique identifier (UID) of the message whose metadata is being queried.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A populated <see cref="MessageInfo"/> instance containing the message metadata, envelope, and body structure,
        /// or <see langword="null"/> if no message with the specified UID exists in the cache.
        /// </returns>
        public async Task<MessageInfo?> GetMessageInfoAsync(
            string userName,
            string folderName,
            long uid,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Resolve target folder directory and enforce creation rules
            string userFolderPath = GetValidatedUserFolderPath(userName, folderName);

            const string sql = @"
                SELECT
                    MessageUid, Version, MessageInternalDate, MessageDate, MessageSize,
                    MessageFlags, MessageSender, MessageRecipient, MessageCc, MessageBcc,
                    MessageSubject, Envelope, BodyStructure
                FROM FolderMessageCache
                WHERE MessageUid = @uid;
            ";

            await using var connection = GetFolderCacheConnection(userFolderPath);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@uid", uid);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                return new MessageInfo(
                    MessageUid: reader.GetInt64(0),
                    Version: reader.GetInt64(1),
                    MessageInternalDate: DateTime.Parse(reader.GetString(2)),
                    MessageSize: reader.GetInt64(4),
                    MessageFlags: reader.IsDBNull(5)
                        ? []
                        : reader.GetString(5).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    MessageDate: reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                    MessageSender: reader.IsDBNull(6) ? null : reader.GetString(6),
                    MessageTo: reader.IsDBNull(7) ? null : reader.GetString(7),
                    MessageCc: reader.IsDBNull(8) ? null : reader.GetString(8),
                    MessageBcc: reader.IsDBNull(9) ? null : reader.GetString(9),
                    MessageSubject: reader.IsDBNull(10) ? null : reader.GetString(10),
                    Envelope: IMAP_t_Envelope.ParseFromString(reader.GetString(11)),
                    BodyStructure: IMAP_t_BodyStructure.ParseFromString(reader.GetString(12))
                );
            }

            return null;
        }

        #endregion

        #region mehtod StoreMessageAsync

        /// <summary>
        /// Asynchronously stores an incoming email message, parsing its metadata and MIME structure, 
        /// saving the raw content to disk, inserting its metadata into the per-folder SQLite cache, 
        /// updating the user's storage quota, and advancing the folder metadata version.
        /// </summary>
        /// <param name="userName">The username owning the mailbox folder.</param>
        /// <param name="folderName">The name or path of the mailbox folder.</param>
        /// <param name="messageStream">A seekable stream containing the raw RFC822/MIME message.</param>
        /// <param name="flags">The initial flags to assign to the message.</param>
        /// <param name="internalDate">The internal date/time to record for the message if not specified in headers.</param>
        /// <returns>A task that represents the asynchronous operation, containing the assigned unique identifier (UID) of the stored message.</returns>
        public async Task<long> StoreMessageAsync(string userName, string folderName, Stream messageStream, string[] flags, DateTime internalDate)
        {
            // 1. Reset stream position (stream is guaranteed seekable) and get exact size
            messageStream.Position = 0;
            long messageSize = messageStream.Length;

            // 2. Parse and validate
            Mail_Message mailMessage;
            try {
                mailMessage = await Mail_Message.ParseFromStreamAsync(messageStream, true, false);
            }
            catch (Exception ex) {
                throw new InvalidOperationException($"Message parsing failed; store rejected: {ex.Message}", ex);
            }

            // 3. Resolve target folder directory and enforce creation rules
            string userFolderDirectory = GetValidatedUserFolderPath(userName, folderName);
            bool folderExists = Directory.Exists(userFolderDirectory);

            if (folderName.Equals("Inbox", StringComparison.OrdinalIgnoreCase)) {
                if (!folderExists) {
                    Directory.CreateDirectory(userFolderDirectory);
                    InitializeFolderCacheSchema(userFolderDirectory);
                }
            }
            else {
                if (!folderExists) {
                    throw new DirectoryNotFoundException($"Folder '{folderName}' does not exist. Message store rejected.");
                }
            }

            // 4. Extract flat search and metadata fields from the parsed LumiSoft message
            string subject = mailMessage.Subject ?? "";
            string sender = mailMessage.From?.ToString() ?? "";
            string to = mailMessage.To?.ToString() ?? "";
            string cc = mailMessage.Cc?.ToString() ?? "";
            string bcc = mailMessage.Bcc?.ToString() ?? "";
            string flagsStr = flags != null ? string.Join(" ", flags) : string.Empty;
            DateTime messageDate = mailMessage.Date != null ? mailMessage.Date.Value : internalDate;
            string bodyText = mailMessage.BodyText ?? "";
            if (bodyText != null && bodyText.Length > 16384) {
                bodyText = bodyText.Substring(0, 16384);
            }

            // 5. Build lowercase-normalized JSON dictionary supporting multiple headers with the same name
            var headersDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (MIME_h header in mailMessage.Header) {
                string headerName = header.Name.Trim().ToLowerInvariant();

                // 1. Drop completely useless transport/security bloat
                if (IgnoredHeaders.Contains(headerName)) {
                    continue;
                }

                string headerValue = header.ValueToString().Trim() ?? string.Empty;

                // 2. Apply length guardrail to unknown/custom headers
                if (!WhitelistedHeaders.Contains(headerName)) {
                    if (headerValue.Length > MaxUnknownHeaderLength) {
                        headerValue = headerValue.Substring(0, MaxUnknownHeaderLength);
                    }
                }

                // 3. Add to multi-value array list
                if (!headersDict.TryGetValue(headerName, out var valueList)) {
                    valueList = new List<string>();
                    headersDict[headerName] = valueList;
                }

                valueList.Add(headerValue);
            }
            string headersJson = JsonSerializer.Serialize(headersDict);

            // 6. Serialize IMAP Envelope and BodyStructure
            string envelope = IMAP_t_Envelope.FromMessage(mailMessage).ToString();
            string bodyStructure = IMAP_t_BodyStructure.FromMessage(mailMessage).ToString();

            long assignedUid;
            long newVersion = DateTime.UtcNow.Ticks;

            // 7. Insert metadata into the per-folder SQLite cache first to acquire the auto-incremented UID
            using (var connection = GetFolderCacheConnection(userFolderDirectory)) {
                await connection.OpenAsync();

                const string insertSql = @"
                    INSERT INTO FolderMessageCache (
                        Version,
                        MessageInternalDate,
                        MessageDate,
                        MessageSize,
                        MessageFlags,
                        MessageSender,
                        MessageRecipient,
                        MessageCc,
                        MessageBcc,
                        MessageSubject,
                        HeadersJson,
                        BodyText,
                        Envelope,
                        BodyStructure
                    ) VALUES (
                        @version,
                        @internalDate,
                        @messageDate,
                        @messageSize,
                        @flags,
                        @sender,
                        @to,
                        @cc,
                        @bcc,
                        @subject,
                        @headersJson,
                        @bodyText,
                        @envelope,
                        @bodyStructure
                    );
                    SELECT last_insert_rowid();
                ";

                using (var cmd = new SqliteCommand(insertSql, connection)) {
                    cmd.Parameters.AddWithValue("@version", newVersion);
                    cmd.Parameters.AddWithValue("@internalDate", internalDate.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    cmd.Parameters.AddWithValue("@messageDate", messageDate.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    cmd.Parameters.AddWithValue("@messageSize", messageSize);
                    cmd.Parameters.AddWithValue("@flags", flagsStr);
                    cmd.Parameters.AddWithValue("@sender", sender);
                    cmd.Parameters.AddWithValue("@to", to);
                    cmd.Parameters.AddWithValue("@cc", cc);
                    cmd.Parameters.AddWithValue("@bcc", bcc);
                    cmd.Parameters.AddWithValue("@subject", subject);
                    cmd.Parameters.AddWithValue("@headersJson", headersJson);
                    cmd.Parameters.AddWithValue("@bodyText", bodyText);
                    cmd.Parameters.AddWithValue("@envelope", envelope);
                    cmd.Parameters.AddWithValue("@bodyStructure", bodyStructure);

                    assignedUid = (long)(await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException("Failed to retrieve assigned message UID."));
                }

                // 8. Update folder metadata version to match the ticks timestamp
                const string updateFolderSql = "UPDATE FolderInfo SET Version = @version;";
                using (var updateFolderCmd = new SqliteCommand(updateFolderSql, connection)) {
                    updateFolderCmd.Parameters.AddWithValue("@version", newVersion);
                    await updateFolderCmd.ExecuteNonQueryAsync();
                }
            }

            // 9. Save the physical .eml file to disk deterministically as {UID}.eml
            string finalFileName = $"{assignedUid}.eml";
            string finalFilePath = Path.Combine(userFolderDirectory, finalFileName);

            messageStream.Position = 0;
            using (var fileStream = new FileStream(finalFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true)) {
                await messageStream.CopyToAsync(fileStream);
            }

            await _dbService.UpdateUserStorageUsageAsync(userName, messageSize);

            return assignedUid;
        }

        #endregion

        #region method StoreMessageFlagsAsync

        /// <summary>
        /// Asynchronously updates the message flags for a specific email message within a folder cache,
        /// applying changes according to the specified mode and updating both the message
        /// record and the folder metadata version using a shared UTC ticks timestamp.
        /// </summary>
        /// <param name="userName">The username owning the mailbox folder.</param>
        /// <param name="folderName">The name or path of the mailbox folder.</param>
        /// <param name="messageUid">The unique identifier (UID) of the message whose flags are being modified.</param>
        /// <param name="flags">The collection of flags to add, remove, or use as a replacement.</param>
        /// <param name="setMode">The operation mode defining how the flags are modified (<see cref="FlagStoreMode.Replace"/>, <see cref="FlagStoreMode.Add"/>, or <see cref="FlagStoreMode.Remove"/>).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task StoreMessageFlagsAsync(
            string userName,
            string folderName,
            long messageUid,
            IEnumerable<string> flags,
            FlagStoreMode setMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Resolve target folder directory and enforce creation rules
            string userFolderDirectory = GetValidatedUserFolderPath(userName, folderName);

            await using var connection = GetFolderCacheConnection(userFolderDirectory);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 2. Fetch current flags from SQLite
            string? currentFlags = null;
            const string selectSql = "SELECT MessageFlags FROM FolderMessageCache WHERE MessageUid = @uid;";
            await using (var selectCmd = new SqliteCommand(selectSql, connection))
            {
                selectCmd.Parameters.AddWithValue("@uid", messageUid);
                object? result = await selectCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (result != null && result != DBNull.Value) {
                    currentFlags = result.ToString();
                }
            }

            // Parse into a case-insensitive set to prevent duplicate flags (e.g. \Seen vs \seen)
            var flagsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(currentFlags)) {
                foreach (string f in currentFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                    flagsSet.Add(f);
                }
            }

            bool modified = false;

            // 3. Apply changes based on the mode
            switch (setMode)
            {
                case FlagStoreMode.Replace:
                    var newSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in flags) {
                        if (!string.IsNullOrWhiteSpace(f)) {
                            newSet.Add(f.Trim());
                        }
                    }

                    if (!flagsSet.SetEquals(newSet)) {
                        flagsSet = newSet;
                        modified = true;
                    }
                    break;

                case FlagStoreMode.Add:
                    foreach (string f in flags) {
                        if (!string.IsNullOrWhiteSpace(f) && flagsSet.Add(f.Trim())) {
                            modified = true;
                        }
                    }
                    break;

                case FlagStoreMode.Remove:
                    foreach (string f in flags) {
                        if (!string.IsNullOrWhiteSpace(f) && flagsSet.Remove(f.Trim())) {
                            modified = true;
                        }
                    }
                    break;
            }

            // 4. Save back to database if modifications occurred
            if (modified) {
                string updatedFlagsString = flagsSet.Count > 0 ? string.Join(" ", flagsSet) : string.Empty;
                long newVersion = DateTime.UtcNow.Ticks;

                // Update the message record flags and set its version to the shared ticks timestamp
                const string updateMessageSql = @"
                    UPDATE FolderMessageCache
                    SET MessageFlags = @flags,
                        Version = @version
                    WHERE MessageUid = @uid;
                ";
                await using (var updateMsgCmd = new SqliteCommand(updateMessageSql, connection))
                {
                    updateMsgCmd.Parameters.AddWithValue("@flags", string.IsNullOrEmpty(updatedFlagsString) ? DBNull.Value : updatedFlagsString);
                    updateMsgCmd.Parameters.AddWithValue("@version", newVersion);
                    updateMsgCmd.Parameters.AddWithValue("@uid", messageUid);
                    await updateMsgCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Update the folder-level version to match the ticks timestamp
                const string updateFolderSql = @"
                    UPDATE FolderInfo
                    SET Version = @version;
                ";
                await using (var updateFolderCmd = new SqliteCommand(updateFolderSql, connection))
                {
                    updateFolderCmd.Parameters.AddWithValue("@version", newVersion);
                    await updateFolderCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        #endregion


        #region method GetValidatedUserRootPath

        /// <summary>
        /// Sanitizes the username and resolves the verified root storage directory for the user's mailbox,
        /// guarding against directory traversal vulnerabilities.
        /// </summary>
        /// <param name="username">The username or account identifier.</param>
        /// <returns>The verified, absolute root directory path for the user's mailbox.</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown if the resolved path escapes the mailboxes root directory.</exception>
        private string GetValidatedUserRootPath(string username)
        {
            string safeUsername = username.Trim().ToLowerInvariant()
                .Replace('@', '_')
                .Replace('.', '_');

            string mailboxesRoot = Path.GetFullPath(Path.Combine(_baseStoragePath, "Mailboxes"));
            string userBaseDirectory = Path.GetFullPath(Path.Combine(mailboxesRoot, safeUsername));

            string rootWithSep = mailboxesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!userBaseDirectory.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Access denied: Invalid username path traversal detected.");
            }

            return userBaseDirectory;
        }

        #endregion

        #region method GetValidatedUserFolderPath

        /// <summary>
        /// Sanitizes the username, resolves the absolute storage path for a specific folder,
        /// and guards against directory traversal vulnerabilities.
        /// </summary>
        /// <param name="username">The username or account identifier.</param>
        /// <param name="folderName">The target folder name.</param>
        /// <returns>The verified, absolute directory path for the user folder.</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown if the resolved path escapes the user's root directory.</exception>
        private string GetValidatedUserFolderPath(string username, string folderName)
        {
            string userBaseDirectory = GetValidatedUserRootPath(username);
            string targetDirectory = Path.GetFullPath(Path.Combine(userBaseDirectory, folderName));

            string baseWithSep = userBaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!targetDirectory.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase) &&
                !targetDirectory.Equals(userBaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Access denied: Invalid folder path traversal detected.");
            }

            return targetDirectory;
        }

        #endregion

        #region method GetFolderCacheConnection

        /// <summary>
        /// Opens and returns an active SQLite database connection pointing to the folder's local cache file (<c>_cache.db</c>) 
        /// located within the specified folder directory.
        /// </summary>
        /// <param name="userFolderPath">The physical directory path on disk where the folder's cache database resides.</param>
        /// <returns>
        /// An opened <see cref="SqliteConnection"/> instance ready for executing queries or commands against the folder cache.
        /// </returns>
        private SqliteConnection GetFolderCacheConnection(string userFolderPath)
        {
            string dbPath = Path.Combine(userFolderPath, "_cache.db");

            var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            return connection;
        }

        #endregion

        #region method InitializeFolderCacheSchema

        /// <summary>
        /// Initializes the local SQLite cache database schema for a mailbox folder by creating 
        /// the mandatory <c>FolderInfo</c> and <c>FolderMessageCache</c> tables if they do not already exist, 
        /// and populating an initial metadata record with a unique UID validity and version timestamp.
        /// </summary>
        /// <param name="userFolderPath">The physical directory path on disk where the folder's cache database resides.</param>
        private void InitializeFolderCacheSchema(string userFolderPath)
        {
            using var connection = GetFolderCacheConnection(userFolderPath);
            using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS FolderInfo (
                    UidValidity INTEGER PRIMARY KEY,
                    Version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS FolderMessageCache (
                    MessageUid INTEGER PRIMARY KEY,
                    Version INTEGER NOT NULL,
                    MessageInternalDate TEXT NOT NULL,
                    MessageDate TEXT NOT NULL,
                    MessageSize INTEGER NOT NULL,
                    MessageFlags TEXT,
                    MessageSender TEXT,
                    MessageRecipient TEXT,
                    MessageCc TEXT,
                    MessageBcc TEXT,
                    MessageSubject TEXT,
                    HeadersJson TEXT,
                    BodyText TEXT,
                    Envelope TEXT,
                    BodyStructure TEXT
                );
            ";
            cmd.ExecuteNonQuery();

            // Ensure an initial metadata row exists only if the table is currently empty
            cmd.CommandText = @"
                INSERT OR IGNORE INTO FolderInfo (UidValidity, Version) 
                SELECT @uidValidity, @version 
                WHERE NOT EXISTS (SELECT 1 FROM FolderInfo);
            ";
    
            long initialTicks = DateTime.UtcNow.Ticks;
            cmd.Parameters.AddWithValue("@uidValidity", (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@version", DateTime.UtcNow.Ticks);
    
            cmd.ExecuteNonQuery();
        }

        #endregion

    }
}
