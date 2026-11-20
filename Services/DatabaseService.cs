using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace lsMailServer.Services 
{
    /// <summary>
    /// Manages SQLite database operations, connection provisioning, and data persistence for the application.
    /// </summary>
    public class DatabaseService
    {
        private readonly string _connectionString;
        private const int SaltSize = 16; // 128-bit
        private const int KeySize = 32;  // 256-bit
        private const int Iterations = 100_000;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseService"/> class, 
        /// configuring the physical directory path where SQLite database files are stored.
        /// </summary>
        /// <param name="dataDirectory">The directory path where database files and local metadata caches are persisted.</param>
        public DatabaseService(string dataDirectory)
        {
            // Path to the per-instance SQLite database
            string dbPath = Path.Combine(dataDirectory, "mailserver.db");

            _connectionString = $"Data Source={dbPath}";
        
            EnsureDatabaseCreated();
        }

        #region method CreateConnection

        internal SqliteConnection CreateConnection()
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

        private void EnsureDatabaseCreated()
        {
            using var connection = CreateConnection();

            string schemaSql = @"

                CREATE TABLE IF NOT EXISTS Domains (
                    DomainName TEXT PRIMARY KEY COLLATE NOCASE,
                    Description TEXT NULL,
                    Enabled INTEGER DEFAULT 1,
                    CreateDate TEXT NOT NULL
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

                
        #region method GetDomains

        /// <summary>
        /// Retrieves mail domains from the database with optional filtering.
        /// </summary>
        /// <param name="isEnabled">Optional enabled status filter.</param>
        /// <param name="search">Optional domain name search string.</param>
        /// <returns>An enumerable collection of matching <see cref="DomainInfo"/> objects.</returns>
        public IEnumerable<DomainInfo> GetDomains(bool? isEnabled = null, string? search = null)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            var sql = new StringBuilder("SELECT DomainName, Description, Enabled FROM Domains WHERE 1=1");
    
            if (isEnabled.HasValue)
            {
                sql.Append(" AND Enabled = @isEnabled");
                cmd.Parameters.AddWithValue("@isEnabled", isEnabled.Value ? 1 : 0);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                sql.Append(" AND DomainName LIKE @search");
                cmd.Parameters.AddWithValue("@search", $"%{search.Trim().ToLowerInvariant()}%");
            }

            sql.Append(" ORDER BY DomainName ASC;");
            cmd.CommandText = sql.ToString();

            using var reader = cmd.ExecuteReader();
            var domains = new List<DomainInfo>();

            while (reader.Read())
            {
                domains.Add(new DomainInfo(
                    Name: reader.GetString(0),
                    Description: reader.IsDBNull(1) ? null : reader.GetString(1),
                    IsEnabled: reader.GetInt32(2) == 1
                ));
            }

            return domains;
        }

        #endregion

        #region method GetDomain

        /// <summary>
        /// Retrieves a single mail domain by its name from the database.
        /// </summary>
        /// <param name="domainName">The normalized domain name.</param>
        /// <returns>A <see cref="DomainInfo"/> if found; otherwise, <c>null</c>.</returns>
        public DomainInfo? GetDomain(string domainName)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT DomainName, Description, Enabled FROM Domains WHERE DomainName = @domainName;";
            cmd.Parameters.AddWithValue("@domainName", domainName);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new DomainInfo(
                    Name: reader.GetString(0),
                    Description: reader.IsDBNull(1) ? null : reader.GetString(1),
                    IsEnabled: reader.GetInt32(2) == 1
                );
            }

            return null;
        }

        #endregion

        #region method DomainExists

        /// <summary>
        /// Checks if a domain exists in the database and is currently enabled.
        /// </summary>
        /// <param name="domainName">The name of the domain to check (normalized via Trim and ToLowerInvariant).</param>
        /// <returns>True if the domain exists and Enabled = 1; otherwise, false.</returns>
        public bool DomainExists(string domainName)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT COUNT(1) 
                FROM Domains 
                WHERE DomainName = @domainName AND Enabled = 1;
            ";
            cmd.Parameters.AddWithValue("@domainName", domainName.Trim().ToLowerInvariant());

            long count = (long)(cmd.ExecuteScalar() ?? 0L);
            return count > 0;
        }

        #endregion

        #region method CreateDomain

        /// <summary>
        /// Creates a new domain record using the normalized domain name as the primary key.
        /// </summary>
        /// <param name="domainName">The target domain name (e.g., example.com). Will be trimmed and normalized to lowercase.</param>
        /// <param name="description">Optional human-readable notes or organizational description for the domain.</param>
        /// <returns>
        /// <see langword="true"/> if the domain was successfully created; 
        /// <see langword="false"/> if the domain name already exists in the database.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="domainName"/> is null or whitespace.</exception>
        public bool CreateDomain(string domainName, string? description = null)
        {
            if(string.IsNullOrWhiteSpace(domainName)) {
                throw new ArgumentNullException(nameof(domainName), "Domain name cannot be null or empty.");
            }

            string normalizedDomain = domainName.Trim().ToLowerInvariant();
            string createDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            try {
                using var connection = CreateConnection();
                using var cmd = connection.CreateCommand();

                cmd.CommandText = @"
                    INSERT INTO Domains (
                        DomainName, Description, CreateDate
                    ) 
                    VALUES (
                        @domainName, @description, @createDate
                    );
                ";

                cmd.Parameters.AddWithValue("@domainName", normalizedDomain);
                cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@createDate", createDate);

                cmd.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException) {
                // Unique constraint violation (DomainName already exists)
                return false;
            }
        }

        #endregion

        #region method UpdateDomain

        /// <summary>
        /// Updates the description and active status of an existing domain identified by its name.
        /// </summary>
        /// <param name="domainName">The unique name of the domain to update (normalized via Trim and ToLowerInvariant).</param>
        /// <param name="description">The updated description or notes, or null to clear it.</param>
        /// <param name="isEnabled">Indicates whether the domain routing is active (true for 1, false for 0).</param>
        /// <returns>True if at least one row was successfully updated; otherwise, false.</returns>
        public bool UpdateDomain(string domainName, string? description, bool isEnabled)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE Domains 
                SET Description = @description, 
                    Enabled = @enabled 
                WHERE DomainName = @name;
            ";
            cmd.Parameters.AddWithValue("@name", domainName.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@enabled", isEnabled ? 1 : 0);

            return cmd.ExecuteNonQuery() > 0;
        }

        #endregion

        #region method DeleteDomain

        /// <summary>
        /// Deletes a domain from the database by its unique name (normalized via Trim and ToLowerInvariant). 
        /// Cascades to delete associated users and mailboxes depending on foreign key constraints.
        /// </summary>
        /// <param name="domainName">The name of the domain to delete.</param>
        /// <returns>True if at least one row was successfully deleted; otherwise, false.</returns>
        public bool DeleteDomain(string domainName)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "DELETE FROM Domains WHERE DomainName = @name;";
            cmd.Parameters.AddWithValue("@name", domainName.Trim().ToLowerInvariant());

            return cmd.ExecuteNonQuery() > 0;
        }

        #endregion

        #region method GetUsers

        /// <summary>
        /// Retrieves a collection of user accounts from the database, supporting optional search filtering 
        /// by username or associated email addresses, and aggregates email aliases via a left join.
        /// </summary>
        /// <param name="search">An optional search query string to filter users by username or email address.</param>
        /// <returns>An enumerable collection of <see cref="UserInfo"/> records representing matching user accounts.</returns>
        public IEnumerable<UserInfo> GetUsers(string? search = null)
        {
            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.Parameters.AddWithValue("@today", todayStr);

            var query = new System.Text.StringBuilder(@"
                SELECT 
                    u.UserId, 
                    u.UserName, 
                    u.Description,
                    u.QuotaMailboxSize, 
                    u.QuotaCurrentUsageBytes,
                    u.QuotaMaxDailySends,
                    CASE WHEN u.QuotaLastSendDate = @today THEN u.QuotaSentCountToday ELSE 0 END AS QuotaSentCountToday,
                    u.CreateDate, 
                    u.LastLoginDate,
                    u.Enabled,
                    GROUP_CONCAT(e.EmailAddress, ', ') AS AllEmails
                FROM Users u
                LEFT JOIN UserEmailAddresses e ON u.UserId = e.UserId
                WHERE 1=1
            ");

            if (!string.IsNullOrWhiteSpace(search)) {
                query.Append(@" AND (
                    u.UserName LIKE @search 
                    OR u.UserId IN (SELECT UserId FROM UserEmailAddresses WHERE EmailAddress LIKE @search)
                )");
                cmd.Parameters.AddWithValue("@search", $"%{search.Trim().ToLowerInvariant()}%");
            }

            query.Append(" GROUP BY u.UserId, u.UserName, u.Enabled, u.QuotaMailboxSize, u.QuotaCurrentUsageBytes");
            query.Append(" ORDER BY u.UserName ASC;");

            cmd.CommandText = query.ToString();

            using var reader = cmd.ExecuteReader();
            var users = new List<UserInfo>();

            // Cache ordinals for safety and performance
            int ordUserId = reader.GetOrdinal("UserId");
            int ordUserName = reader.GetOrdinal("UserName");
            int ordDescription = reader.GetOrdinal("Description");
            int ordQuotaMailboxSize = reader.GetOrdinal("QuotaMailboxSize");
            int ordQuotaCurrentUsageBytes = reader.GetOrdinal("QuotaCurrentUsageBytes");
            int ordQuotaMaxDailySends = reader.GetOrdinal("QuotaMaxDailySends");
            int ordQuotaSentCountToday = reader.GetOrdinal("QuotaSentCountToday");
            int ordCreateDate = reader.GetOrdinal("CreateDate");
            int ordLastLoginDate = reader.GetOrdinal("LastLoginDate");
            int ordEnabled = reader.GetOrdinal("Enabled");
            int ordAllEmails = reader.GetOrdinal("AllEmails");

            while (reader.Read()) {
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
                    CreateDate: DateTime.Parse(reader.GetString(ordCreateDate), CultureInfo.InvariantCulture),
                    LastLoginDate: reader.IsDBNull(ordLastLoginDate) ? (DateTime?)null : DateTime.Parse(reader.GetString(ordLastLoginDate), CultureInfo.InvariantCulture),
                    IsEnabled: reader.GetInt32(ordEnabled) == 1
                ));
            }

            return users;
        }

        #endregion

        #region method GetUser

        /// <summary>
        /// Retrieves an existing user record and their complete quota, status, and audit details by username.
        /// </summary>
        /// <param name="userName">The primary username or account handle to look up in the database.</param>
        /// <returns>
        /// A <see cref="UserInfo"/> record containing the user's details and associated email addresses if found; 
        /// otherwise, <c>null</c> if the user does not exist.
        /// </returns>
        public UserInfo? GetUser(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                return null;

            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            using var connection = CreateConnection();
            
            using var cmd = connection.CreateCommand();

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
            DateTime createDate;
            DateTime? lastLoginDate;

            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                {
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
                createDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreateDate")), CultureInfo.InvariantCulture);
                lastLoginDate = reader.IsDBNull(reader.GetOrdinal("LastLoginDate")) ? (DateTime?)null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastLoginDate")), CultureInfo.InvariantCulture);
            }

            // 2. Fetch all associated email addresses from UserEmailAddresses
            var emailAddresses = new List<string>();
            using (var emailCmd = connection.CreateCommand())
            {
                emailCmd.CommandText = @"
                    SELECT EmailAddress 
                    FROM UserEmailAddresses 
                    WHERE UserId = @userId;
                ";
                emailCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@userId", Microsoft.Data.Sqlite.SqliteType.Integer) { Value = userId });

                using var emailReader = emailCmd.ExecuteReader();
                while (emailReader.Read())
                {
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
                CreateDate: createDate,
                LastLoginDate: lastLoginDate,
                IsEnabled: enabled
            );
        }

        #endregion

        #region method CreateUser
          
        /// <summary>
        /// Creates a new user record in the database with secure password hashing and initial quota parameters.
        /// </summary>
        /// <param name="userName">The primary username or account handle.</param>
        /// <param name="rawPassword">The plaintext password to be securely hashed and salted.</param>
        /// <param name="description">Optional notes or description for the user account.</param>
        /// <param name="quotaMailboxSize">The maximum allowed mailbox size quota in bytes.</param>
        /// <param name="quotaMaxDailySends">The maximum number of outgoing emails permitted per day.</param>
        /// <returns>
        /// The generated unique 64-bit user ID if creation succeeds; 
        /// otherwise, <c>-1</c> if a unique constraint violation (such as a duplicate username) occurs.
        /// </returns>
        public long CreateUser(
            string userName, 
            string rawPassword, 
            string? description, 
            long quotaMailboxSize, 
            int quotaMaxDailySends)
        {
            long userId = ((long)RandomNumberGenerator.GetInt32(1, int.MaxValue) << 32) 
                | (uint)RandomNumberGenerator.GetInt32(int.MaxValue);

            HashPassword(rawPassword, out string passwordHash, out string passwordSalt);
            string createDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            try
            {
                using var connection = CreateConnection();
                using var cmd = connection.CreateCommand();

                cmd.CommandText = @"
                    INSERT INTO Users (
                        UserId, UserName, PasswordHash, PasswordSalt, Enabled, 
                        Description, QuotaMailboxSize, QuotaCurrentUsageBytes, 
                        QuotaMaxDailySends, QuotaSentCountToday, 
                        QuotaLastSendDate, CreateDate
                    ) 
                    VALUES (
                        @userId, @userName, @passwordHash, @passwordSalt, 1, 
                        @description, @quotaSize, 0, 
                        @maxDailySends, 0, 
                        @quotaDate, @createDate
                    );
                ";

                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@userName", userName.Trim().ToLowerInvariant());
                cmd.Parameters.AddWithValue("@passwordHash", passwordHash);
                cmd.Parameters.AddWithValue("@passwordSalt", passwordSalt);
                cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@quotaSize", quotaMailboxSize);
                cmd.Parameters.AddWithValue("@maxDailySends", quotaMaxDailySends);
                cmd.Parameters.AddWithValue("@quotaDate", today);
                cmd.Parameters.AddWithValue("@createDate", createDate);

                cmd.ExecuteNonQuery();
                return userId;
            }
            catch (SqliteException)
            {
                // Unique constraint violation (UserName already exists, or rare ID collision)
                return -1;
            }
        }

        #endregion

        #region method UpdateUser

        /// <summary>
        /// Updates an existing user's record in the database, selectively modifying only the fields 
        /// that are provided (non-null), while leaving unprovided fields unchanged.
        /// </summary>
        /// <param name="userId">The unique 64-bit identifier of the user to update.</param>
        /// <param name="newRawPassword">The new plaintext password to be hashed, or null to keep the existing password.</param>
        /// <param name="description">The updated notes or description, or null to leave unchanged.</param>
        /// <param name="enabled">The updated active status, or null to leave unchanged.</param>
        /// <param name="quotaMailboxSize">The updated mailbox size quota in bytes, or null to leave unchanged.</param>
        /// <param name="quotaMaxDailySends">The updated maximum daily email send limit, or null to leave unchanged.</param>
        /// <returns>
        /// <c>true</c> if the user record was successfully updated (at least one row affected); 
        /// otherwise, <c>false</c> if the user ID was not found.
        /// </returns>
        public bool UpdateUser(
            long userId,
            string? newRawPassword = null,
            string? description = null,
            bool? enabled = null,
            long? quotaMailboxSize = null,
            int? quotaMaxDailySends = null)
        {
            string? passwordHash = null;
            string? passwordSalt = null;

            if (!string.IsNullOrEmpty(newRawPassword))
            {
                HashPassword(newRawPassword, out passwordHash, out passwordSalt);
            }

            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE Users 
                SET 
                    PasswordHash = COALESCE(@passwordHash, PasswordHash),
                    PasswordSalt = COALESCE(@passwordSalt, PasswordSalt),
                    Description = COALESCE(@description, Description),
                    Enabled = COALESCE(@enabled, Enabled),
                    QuotaMailboxSize = COALESCE(@quotaMailboxSize, QuotaMailboxSize),
                    QuotaMaxDailySends = COALESCE(@quotaMaxDailySends, QuotaMaxDailySends)
                WHERE UserId = @userId;
            ";

            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@passwordHash", (object?)passwordHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@passwordSalt", (object?)passwordSalt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@enabled", enabled.HasValue ? (enabled.Value ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("@quotaMailboxSize", (object?)quotaMailboxSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@quotaMaxDailySends", (object?)quotaMaxDailySends ?? DBNull.Value);

            int affectedRows = cmd.ExecuteNonQuery();
            return affectedRows > 0;
        }

        #endregion

        #region method DeleteUser

        /// <summary>
        /// Deletes a user account and all of its associated email addresses from the database 
        /// within an explicit transaction to ensure data integrity.
        /// </summary>
        /// <param name="userId">The unique 64-bit identifier of the user to delete.</param>
        /// <param name="errorMessage">
        /// When this method returns <c>false</c>, contains the error message or reason for failure; 
        /// otherwise, <see cref="string.Empty"/>.
        /// </param>
        /// <returns>
        /// <c>true</c> if the user and their associated records were successfully deleted; 
        /// otherwise, <c>false</c> if the user was not found or an exception occurred.
        /// </returns>
        public bool DeleteUser(long userId, out string errorMessage)
        {
            errorMessage = string.Empty;

            using var connection = CreateConnection();
            
            using var transaction = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;

                // 1. Explicitly remove any associated email addresses/aliases first
                cmd.CommandText = "DELETE FROM UserEmailAddresses WHERE UserId = @userId;";
                cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@userId", Microsoft.Data.Sqlite.SqliteType.Integer) { Value = userId });
                cmd.ExecuteNonQuery();

                // 2. Now delete the user
                cmd.CommandText = "DELETE FROM Users WHERE UserId = @userId;";
                cmd.Parameters.Clear();
                cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@userId", Microsoft.Data.Sqlite.SqliteType.Integer) { Value = userId });
        
                int rowsAffected = cmd.ExecuteNonQuery();

                if (rowsAffected == 0)
                {
                    transaction.Rollback();
                    errorMessage = $"User with ID {userId} was not found.";
                    return false;
                }

                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                errorMessage = ex.Message;
                return false;
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
            if (await reader.ReadAsync(cancellationToken))
            {
                int ordUserId = reader.GetOrdinal("UserId");
                int ordUserName = reader.GetOrdinal("UserName");
                int ordDescription = reader.GetOrdinal("Description");
                int ordQuotaMailboxSize = reader.GetOrdinal("QuotaMailboxSize");
                int ordQuotaCurrentUsageBytes = reader.GetOrdinal("QuotaCurrentUsageBytes");
                int ordQuotaMaxDailySends = reader.GetOrdinal("QuotaMaxDailySends");
                int ordQuotaSentCountToday = reader.GetOrdinal("QuotaSentCountToday");
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
                    CreateDate: createDate,
                    LastLoginDate: lastLoginDate,
                    IsEnabled: enabled
                );
            }

            return null;
        }

        #endregion

        #region method AuthenticateUser

        /// <summary>
        /// Authenticates a user against stored hash and salt values.
        /// Returns true if credentials are valid and the account is enabled.
        /// </summary>
        /// <param name="userName">The login name or email address attempting authentication.</param>
        /// <param name="rawPassword">The plaintext password provided during authentication.</param>
        /// <param name="userId">Outputs the user's database ID if authentication succeeds; otherwise, -1.</param>
        /// <returns>True if the credentials are valid and the account is active; otherwise, false.</returns>
        public bool AuthenticateUser(string userName, string rawPassword, out long userId)
        {
            userId = -1;

            using var connection = CreateConnection();
    
            string storedHash;
            string storedSalt;
            int enabled;

            // 1. Query user credentials and status
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT UserId, PasswordHash, PasswordSalt, Enabled 
                    FROM Users 
                    WHERE UserName = @userName;
                ";
                cmd.Parameters.AddWithValue("@userName", userName.Trim().ToLowerInvariant());

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) {
                    return false; // User not found
                }

                userId = reader.GetInt64(0);
                storedHash = reader.GetString(1);
                storedSalt = reader.GetString(2);
                enabled = reader.GetInt32(3);
            } // Reader is disposed here, freeing the connection for the next command

            if (enabled != 1) {
                userId = -1;
                return false; // Account disabled
            }

            // 2. Verify password using the shared hashing mechanism
            if (!VerifyPassword(rawPassword, storedHash, storedSalt)) {
                userId = -1;
                return false;
            }

            // 3. Authentication successful — update the LastLoginDate timestamp
            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.CommandText = @"
                    UPDATE Users 
                    SET LastLoginDate = @lastLogin 
                    WHERE UserId = @userId;
                ";
                updateCmd.Parameters.AddWithValue("@lastLogin", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                updateCmd.Parameters.AddWithValue("@userId", userId);
                updateCmd.ExecuteNonQuery();
            }

            return true;
        }

        #endregion

        #region method UpdateUserStorageUsage

        /// <summary>
        /// Updates the current storage usage quota for a specified user in the database, 
        /// ensuring the usage never drops below zero.
        /// </summary>
        /// <param name="userName">The username whose storage quota usage is being modified.</param>
        /// <param name="byteDelta">The number of bytes to add (positive) or subtract (negative) from the current usage.</param>
        public void UpdateUserStorageUsage(string userName, long byteDelta)
        {
            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE Users 
                SET QuotaCurrentUsageBytes = MAX(0, QuotaCurrentUsageBytes + @delta)
                WHERE UserName = @userName;
            ";

            cmd.Parameters.AddWithValue("@delta", byteDelta);
            cmd.Parameters.AddWithValue("@userName", userName.Trim().ToLowerInvariant());

            cmd.ExecuteNonQuery();
        }

        #endregion   

        #region method IncrementDailySendCount

        /// <summary>
        /// Increments the user's daily send count.
        /// </summary>
        /// <param name="userName">The authenticated username of the sender.</param>
        public void IncrementDailySendCount(string userName)
        {
            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");

            using var connection = CreateConnection();
            
            const string updateSql = @"
                UPDATE Users 
                SET QuotaSentCountToday = CASE WHEN QuotaLastSendDate = @today THEN QuotaSentCountToday + 1 ELSE 1 END,
                    QuotaLastSendDate = @today
                WHERE Username = @username;
            ";

            using var cmd = new SqliteCommand(updateSql, connection);
            cmd.Parameters.AddWithValue("@today", todayStr);
            cmd.Parameters.AddWithValue("@username", userName);
            cmd.ExecuteNonQuery();
        }

        #endregion

        #region method EmailAddressExists

        /// <summary>
        /// Checks whether a given email address exists in the routing database 
        /// and verifies that both the owning user account and the target domain are enabled.
        /// </summary>
        /// <param name="emailAddress">The email address to check (e.g., user@example.com).</param>
        /// <returns>
        /// <see langword="true"/> if the email exists, belongs to an enabled user, 
        /// and resides on an enabled domain; otherwise, <see langword="false"/>.
        /// </returns>
        public bool EmailAddressExists(string emailAddress)
        {
            if (string.IsNullOrWhiteSpace(emailAddress))
            {
                return false;
            }

            string normalizedEmail = emailAddress.Trim().ToLowerInvariant();
            
            // Extract the domain part in C#
            int atIndex = normalizedEmail.IndexOf('@');
            if (atIndex <= 0 || atIndex == normalizedEmail.Length - 1)
            {
                return false; // Invalid email format
            }

            string domainName = normalizedEmail[(atIndex + 1)..];

            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

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

            using var reader = cmd.ExecuteReader();
            return reader.HasRows;
        }

        #endregion

        #region method CreateEmailAddress

        /// <summary>
        /// Creates and assigns a new email address routing record for a user, 
        /// verifying that the user exists, the domain is registered and enabled, and the email is unique.
        /// </summary>
        /// <param name="userId">The unique 64-bit ID of the user owning the email address.</param>
        /// <param name="emailAddress">The email address to create (e.g., user@example.com).</param>
        /// <param name="errorMessage">When the method returns false, contains the reason for failure.</param>
        /// <returns><see langword="true"/> if successfully created; otherwise, <see langword="false"/>.</returns>
        public bool CreateEmailAddress(long userId, string emailAddress, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(emailAddress))
            {
                errorMessage = "Email address cannot be null or empty.";
                return false;
            }

            string normalizedEmail = emailAddress.Trim().ToLowerInvariant();

            // Extract the domain part in C#
            int atIndex = normalizedEmail.IndexOf('@');
            if (atIndex <= 0 || atIndex == normalizedEmail.Length - 1)
            {
                errorMessage = $"Invalid email address format: '{emailAddress}'.";
                return false;
            }

            string domainName = normalizedEmail[(atIndex + 1)..];
            string createDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            using var connection = CreateConnection();

            // 1. Verify user existence and active status
            using var userCheckCmd = connection.CreateCommand();
            userCheckCmd.CommandText = "SELECT 1 FROM Users WHERE UserId = @userId AND Enabled = 1 LIMIT 1;";
            userCheckCmd.Parameters.AddWithValue("@userId", userId);
            
            if (userCheckCmd.ExecuteScalar() == null)
            {
                errorMessage = $"User with ID {userId} does not exist or is disabled.";
                return false;
            }

            // 2. Verify domain existence and active status
            using var domainCheckCmd = connection.CreateCommand();
            domainCheckCmd.CommandText = "SELECT 1 FROM Domains WHERE DomainName = @domainName AND Enabled = 1 LIMIT 1;";
            domainCheckCmd.Parameters.AddWithValue("@domainName", domainName);

            if (domainCheckCmd.ExecuteScalar() == null)
            {
                errorMessage = $"Domain '{domainName}' is not registered on this server or is disabled.";
                return false;
            }

            // 3. Attempt insert
            try
            {
                using var insertCmd = connection.CreateCommand();
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

                insertCmd.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException)
            {
                errorMessage = $"The email address '{normalizedEmail}' is already taken.";
                return false;
            }
        }

        #endregion

        #region method DeleteEmailAddress
           
        /// <summary>
        /// Deletes a specific email address associated with a user account from the database.
        /// </summary>
        /// <param name="userId">The unique numeric identifier of the user owning the email address.</param>
        /// <param name="emailAddress">The email address to be removed.</param>
        /// <param name="errorMessage">When this method returns, contains an explanatory message if the operation fails or the email is not found; otherwise, <c>string.Empty</c>.</param>
        /// <returns><c>true</c> if the email address was successfully deleted; otherwise, <c>false</c>.</returns>
        public bool DeleteEmailAddress(long userId, string emailAddress, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(emailAddress))
            {
                errorMessage = "Email address cannot be null or empty.";
                return false;
            }

            string normalizedEmail = emailAddress.Trim().ToLowerInvariant();

            using var connection = CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                DELETE FROM UserEmailAddresses 
                WHERE UserId = @userId AND EmailAddress = @emailAddress;
            ";

            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@emailAddress", normalizedEmail);

            int rowsAffected = cmd.ExecuteNonQuery();

            if (rowsAffected == 0) {
                errorMessage = $"The email address '{normalizedEmail}' was not found for this user.";
                return false;
            }

            return true;
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
