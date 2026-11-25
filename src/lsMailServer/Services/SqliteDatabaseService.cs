using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace lsMailServer.Services 
{
    /// <summary>
    /// Manages SQLite database operations, connection provisioning, and data persistence for the application.
    /// </summary>
    public class SqliteDatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private const int SaltSize = 16; // 128-bit
        private const int KeySize = 32;  // 256-bit
        private const int Iterations = 100_000;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqliteDatabaseService"/> class,
        /// configuring the physical directory path where SQLite database files are stored.
        /// </summary>
        /// <param name="dataDirectory">The directory path where database files and local metadata caches are persisted.</param>
        public SqliteDatabaseService(string dataDirectory)
        {
            // Path to the per-instance SQLite database
            string dbPath = Path.Combine(dataDirectory, "mailserver.db");

            _connectionString = $"Data Source={dbPath}";
        
            EnsureDatabaseCreated();
            EnsureDatabaseSchema();
        }

        #region method CreateConnection

        /// <summary>
        /// Opens a new SQLite connection using the configured connection string and applies baseline connection pragmas.
        /// </summary>
        /// <returns>An open <see cref="SqliteConnection"/> with foreign keys enabled, WAL journaling, and normal synchronous mode.</returns>
        private SqliteConnection CreateConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Enable SQLite performance and integrity pragmas per connection
            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = @"
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
            ";
            pragmaCmd.ExecuteNonQuery();

            return connection;
        }

        #endregion

        #region method EnsureDatabaseCreated

        /// <summary>
        /// Ensures that the required database tables and indexes exist for domains, users, and user email addresses.
        /// </summary>
        private void EnsureDatabaseCreated()
        {
            using var connection = CreateConnection();

            string schemaSql = @"

                CREATE TABLE IF NOT EXISTS Domains (
                    DomainName TEXT PRIMARY KEY COLLATE NOCASE,
                    Description TEXT NULL,
                    Enabled INTEGER DEFAULT 1,
                    CreateDate TEXT NOT NULL,
                    DkimEnabled INTEGER NOT NULL DEFAULT 0,
                    DkimSelector TEXT NULL,
                    DkimPrivateKey TEXT NULL,
                    DkimPublicKey TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS Users (
                    UserId INTEGER PRIMARY KEY,
                    UserName TEXT UNIQUE NOT NULL COLLATE NOCASE,
                    PasswordHash TEXT NOT NULL,
                    PasswordSalt TEXT,
                    Description TEXT NULL,
                    QuotaMailboxSize INTEGER NOT NULL,
                    QuotaCurrentUsageBytes INTEGER NOT NULL DEFAULT 0,
                    QuotaMaxDailySends INTEGER NOT NULL,
                    QuotaSentCountToday INTEGER NOT NULL DEFAULT 0,
                    QuotaLastSendDate TEXT NOT NULL,
                    AllowedSendAs TEXT NULL,
                    CreateDate TEXT NOT NULL,
                    LastLoginDate TEXT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS UserEmailAddresses (
                    EmailAddress TEXT PRIMARY KEY COLLATE NOCASE,
                    UserId INTEGER NOT NULL,
                    CreateDate TEXT NOT NULL,
                    FOREIGN KEY (UserId) REFERENCES Users(UserId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_UserEmailAddresses_UserId ON UserEmailAddresses(UserId);
            ";

            using var command = connection.CreateCommand();
            command.CommandText = schemaSql;
            command.ExecuteNonQuery();
        }

        #endregion

        #region method EnsureDatabaseSchema

        /// <summary>
        /// Applies incremental, idempotent schema updates (such as adding new columns or indexes)
        /// to an existing SQLite database so older installations automatically upgrade on startup.
        /// </summary>
        private void EnsureDatabaseSchema()
        {
            using var connection = CreateConnection();

            // v1.1 - Send-As / Send-on-Behalf delegation
            EnsureColumnExists(connection, "Users", "AllowedSendAs", "TEXT NULL");
        }

        #endregion

        #region method EnsureColumnExists

        /// <summary>
        /// Synchronously checks whether <paramref name="columnName"/> exists on SQLite table <paramref name="tableName"/>
        /// and executes <c>ALTER TABLE ... ADD COLUMN ...</c> if it is missing.
        /// </summary>
        /// <param name="connection">An open (or openable) SQLite <see cref="DbConnection"/>.</param>
        /// <param name="tableName">The target SQLite table name (e.g., <c>"Users"</c>).</param>
        /// <param name="columnName">The target column name (e.g., <c>"AllowedSendAs"</c>).</param>
        /// <param name="columnDefinition">The SQLite column type and constraints (e.g., <c>"TEXT NULL"</c>).</param>
        /// <returns><see langword="true"/> if the column was added; <see langword="false"/> if it already existed.</returns>
        public static bool EnsureColumnExists(
            DbConnection connection,
            string tableName,
            string columnName,
            string columnDefinition)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
            ArgumentException.ThrowIfNullOrWhiteSpace(columnDefinition);

            if (connection.State != ConnectionState.Open) {
                connection.Open();
            }

            string cleanTable = tableName.Replace("\"", string.Empty).Trim();
            string cleanColumn = columnName.Replace("\"", string.Empty).Trim();

            using (DbCommand checkCmd = connection.CreateCommand()) {
                checkCmd.CommandText = $"SELECT 1 FROM pragma_table_info('{cleanTable.Replace("'", "''")}') WHERE lower(name) = lower(@col) LIMIT 1;";
                DbParameter colParam = checkCmd.CreateParameter();
                colParam.ParameterName = "@col";
                colParam.Value = cleanColumn;
                checkCmd.Parameters.Add(colParam);

                object? exists = checkCmd.ExecuteScalar();
                if (exists != null && exists != DBNull.Value) {
                    return false;
                }
            }

            using DbCommand alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE \"{cleanTable}\" ADD COLUMN \"{cleanColumn}\" {columnDefinition};";
            alterCmd.ExecuteNonQuery();

            return true;
        }

        #endregion


                
        #region method GetDomainsAsync

        /// <summary>
        /// Asynchronously retrieves mail domains from the database with optional filtering.
        /// </summary>
        /// <param name="isEnabled">Optional enabled status filter.</param>
        /// <param name="search">Optional domain name search string.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of matching <see cref="DomainInfo"/> objects ordered by domain name.</returns>
        public async Task<List<DomainInfo>> GetDomainsAsync(
            bool? isEnabled = null,
            string? search = null,
            CancellationToken cancellationToken = default)
        {
            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            var sql = new StringBuilder(
                "SELECT DomainName, Description, Enabled, DkimEnabled, DkimSelector, DkimPublicKey FROM Domains WHERE 1=1");

            if (isEnabled.HasValue) {
                sql.Append(" AND Enabled = @isEnabled");
                cmd.Parameters.AddWithValue("@isEnabled", isEnabled.Value ? 1 : 0);
            }

            if (!string.IsNullOrWhiteSpace(search)) {
                sql.Append(" AND DomainName LIKE @search");
                cmd.Parameters.AddWithValue("@search", $"%{search.Trim().ToLowerInvariant()}%");
            }

            sql.Append(" ORDER BY DomainName ASC;");
            cmd.CommandText = sql.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var domains = new List<DomainInfo>();

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                domains.Add(new DomainInfo(
                    Name: reader.GetString(0),
                    Description: reader.IsDBNull(1) ? null : reader.GetString(1),
                    IsEnabled: reader.GetInt32(2) == 1,
                    IsDkimEnabled: reader.GetInt32(3) == 1,
                    DkimSelector: reader.IsDBNull(4) ? null : reader.GetString(4),
                    DkimPublicKey: reader.IsDBNull(5) ? null : reader.GetString(5),
                    null
                ));
            }

            return domains;
        }

        #endregion

        #region method GetDomainAsync

        /// <summary>
        /// Asynchronously retrieves a single mail domain by its name from the database.
        /// </summary>
        /// <param name="domainName">The normalized domain name.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="DomainInfo"/> if found; otherwise, <c>null</c>.</returns>
        public async Task<DomainInfo?> GetDomainAsync(string domainName, CancellationToken cancellationToken = default)
        {
            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT DomainName, Description, Enabled, DkimEnabled, DkimSelector, DkimPublicKey, DkimPrivateKey
                FROM Domains
                WHERE DomainName = @domainName;";
            cmd.Parameters.AddWithValue("@domainName", domainName.Trim().ToLowerInvariant());

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                return new DomainInfo(
                    Name: reader.GetString(0),
                    Description: reader.IsDBNull(1) ? null : reader.GetString(1),
                    IsEnabled: reader.GetInt32(2) == 1,
                    IsDkimEnabled: reader.GetInt32(3) == 1,
                    DkimSelector: reader.IsDBNull(4) ? null : reader.GetString(4),
                    DkimPublicKey: reader.IsDBNull(5) ? null : reader.GetString(5),
                    DkimPrivateKey: reader.IsDBNull(6) ? null : reader.GetString(6)
                );
            }

            return null;
        }

        #endregion

        #region method DomainExistsAsync

        /// <summary>
        /// Asynchronously checks if a domain exists in the database and is currently enabled.
        /// </summary>
        /// <param name="domainName">The name of the domain to check (normalized via Trim and ToLowerInvariant).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><c>true</c> if the domain exists and is enabled; otherwise, <c>false</c>.</returns>
        public async Task<bool> DomainExistsAsync(string domainName, CancellationToken cancellationToken = default)
        {
            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT COUNT(1) 
                FROM Domains 
                WHERE DomainName = @domainName AND Enabled = 1;
            ";
            cmd.Parameters.AddWithValue("@domainName", domainName.Trim().ToLowerInvariant());

            object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            long count = result is null or DBNull ? 0L : Convert.ToInt64(result);

            return count > 0;
        }

        #endregion

        #region method CreateDomainAsync

        /// <summary>
        /// Asynchronously creates a new domain record using the normalized domain name as the primary key.
        /// </summary>
        /// <param name="domainName">The target domain name (e.g., example.com). Will be trimmed and normalized to lowercase.</param>
        /// <param name="description">Optional human-readable notes or organizational description for the domain.</param>
        /// <param name="isEnabled">Indicates whether the domain routing is active (<see langword="true"/> for 1, <see langword="false"/> for 0).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="true"/> if the domain was successfully created;
        /// <see langword="false"/> if the domain name already exists in the database.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="domainName"/> is null or whitespace.</exception>
        public async Task<bool> CreateDomainAsync(string domainName, string? description, bool isEnabled, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(domainName)) {
                throw new ArgumentNullException(nameof(domainName), "Domain name cannot be null or empty.");
            }

            string normalizedDomain = domainName.Trim().ToLowerInvariant();
            string createDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            try {
                using var connection = CreateConnection();
                await using var cmd = connection.CreateCommand();

                cmd.CommandText = @"
                    INSERT INTO Domains (
                        DomainName, Description, Enabled, CreateDate
                    )
                    VALUES (
                        @domainName, @description, @enabled, @createDate
                    );
                ";

                cmd.Parameters.AddWithValue("@domainName", normalizedDomain);
                cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@enabled", isEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("@createDate", createDate);

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) {
                // Unique/primary key constraint violation (DomainName already exists)
                return false;
            }
        }

        #endregion

        #region method UpdateDomainAsync

        /// <summary>
        /// Asynchronously updates the description, active status, DKIM signing state, and DKIM selector
        /// of an existing domain identified by its name.
        /// </summary>
        /// <param name="domainName">The unique name of the domain to update (normalized via Trim and ToLowerInvariant).</param>
        /// <param name="description">The updated description or notes, or <see langword="null"/> to clear it.</param>
        /// <param name="isEnabled">Indicates whether the domain routing is active (<see langword="true"/> for 1, <see langword="false"/> for 0).</param>
        /// <param name="dkimEnabled">Indicates whether DKIM signing is enabled for outbound messages on this domain.</param>
        /// <param name="dkimSelector">Optional DKIM selector, or <see langword="null"/>/whitespace to clear it.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><see langword="true"/> if at least one row was successfully updated; otherwise, <see langword="false"/>.</returns>
        public async Task<bool> UpdateDomainAsync(
            string domainName,
            string? description,
            bool isEnabled,
            bool dkimEnabled,
            string? dkimSelector,
            CancellationToken cancellationToken = default)
        {
            string normalizedName = domainName.Trim().ToLowerInvariant();
            string? normalizedSelector = string.IsNullOrWhiteSpace(dkimSelector)
                ? null
                : dkimSelector.Trim().ToLowerInvariant();

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE Domains
                SET Description = @description,
                    Enabled = @enabled,
                    DkimEnabled = @dkimEnabled,
                    DkimSelector = @dkimSelector
                WHERE DomainName = @name;
            ";
            cmd.Parameters.AddWithValue("@name", normalizedName);
            cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@enabled", isEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@dkimEnabled", dkimEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@dkimSelector", (object?)normalizedSelector ?? DBNull.Value);

            int affectedRows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affectedRows > 0;
        }

        #endregion

        #region method DeleteDomainAsync

        /// <summary>
        /// Asynchronously deletes a domain from the database by its unique name (normalized via Trim and ToLowerInvariant).
        /// </summary>
        /// <param name="domainName">The name of the domain to delete.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><see langword="true"/> if at least one row was successfully deleted; otherwise, <see langword="false"/>.</returns>
        public async Task<bool> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default)
        {
            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = "DELETE FROM Domains WHERE DomainName = @name;";
            cmd.Parameters.AddWithValue("@name", domainName.Trim().ToLowerInvariant());

            int affectedRows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return affectedRows > 0;
        }

        #endregion

        #region method GenerateDomainDkimKeyAsync

        /// <summary>
        /// Asynchronously generates a new 2048-bit RSA DKIM keypair for an existing domain and saves
        /// the selector, private key (PKCS#8 PEM), and public key (base64-encoded SubjectPublicKeyInfo) to SQLite.
        /// </summary>
        /// <param name="domainName">The unique name of the domain to update (normalized via Trim and ToLowerInvariant).</param>
        /// <param name="selector">The DKIM selector to persist alongside the generated keypair.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="true"/> if the domain exists and its DKIM keypair was updated; otherwise, <see langword="false"/>.
        /// </returns>
        public async Task<bool> GenerateDomainDkimKeyAsync(
            string domainName,
            string selector,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(domainName) || string.IsNullOrWhiteSpace(selector)) {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string normalizedName = domainName.Trim().ToLowerInvariant();
            string normalizedSelector = selector.Trim().ToLowerInvariant();

            using var rsa = RSA.Create(2048);
            string privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
            string publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE Domains
                SET DkimSelector = @selector,
                    DkimPrivateKey = @privateKey,
                    DkimPublicKey = @publicKey
                WHERE DomainName = @name;
            ";
            cmd.Parameters.AddWithValue("@name", normalizedName);
            cmd.Parameters.AddWithValue("@selector", normalizedSelector);
            cmd.Parameters.AddWithValue("@privateKey", privateKeyPem);
            cmd.Parameters.AddWithValue("@publicKey", publicKeyBase64);

            int affectedRows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return affectedRows > 0;
        }

        #endregion

        #region method GetUsersAsync

        /// <summary>
        /// Asynchronously retrieves a list of user accounts from the database, supporting optional search filtering
        /// by username or associated email addresses, and aggregates email aliases via a left join.
        /// </summary>
        /// <param name="search">An optional search query string to filter users by username or email address.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of <see cref="UserInfo"/> records representing matching user accounts ordered by username.</returns>
        public async Task<List<UserInfo>> GetUsersAsync(string? search = null, CancellationToken cancellationToken = default)
        {
            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();
            cmd.Parameters.AddWithValue("@today", todayStr);

            var query = new StringBuilder(@"
                SELECT
                    u.UserId,
                    u.UserName,
                    u.Description,
                    u.QuotaMailboxSize,
                    u.QuotaCurrentUsageBytes,
                    u.QuotaMaxDailySends,
                    CASE WHEN u.QuotaLastSendDate = @today THEN u.QuotaSentCountToday ELSE 0 END AS QuotaSentCountToday,
                    u.AllowedSendAs,
                    u.CreateDate,
                    u.LastLoginDate,
                    u.Enabled,
                    GROUP_CONCAT(e.EmailAddress, ', ') AS AllEmails
                FROM Users u
                LEFT JOIN UserEmailAddresses e ON u.UserId = e.UserId
                WHERE 1=1
            ");

            if (!string.IsNullOrWhiteSpace(search))
            {
                query.Append(@" AND (
                    u.UserName LIKE @search
                    OR u.UserId IN (SELECT UserId FROM UserEmailAddresses WHERE EmailAddress LIKE @search)
                )");
                cmd.Parameters.AddWithValue("@search", $"%{search.Trim().ToLowerInvariant()}%");
            }

            query.Append(" GROUP BY u.UserId, u.UserName, u.Enabled, u.QuotaMailboxSize, u.QuotaCurrentUsageBytes");
            query.Append(" ORDER BY u.UserName ASC;");

            cmd.CommandText = query.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var users = new List<UserInfo>();

            // Cache ordinals for safety and performance
            int ordUserId = reader.GetOrdinal("UserId");
            int ordUserName = reader.GetOrdinal("UserName");
            int ordDescription = reader.GetOrdinal("Description");
            int ordQuotaMailboxSize = reader.GetOrdinal("QuotaMailboxSize");
            int ordQuotaCurrentUsageBytes = reader.GetOrdinal("QuotaCurrentUsageBytes");
            int ordQuotaMaxDailySends = reader.GetOrdinal("QuotaMaxDailySends");
            int ordQuotaSentCountToday = reader.GetOrdinal("QuotaSentCountToday");
            int ordAllowedSendAs = reader.GetOrdinal("AllowedSendAs");
            int ordCreateDate = reader.GetOrdinal("CreateDate");
            int ordLastLoginDate = reader.GetOrdinal("LastLoginDate");
            int ordEnabled = reader.GetOrdinal("Enabled");
            int ordAllEmails = reader.GetOrdinal("AllEmails");

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string emailsString = reader.IsDBNull(ordAllEmails) ? "" : reader.GetString(ordAllEmails);
                var emailList = emailsString.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).ToList();

                users.Add(new UserInfo(
                    UserId: reader.GetInt64(ordUserId),
                    UserName: reader.GetString(ordUserName),
                    Description: reader.IsDBNull(ordDescription) ? null : reader.GetString(ordDescription),
                    EmailAddresses: emailList,
                    QuotaMailboxSize: reader.GetInt64(ordQuotaMailboxSize),
                    QuotaCurrentUsageBytes: reader.GetInt64(ordQuotaCurrentUsageBytes),
                    QuotaMaxDailySends: reader.GetInt32(ordQuotaMaxDailySends),
                    QuotaSentCountToday: reader.GetInt32(ordQuotaSentCountToday),
                    AllowedSendAs: reader.IsDBNull(ordAllowedSendAs) ? null : reader.GetString(ordAllowedSendAs),
                    CreateDate: DateTime.Parse(reader.GetString(ordCreateDate), CultureInfo.InvariantCulture),
                    LastLoginDate: reader.IsDBNull(ordLastLoginDate) ? (DateTime?)null : DateTime.Parse(reader.GetString(ordLastLoginDate), CultureInfo.InvariantCulture),
                    IsEnabled: reader.GetInt32(ordEnabled) == 1
                ));
            }

            return users;
        }

        #endregion

        #region method GetUserAsync

        /// <summary>
        /// Asynchronously retrieves an existing user record and their complete quota, status, and audit details by username.
        /// </summary>
        /// <param name="userName">The primary username or account handle to look up in the database.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A <see cref="UserInfo"/> record containing the user's details and associated email addresses if found;
        /// otherwise, <see langword="null"/> if the user does not exist.
        /// </returns>
        public async Task<UserInfo?> GetUserAsync(string userName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userName))
                return null;

            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            // 1. Fetch the complete user details matching the GetUsers schema
            cmd.CommandText = @"
                SELECT
                    UserId,
                    UserName,
                    Description,
                    Enabled,
                    QuotaMailboxSize,
                    QuotaCurrentUsageBytes,
                    QuotaMaxDailySends,
                    CASE WHEN QuotaLastSendDate = @today THEN QuotaSentCountToday ELSE 0 END AS QuotaSentCountToday,
                    AllowedSendAs,
                    CreateDate,
                    LastLoginDate
                FROM Users
                WHERE UserName = @userName;
            ";

            cmd.Parameters.AddWithValue("@today", todayStr);
            cmd.Parameters.AddWithValue("@userName", userName.Trim().ToLowerInvariant());

            long userId;
            string dbUserName;
            string? description;
            bool enabled;
            long quotaMailboxSize;
            long currentUsageBytes;
            int maxDailySends;
            int sentCountToday;
            string? allowedSendAs;
            DateTime createDate;
            DateTime? lastLoginDate;

            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                    return null;
                }

                userId = reader.GetInt64(reader.GetOrdinal("UserId"));
                dbUserName = reader.GetString(reader.GetOrdinal("UserName"));
                description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description"));
                enabled = reader.GetInt32(reader.GetOrdinal("Enabled")) == 1;
                quotaMailboxSize = reader.GetInt64(reader.GetOrdinal("QuotaMailboxSize"));
                currentUsageBytes = reader.GetInt64(reader.GetOrdinal("QuotaCurrentUsageBytes"));
                maxDailySends = reader.GetInt32(reader.GetOrdinal("QuotaMaxDailySends"));
                sentCountToday = reader.GetInt32(reader.GetOrdinal("QuotaSentCountToday"));
                allowedSendAs = reader.IsDBNull(reader.GetOrdinal("AllowedSendAs")) ? null : reader.GetString(reader.GetOrdinal("AllowedSendAs"));
                createDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreateDate")), CultureInfo.InvariantCulture);
                lastLoginDate = reader.IsDBNull(reader.GetOrdinal("LastLoginDate")) ? (DateTime?)null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastLoginDate")), CultureInfo.InvariantCulture);
            }

            // 2. Fetch all associated email addresses from UserEmailAddresses
            var emailAddresses = new List<string>();
            await using (var emailCmd = connection.CreateCommand()) {
                emailCmd.CommandText = @"
                    SELECT EmailAddress
                    FROM UserEmailAddresses
                    WHERE UserId = @userId;
                ";
                emailCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@userId", Microsoft.Data.Sqlite.SqliteType.Integer) { Value = userId });

                await using var emailReader = await emailCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await emailReader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                    emailAddresses.Add(emailReader.GetString(0));
                }
            }

            return new UserInfo(
                UserId: userId,
                UserName: dbUserName,
                Description: description,
                EmailAddresses: emailAddresses,
                QuotaMailboxSize: quotaMailboxSize,
                QuotaCurrentUsageBytes: currentUsageBytes,
                QuotaMaxDailySends: maxDailySends,
                QuotaSentCountToday: sentCountToday,
                AllowedSendAs: allowedSendAs,
                CreateDate: createDate,
                LastLoginDate: lastLoginDate,
                IsEnabled: enabled
            );
        }

        #endregion

        #region method CreateUserAsync
          
        /// <summary>
        /// Asynchronously creates a new user record in the database with secure password hashing and initial quota parameters.
        /// </summary>
        /// <param name="userName">The primary username or account handle.</param>
        /// <param name="rawPassword">The plaintext password to be securely hashed and salted.</param>
        /// <param name="description">Optional notes or description for the user account.</param>
        /// <param name="enabled">Indicates whether the user account is initially enabled.</param>
        /// <param name="quotaMailboxSize">The maximum allowed mailbox size quota in bytes.</param>
        /// <param name="quotaMaxDailySends">The maximum number of outgoing emails permitted per day.</param>
        /// <param name="allowedSendAs">
        /// Optional semicolon-separated list of delegated sender addresses or domain wildcards
        /// (e.g., <c>"*@example.com; billing@partner.com"</c>) that this user is permitted to send as or on behalf of.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// The generated unique 64-bit user ID if creation succeeds;
        /// otherwise, <c>-1</c> if a unique constraint violation (such as a duplicate username) occurs.
        /// </returns>
        public async Task<long> CreateUserAsync(
            string userName,
            string rawPassword,
            string? description,
            bool enabled,
            long quotaMailboxSize,
            int quotaMaxDailySends,
            string? allowedSendAs,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);

            long userId = ((long)RandomNumberGenerator.GetInt32(1, int.MaxValue) << 32)
                | (uint)RandomNumberGenerator.GetInt32(int.MaxValue);

            HashPassword(rawPassword, out string passwordHash, out string passwordSalt);
            string createDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            try {
                using var connection = CreateConnection();
                await using var cmd = connection.CreateCommand();

                cmd.CommandText = @"
                    INSERT INTO Users (
                        UserId, UserName, PasswordHash, PasswordSalt, Enabled,
                        Description, QuotaMailboxSize, QuotaCurrentUsageBytes,
                        QuotaMaxDailySends, QuotaSentCountToday,
                        QuotaLastSendDate, AllowedSendAs, CreateDate
                    )
                    VALUES (
                        @userId, @userName, @passwordHash, @passwordSalt, @enabled,
                        @description, @quotaSize, 0, @maxDailySends, 0,
                        @quotaDate, @allowedSendAs, @createDate
                    );
                ";

                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@userName", userName.Trim().ToLowerInvariant());
                cmd.Parameters.AddWithValue("@passwordHash", passwordHash);
                cmd.Parameters.AddWithValue("@passwordSalt", passwordSalt);
                cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("@quotaSize", quotaMailboxSize);
                cmd.Parameters.AddWithValue("@maxDailySends", quotaMaxDailySends);
                cmd.Parameters.AddWithValue("@quotaDate", today);
                cmd.Parameters.AddWithValue("@allowedSendAs", (object?)allowedSendAs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@createDate", createDate);

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return userId;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) {
                // Unique constraint violation (UserName already exists, or rare ID collision)
                return -1;
            }
        }

        #endregion

        #region method UpdateUserAsync

        /// <summary>
        /// Asynchronously updates an existing user's record in the database, selectively modifying only the fields
        /// that are provided (non-null), while leaving unprovided fields unchanged.
        /// </summary>
        /// <param name="userId">The unique 64-bit identifier of the user to update.</param>
        /// <param name="newRawPassword">The new plaintext password to be hashed, or <see langword="null"/> to keep the existing password.</param>
        /// <param name="description">The updated notes or description, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="enabled">The updated active status, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="quotaMailboxSize">The updated mailbox size quota in bytes, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="quotaMaxDailySends">The updated maximum daily email send limit, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="allowedSendAs">
        /// Optional semicolon-separated list of delegated sender addresses or domain wildcards
        /// (e.g., <c>"*@example.com; billing@partner.com"</c>) that this user is permitted to send as or on behalf of.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="true"/> if the user record was successfully updated (at least one row affected);
        /// otherwise, <see langword="false"/> if the user ID was not found.
        /// </returns>
        public async Task<bool> UpdateUserAsync(
            long userId,
            string? newRawPassword = null,
            string? description = null,
            bool? enabled = null,
            long? quotaMailboxSize = null,
            int? quotaMaxDailySends = null,
            string? allowedSendAs = null,
            CancellationToken cancellationToken = default)
        {
            string? passwordHash = null;
            string? passwordSalt = null;

            if (!string.IsNullOrEmpty(newRawPassword)) {
                HashPassword(newRawPassword, out passwordHash, out passwordSalt);
            }

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE Users
                SET
                    PasswordHash = COALESCE(@passwordHash, PasswordHash),
                    PasswordSalt = COALESCE(@passwordSalt, PasswordSalt),
                    Description = @description,
                    Enabled = COALESCE(@enabled, Enabled),
                    QuotaMailboxSize = COALESCE(@quotaMailboxSize, QuotaMailboxSize),
                    QuotaMaxDailySends = COALESCE(@quotaMaxDailySends, QuotaMaxDailySends),
                    AllowedSendAs = @allowedSendAs
                WHERE UserId = @userId;
            ";

            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@passwordHash", (object?)passwordHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@passwordSalt", (object?)passwordSalt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@enabled", enabled.HasValue ? (enabled.Value ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("@quotaMailboxSize", (object?)quotaMailboxSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@quotaMaxDailySends", (object?)quotaMaxDailySends ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@allowedSendAs", (object?)allowedSendAs ?? DBNull.Value);

            int affectedRows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return affectedRows > 0;
        }

        #endregion

        #region method DeleteUserAsync

        /// <summary>
        /// Asynchronously deletes a user account and all of its associated email addresses from the database
        /// within an explicit transaction to ensure data integrity.
        /// </summary>
        /// <param name="userId">The unique 64-bit identifier of the user to delete.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="null"/> if the user and associated records were deleted;
        /// otherwise, an error message describing why deletion failed.
        /// </returns>
        public async Task<string?> DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
        {
            using var connection = CreateConnection();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)transaction;

                // 1. Explicitly remove any associated email addresses/aliases first
                cmd.CommandText = "DELETE FROM UserEmailAddresses WHERE UserId = @userId;";
                cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@userId", Microsoft.Data.Sqlite.SqliteType.Integer) { Value = userId });
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                // 2. Now delete the user
                cmd.CommandText = "DELETE FROM Users WHERE UserId = @userId;";
                cmd.Parameters.Clear();
                cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@userId", Microsoft.Data.Sqlite.SqliteType.Integer) { Value = userId });

                int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (rowsAffected == 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return $"User with ID {userId} was not found.";
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex) {
                try {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch {
                    // Preserve the original exception message if rollback also fails
                }

                return ex.Message;
            }
        }

        #endregion

        #region method ResolveUserAsync

        /// <summary>
        /// Asynchronously resolves and retrieves a user account and their complete quota, status, and audit details 
        /// by a specific associated email address, verifying that the account is enabled.
        /// </summary>
        /// <param name="emailAddress">The email address to resolve.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
        /// <returns>
        /// A <see cref="UserInfo"/> record containing the user's complete details if found and active; 
        /// otherwise, <c>null</c>.
        /// </returns>
        public async Task<UserInfo?> ResolveUserAsync(string emailAddress, CancellationToken cancellationToken = default)
        {
            string normalizedEmail = emailAddress.Trim().ToLowerInvariant();

            const string sql = @"
                SELECT 
                    u.UserId,
                    u.UserName,
                    u.Description,
                    u.QuotaMailboxSize,
                    u.QuotaCurrentUsageBytes,
                    u.QuotaMaxDailySends,
                    u.QuotaSentCountToday,
                    u.AllowedSendAs,
                    u.CreateDate,
                    u.LastLoginDate,
                    u.enabled,
                    (
                        SELECT json_group_array(a2.EmailAddress)
                        FROM UserEmailAddresses a2
                        WHERE a2.UserId = u.UserId
                    ) AS AllEmailsJson
                FROM UserEmailAddresses a
                JOIN Users u ON a.UserId = u.UserId
                WHERE a.EmailAddress = @email AND u.enabled = 1;
            ";

            using var connection = CreateConnection();

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@email", normalizedEmail);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) {
                int ordUserId = reader.GetOrdinal("UserId");
                int ordUserName = reader.GetOrdinal("UserName");
                int ordDescription = reader.GetOrdinal("Description");
                int ordQuotaMailboxSize = reader.GetOrdinal("QuotaMailboxSize");
                int ordQuotaCurrentUsageBytes = reader.GetOrdinal("QuotaCurrentUsageBytes");
                int ordQuotaMaxDailySends = reader.GetOrdinal("QuotaMaxDailySends");
                int ordQuotaSentCountToday = reader.GetOrdinal("QuotaSentCountToday");
                int ordAllowedSendAs = reader.GetOrdinal("AllowedSendAs");
                int ordCreateDate = reader.GetOrdinal("CreateDate");
                int ordLastLoginDate = reader.GetOrdinal("LastLoginDate");
                int ordEnabled = reader.GetOrdinal("Enabled");
                int ordAllEmailsJson = reader.GetOrdinal("AllEmailsJson");

                long userId = reader.GetInt64(ordUserId);
                string userName = reader.GetString(ordUserName);
                string? description = reader.IsDBNull(ordDescription) ? null : reader.GetString(ordDescription);
                long quotaMailboxSize = reader.GetInt64(ordQuotaMailboxSize);
                long currentUsageBytes = reader.GetInt64(ordQuotaCurrentUsageBytes);
                int maxDailySends = reader.GetInt32(ordQuotaMaxDailySends);
                int sentCountToday = reader.GetInt32(ordQuotaSentCountToday);
                string? allowedSendAs = reader.IsDBNull(ordAllowedSendAs) ? null : reader.GetString(ordAllowedSendAs);
                DateTime createDate = DateTime.Parse(reader.GetString(ordCreateDate));
                DateTime? lastLoginDate = reader.IsDBNull(ordLastLoginDate) ? (DateTime?)null : DateTime.Parse(reader.GetString(ordLastLoginDate));
                bool enabled = reader.GetInt32(ordEnabled) == 1;

                string emailsJson = reader.IsDBNull(ordAllEmailsJson) ? "[]" : reader.GetString(ordAllEmailsJson);
                var emails = JsonSerializer.Deserialize<List<string>>(emailsJson) ?? new List<string>();

                return new UserInfo(
                    UserId: userId,
                    UserName: userName,
                    Description: description,
                    EmailAddresses: emails,
                    QuotaMailboxSize: quotaMailboxSize,
                    QuotaCurrentUsageBytes: currentUsageBytes,
                    QuotaMaxDailySends: maxDailySends,
                    QuotaSentCountToday: sentCountToday,
                    AllowedSendAs: allowedSendAs,
                    CreateDate: createDate,
                    LastLoginDate: lastLoginDate,
                    IsEnabled: enabled
                );
            }

            return null;
        }

        #endregion

        #region method AuthenticateUserAsync

        /// <summary>
        /// Asynchronously authenticates a user against stored hash and salt values and updates <c>LastLoginDate</c>
        /// when credentials are valid and the account is enabled.
        /// </summary>
        /// <param name="userName">The login name attempting authentication.</param>
        /// <param name="rawPassword">The plaintext password provided during authentication.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// The authenticated user's 64-bit <c>UserId</c> if credentials are valid and the account is active;
        /// otherwise, <see langword="null"/>.
        /// </returns>
        public async Task<long?> AuthenticateUserAsync(
            string userName,
            string rawPassword,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(rawPassword)) {
                return null;
            }

            string normalizedUserName = userName.Trim().ToLowerInvariant();
            using var connection = CreateConnection();

            long userId;
            string storedHash;
            string storedSalt;
            int enabled;

            // 1. Query user credentials and status
            await using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = @"
                    SELECT UserId, PasswordHash, PasswordSalt, Enabled
                    FROM Users
                    WHERE UserName = @userName;
                ";
                cmd.Parameters.AddWithValue("@userName", normalizedUserName);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null; // User not found
                }

                userId = reader.GetInt64(0);
                storedHash = reader.GetString(1);
                storedSalt = reader.GetString(2);
                enabled = reader.GetInt32(3);
            }

            if (enabled != 1) {
                return null; // Account disabled
            }

            // 2. Verify password using the shared hashing mechanism
            if (!VerifyPassword(rawPassword, storedHash, storedSalt)) {
                return null;
            }

            // 3. Authentication successful — update LastLoginDate
            string lastLoginIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            await using (var updateCmd = connection.CreateCommand()) {
                updateCmd.CommandText = @"
                    UPDATE Users
                    SET LastLoginDate = @lastLogin
                    WHERE UserId = @userId;
                ";
                updateCmd.Parameters.AddWithValue("@lastLogin", lastLoginIso);
                updateCmd.Parameters.AddWithValue("@userId", userId);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return userId;
        }

        #endregion

        #region method UpdateUserStorageUsageAsync

        /// <summary>
        /// Asynchronously updates the current storage usage quota for a specified user in the database,
        /// ensuring the usage never drops below zero.
        /// </summary>
        /// <param name="userName">The username whose storage quota usage is being modified.</param>
        /// <param name="byteDelta">The number of bytes to add (positive) or subtract (negative) from the current usage.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task UpdateUserStorageUsageAsync(
            string userName,
            long byteDelta,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userName)) {
                return;
            }

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE Users
                SET QuotaCurrentUsageBytes = MAX(0, QuotaCurrentUsageBytes + @delta)
                WHERE UserName = @userName;
            ";

            cmd.Parameters.AddWithValue("@delta", byteDelta);
            cmd.Parameters.AddWithValue("@userName", userName.Trim().ToLowerInvariant());

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        #endregion   

        #region method IncrementDailySendCountAsync

        /// <summary>
        /// Asynchronously increments the user's daily send count, resetting the counter to <c>1</c>
        /// when the last send occurred on a prior UTC date.
        /// </summary>
        /// <param name="userName">The authenticated username of the sender.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        public async Task IncrementDailySendCountAsync(
            string userName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userName)) {
                return;
            }

            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string normalizedUserName = userName.Trim().ToLowerInvariant();

            using var connection = CreateConnection();

            const string updateSql = @"
                UPDATE Users
                SET QuotaSentCountToday = CASE WHEN QuotaLastSendDate = @today THEN QuotaSentCountToday + 1 ELSE 1 END,
                    QuotaLastSendDate = @today
                WHERE UserName = @username;
            ";

            await using var cmd = new SqliteCommand(updateSql, connection);
            cmd.Parameters.AddWithValue("@today", todayStr);
            cmd.Parameters.AddWithValue("@username", normalizedUserName);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region method EmailAddressExistsAsync

        /// <summary>
        /// Asynchronously checks whether a given email address exists in the routing database
        /// and verifies that both the owning user account and the target domain are enabled.
        /// </summary>
        /// <param name="emailAddress">The email address to check (e.g., <c>user@example.com</c>).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="true"/> if the email exists, belongs to an enabled user,
        /// and resides on an enabled domain; otherwise, <see langword="false"/>.
        /// </returns>
        public async Task<bool> EmailAddressExistsAsync(
            string emailAddress,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(emailAddress)) {
                return false;
            }

            string normalizedEmail = emailAddress.Trim().ToLowerInvariant();

            // Extract the domain part
            int atIndex = normalizedEmail.IndexOf('@');
            if (atIndex <= 0 || atIndex == normalizedEmail.Length - 1) {
                return false; // Invalid email format
            }

            string domainName = normalizedEmail[(atIndex + 1)..];

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT 1
                FROM UserEmailAddresses ue
                JOIN Users u ON ue.UserId = u.UserId
                JOIN Domains d ON d.DomainName = @domainName
                WHERE ue.EmailAddress = @emailAddress
                  AND u.Enabled = 1
                  AND d.Enabled = 1
                LIMIT 1;
            ";

            cmd.Parameters.AddWithValue("@emailAddress", normalizedEmail);
            cmd.Parameters.AddWithValue("@domainName", domainName);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region method CreateEmailAddressAsync

        /// <summary>
        /// Asynchronously creates and assigns a new email address routing record for a user,
        /// verifying that the user exists and is enabled, the domain is registered and enabled,
        /// and the email address is unique.
        /// </summary>
        /// <param name="userId">The unique 64-bit ID of the user owning the email address.</param>
        /// <param name="emailAddress">The email address to create (e.g., <c>user@example.com</c>).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="null"/> if the email address was created successfully;
        /// otherwise, an error message describing why creation failed.
        /// </returns>
        public async Task<string?> CreateEmailAddressAsync(
            long userId,
            string emailAddress,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(emailAddress)) {
                return "Email address cannot be null or empty.";
            }

            string normalizedEmail = emailAddress.Trim().ToLowerInvariant();

            // Extract the domain part in C#
            int atIndex = normalizedEmail.IndexOf('@');
            if (atIndex <= 0 || atIndex == normalizedEmail.Length - 1) {
                return $"Invalid email address format: '{emailAddress}'.";
            }

            string domainName = normalizedEmail[(atIndex + 1)..];
            string createDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            using var connection = CreateConnection();

            // 1. Verify user existence
            await using (var userCheckCmd = connection.CreateCommand()) {
                userCheckCmd.CommandText = "SELECT 1 FROM Users WHERE UserId = @userId LIMIT 1;";
                userCheckCmd.Parameters.AddWithValue("@userId", userId);

                if (await userCheckCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) == null) {
                    return $"User with ID {userId} does not exist.";
                }
            }

            // 2. Verify domain existence and active status
            await using (var domainCheckCmd = connection.CreateCommand()) {
                domainCheckCmd.CommandText = "SELECT 1 FROM Domains WHERE DomainName = @domainName AND Enabled = 1 LIMIT 1;";
                domainCheckCmd.Parameters.AddWithValue("@domainName", domainName);

                if (await domainCheckCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) == null) {
                    return $"Domain '{domainName}' is not registered on this server or is disabled.";
                }
            }

            // 3. Attempt insert
            try {
                await using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO UserEmailAddresses (
                        EmailAddress, UserID, CreateDate
                    )
                    VALUES (
                        @emailAddress, @userId, @createDate
                    );
                ";

                insertCmd.Parameters.AddWithValue("@emailAddress", normalizedEmail);
                insertCmd.Parameters.AddWithValue("@userId", userId);
                insertCmd.Parameters.AddWithValue("@createDate", createDate);

                await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (SqliteException) {
                return $"The email address '{normalizedEmail}' is already taken.";
            }
        }

        #endregion

        #region method DeleteEmailAddress
           
        /// <summary>
        /// Asynchronously deletes a specific email address associated with a user account from the database.
        /// </summary>
        /// <param name="userId">The unique numeric identifier of the user owning the email address.</param>
        /// <param name="emailAddress">The email address to be removed.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="null"/> if the email address was successfully deleted;
        /// otherwise, an explanatory message if validation failed or the email address was not found for the user.
        /// </returns>
        public async Task<string?> DeleteEmailAddressAsync(
            long userId,
            string emailAddress,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(emailAddress)) {
                return "Email address cannot be null or empty.";
            }

            string normalizedEmail = emailAddress.Trim().ToLowerInvariant();

            using var connection = CreateConnection();
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                DELETE FROM UserEmailAddresses
                WHERE UserId = @userId AND EmailAddress = @emailAddress;
            ";

            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@emailAddress", normalizedEmail);

            int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (rowsAffected == 0) {
                return $"The email address '{normalizedEmail}' was not found for this user.";
            }

            return null;
        }

        #endregion
                

        #region Private Cryptographic Helpers

        /// <summary>
        /// Generates a cryptographic salt and computes a secure PBKDF2 hash of the raw password.
        /// </summary>
        /// <param name="rawPassword">The plaintext password to hash.</param>
        /// <param name="hashString">Outputs the base64-encoded derived cryptographic key.</param>
        /// <param name="saltString">Outputs the base64-encoded random salt.</param>
        private void HashPassword(string rawPassword, out string hashString, out string saltString)
        {
            byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
                rawPassword,
                saltBytes,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize
            );

            hashString = Convert.ToBase64String(hashBytes);
            saltString = Convert.ToBase64String(saltBytes);
        }

        /// <summary>
        /// Verifies a plaintext password against a stored base64 hash and salt using a constant-time comparison.
        /// </summary>
        /// <param name="rawPassword">The plaintext password attempt.</param>
        /// <param name="storedHash">The expected base64-encoded password hash from the database.</param>
        /// <param name="storedSalt">The base64-encoded salt associated with the user.</param>
        /// <returns>True if the computed hash matches the stored hash; otherwise, false.</returns>
        private bool VerifyPassword(string rawPassword, string storedHash, string storedSalt)
        {
            byte[] saltBytes = Convert.FromBase64String(storedSalt);
            byte[] storedHashBytes = Convert.FromBase64String(storedHash);

            byte[] computedHashBytes = Rfc2898DeriveBytes.Pbkdf2(
                rawPassword,
                saltBytes,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize
            );

            return CryptographicOperations.FixedTimeEquals(computedHashBytes, storedHashBytes);
        }
        #endregion
    }
}
