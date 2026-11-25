namespace lsMailServer.Services 
{
    /// <summary>
    /// Represents User entry.
    /// </summary>
    /// <param name="UserId">The unique numeric identifier for the user.</param>
    /// <param name="UserName">The unique username associated with the account.</param>
    /// <param name="Description">An optional description, display name, or administrative note for the user.</param>
    /// <param name="EmailAddresses">A read-only list of email addresses tied to this user account.</param>
    /// <param name="QuotaMailboxSize">The maximum allowed mailbox storage size in bytes.</param>
    /// <param name="QuotaCurrentUsageBytes">The current storage space consumed by the user in bytes.</param>
    /// <param name="QuotaMaxDailySends">The maximum number of outgoing emails allowed per day.</param>
    /// <param name="QuotaSentCountToday">The number of emails sent by the user during the current day.</param>
    /// <param name="AllowedSendAs">
    /// Optional semicolon-separated list of delegated sender addresses or domain wildcards
    /// (e.g., <c>"*@example.com; billing@partner.com"</c>) that this user is permitted to send as or on behalf of.
    /// </param>
    /// <param name="CreateDate">The date and time when the user account was created.</param>
    /// <param name="LastLoginDate">The date and time of the user's most recent login, or <c>null</c> if they have never logged in.</param>
    /// <param name="IsEnabled">A value indicating whether the user account is active and permitted to connect.</param>
    public readonly record struct UserInfo(
        long UserId,
        string UserName,
        string? Description,
        IReadOnlyList<string> EmailAddresses,
        long QuotaMailboxSize,
        long QuotaCurrentUsageBytes,        
        int QuotaMaxDailySends,
        int QuotaSentCountToday,
        string? AllowedSendAs,
        DateTime CreateDate,
        DateTime? LastLoginDate,
        bool IsEnabled
    );
}
