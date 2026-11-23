namespace lsMailServer.Services 
{
    /// <summary>
    /// Defines the persistence contract for domains, users, authentication, quotas, and email address routing.
    /// </summary>
    public interface IDatabaseService
    {
        // --- Domains ---

        /// <summary>
        /// Asynchronously retrieves mail domains from the database with optional filtering.
        /// </summary>
        /// <param name="isEnabled">Optional enabled status filter.</param>
        /// <param name="search">Optional domain name search string.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of matching <see cref="DomainInfo"/> objects ordered by domain name.</returns>
        Task<List<DomainInfo>> GetDomainsAsync(
            bool? isEnabled = null,
            string? search = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously retrieves a single mail domain by its name from the database.
        /// </summary>
        /// <param name="domainName">The normalized domain name.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="DomainInfo"/> if found; otherwise, <see langword="null"/>.</returns>
        Task<DomainInfo?> GetDomainAsync(
            string domainName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously checks if a domain exists in the database and is currently enabled.
        /// </summary>
        /// <param name="domainName">The name of the domain to check.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><see langword="true"/> if the domain exists and is enabled; otherwise, <see langword="false"/>.</returns>
        Task<bool> DomainExistsAsync(
            string domainName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously creates a new domain record using the normalized domain name as the primary key.
        /// </summary>
        /// <param name="domainName">The target domain name (e.g., <c>example.com</c>).</param>
        /// <param name="description">Optional human-readable notes or organizational description for the domain.</param>
        /// <param name="isEnabled">Indicates whether the domain routing is active (<see langword="true"/> for 1, <see langword="false"/> for 0).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="true"/> if the domain was successfully created;
        /// <see langword="false"/> if the domain name already exists in the database.
        /// </returns>
        Task<bool> CreateDomainAsync(
            string domainName,
            string? description, 
            bool isEnabled,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously updates the description and active status of an existing domain identified by its name.
        /// </summary>
        /// <param name="domainName">The unique name of the domain to update.</param>
        /// <param name="description">The updated description or notes, or <see langword="null"/> to clear it.</param>
        /// <param name="isEnabled">Indicates whether the domain routing is active.</param>
        /// <param name="dkimEnabled">Indicates whether DKIM signing is enabled for outbound messages on this domain.</param>
        /// <param name="dkimSelector">Optional DKIM selector, or <see langword="null"/>/whitespace to clear it.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><see langword="true"/> if at least one row was successfully updated; otherwise, <see langword="false"/>.</returns>
        Task<bool> UpdateDomainAsync(
            string domainName,
            string? description,
            bool isEnabled,
            bool dkimEnabled,
            string? dkimSelector,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously deletes a domain from the database by its unique name.
        /// </summary>
        /// <param name="domainName">The name of the domain to delete.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><see langword="true"/> if at least one row was successfully deleted; otherwise, <see langword="false"/>.</returns>
        Task<bool> DeleteDomainAsync(
            string domainName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Generates a new 2048-bit RSA DKIM keypair for an existing domain and persists it with the specified selector.
        /// </summary>
        /// <param name="domainName">The unique name of the domain (normalized via Trim and ToLowerInvariant).</param>
        /// <param name="selector">The DKIM selector to store and associate with the generated keypair.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><see langword="true"/> if the keypair was generated and saved for the domain; otherwise, <see langword="false"/>.</returns>
        Task<bool> GenerateDomainDkimKeyAsync(
            string domainName,
            string selector,
            CancellationToken cancellationToken = default);

        // --- Users ---

        /// <summary>
        /// Asynchronously retrieves a list of user accounts from the database with optional search filtering.
        /// </summary>
        /// <param name="search">An optional search query string to filter users by username or email address.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of <see cref="UserInfo"/> records representing matching user accounts ordered by username.</returns>
        Task<List<UserInfo>> GetUsersAsync(
            string? search = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously retrieves an existing user record and their complete quota, status, and audit details by username.
        /// </summary>
        /// <param name="userName">The primary username or account handle to look up in the database.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A <see cref="UserInfo"/> record containing the user's details and associated email addresses if found;
        /// otherwise, <see langword="null"/> if the user does not exist.
        /// </returns>
        Task<UserInfo?> GetUserAsync(
            string userName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously creates a new user record in the database with secure password hashing and initial quota parameters.
        /// </summary>
        /// <param name="userName">The primary username or account handle.</param>
        /// <param name="rawPassword">The plaintext password to be securely hashed and salted.</param>
        /// <param name="description">Optional notes or description for the user account.</param>
        /// <param name="enabled">Indicates whether the user account is initially enabled.</param>
        /// <param name="quotaMailboxSize">The maximum allowed mailbox size quota in bytes.</param>
        /// <param name="quotaMaxDailySends">The maximum number of outgoing emails permitted per day.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// The generated unique 64-bit user ID if creation succeeds;
        /// otherwise, <c>-1</c> if a unique constraint violation occurs.
        /// </returns>
        Task<long> CreateUserAsync(
            string userName,
            string rawPassword,
            string? description,
            bool enabled,
            long quotaMailboxSize,
            int quotaMaxDailySends,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously updates an existing user's record in the database, selectively modifying only provided fields.
        /// </summary>
        /// <param name="userId">The unique 64-bit identifier of the user to update.</param>
        /// <param name="newRawPassword">The new plaintext password to be hashed, or <see langword="null"/> to keep the existing password.</param>
        /// <param name="description">The updated notes or description, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="enabled">The updated active status, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="quotaMailboxSize">The updated mailbox size quota in bytes, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="quotaMaxDailySends">The updated maximum daily email send limit, or <see langword="null"/> to leave unchanged.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="true"/> if the user record was successfully updated;
        /// otherwise, <see langword="false"/> if the user ID was not found.
        /// </returns>
        Task<bool> UpdateUserAsync(
            long userId,
            string? newRawPassword = null,
            string? description = null,
            bool? enabled = null,
            long? quotaMailboxSize = null,
            int? quotaMaxDailySends = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously deletes a user account and all of its associated email addresses from the database.
        /// </summary>
        /// <param name="userId">The unique 64-bit identifier of the user to delete.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// <see langword="null"/> if the user and associated records were deleted;
        /// otherwise, an error message describing why deletion failed.
        /// </returns>
        Task<string?> DeleteUserAsync(
            long userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously resolves and retrieves a user account and their complete quota, status, and audit details
        /// by a specific associated email address, verifying that the account is enabled.
        /// </summary>
        /// <param name="emailAddress">The email address to resolve.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A <see cref="UserInfo"/> record containing the user's complete details if found and active;
        /// otherwise, <see langword="null"/>.
        /// </returns>
        Task<UserInfo?> ResolveUserAsync(
            string emailAddress,
            CancellationToken cancellationToken = default);

        // --- Authentication & Quotas ---

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
        Task<long?> AuthenticateUserAsync(
            string userName,
            string rawPassword,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously updates the current storage usage quota for a specified user in the database,
        /// ensuring the usage never drops below zero.
        /// </summary>
        /// <param name="userName">The username whose storage quota usage is being modified.</param>
        /// <param name="byteDelta">The number of bytes to add (positive) or subtract (negative) from the current usage.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        Task UpdateUserStorageUsageAsync(
            string userName,
            long byteDelta,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously increments the user's daily send count, resetting the counter to <c>1</c>
        /// when the last send occurred on a prior UTC date.
        /// </summary>
        /// <param name="userName">The authenticated username of the sender.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        Task IncrementDailySendCountAsync(
            string userName,
            CancellationToken cancellationToken = default);

        // --- Email Address Routing ---

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
        Task<bool> EmailAddressExistsAsync(
            string emailAddress,
            CancellationToken cancellationToken = default);

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
        Task<string?> CreateEmailAddressAsync(
            long userId,
            string emailAddress,
            CancellationToken cancellationToken = default);

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
        Task<string?> DeleteEmailAddressAsync(
            long userId,
            string emailAddress,
            CancellationToken cancellationToken = default);
    }
}
