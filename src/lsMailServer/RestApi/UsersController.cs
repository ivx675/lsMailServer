using lsMailServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace lsMailServer.RestApi 
{
    /// <summary>
    /// Manages user accounts, profile operations, and user data endpoints.
    /// </summary>
    [ApiController]
    [Route("api/users")]
    [Authorize]
    public class UsersController : ControllerBase
    {
        #region Dto and Request types

        /// <summary>
        /// Represents a mail server user account, including identification, email aliases, storage quotas, and activity metrics.
        /// </summary>
        /// <param name="UserId">The unique identifier for the user.</param>
        /// <param name="UserName">The primary username or account handle.</param>
        /// <param name="Description">Optional notes or description for the user account.</param>
        /// <param name="EmailAddresses">The list of associated email addresses and aliases assigned to the user.</param>
        /// <param name="StorageQuotaBytes">The maximum allowed storage quota in bytes (converted from MB in the UI).</param>
        /// <param name="StorageUsageBytes">The current storage consumption of the mailbox in bytes.</param>
        /// <param name="MaxDailySends">The maximum number of emails the user is allowed to send per day.</param>
        /// <param name="SentCountToday">The number of emails the user has already sent today.</param>
        /// <param name="CreateDate">The UTC timestamp when the user account was created, or null if unrecorded.</param>
        /// <param name="LastLoginDate">The UTC timestamp of the user's last successful login, or null if never logged in.</param>
        /// <param name="IsEnabled">Indicates whether the user account and authentication are active.</param>
        public readonly record struct UserDto(
            string UserId,
            string UserName,
            string? Description,
            IReadOnlyList<string> EmailAddresses,
            long StorageQuotaBytes,
            long StorageUsageBytes,
            int MaxDailySends,
            int SentCountToday,
            DateTime CreateDate,
            DateTime? LastLoginDate,
            bool IsEnabled
        );

        /// <summary>
        /// Represents the payload required to create a new mail server user account.
        /// </summary>
        /// <param name="UserName">The primary username or account handle for login.</param>
        /// <param name="Password">The plaintext password to be securely hashed and salted upon creation.</param>
        /// <param name="Description">Optional notes or description for the user account.</param>
        /// <param name="StorageQuotaBytes">The maximum allowed storage quota for the mailbox in bytes.</param>
        /// <param name="MaxDailySends">The maximum number of outgoing emails the user is permitted to send per day.</param>
        /// <param name="IsEnabled">Indicates whether the user account is active immediately upon creation.</param>
        public record CreateUserRequest(
            string UserName, 
            string Password, 
            string? Description, 
            long StorageQuotaBytes, 
            int MaxDailySends,
            bool IsEnabled
        );

        /// <summary>
        /// Represents the payload for updating an existing mail server user account, 
        /// supporting partial updates where null properties indicate no change.
        /// </summary>
        /// <param name="Password">The new plaintext password to be hashed, or null to keep the existing password.</param>
        /// <param name="Description">The updated notes or description, or null to leave unchanged.</param>
        /// <param name="StorageQuotaBytes">The updated maximum allowed storage quota in bytes, or null to leave unchanged.</param>
        /// <param name="MaxDailySends">The updated maximum number of daily outgoing emails, or null to leave unchanged.</param>
        /// <param name="IsEnabled">The updated active status, or null to leave unchanged.</param>
        public record UpdateUserRequest(
            string? Password, 
            string? Description, 
            long? StorageQuotaBytes, 
            int? MaxDailySends,
            bool? IsEnabled
        );

        /// <summary>
        /// Represents the payload for creating and associating a new email address or alias with a user account.
        /// </summary>
        /// <param name="EmailAddress">The email address to be created and assigned.</param>
        public record CreateEmailAddressRequest(string EmailAddress);

        #endregion

        private readonly IDatabaseService _dbService;

        /// <summary>
        /// Initializes a new instance of the <see cref="UsersController"/> class.
        /// </summary>
        /// <param name="dbService">The database service used for user data persistence and queries.</param>
        public UsersController(IDatabaseService dbService)
        {
            _dbService = dbService;
        }

        
        /// <summary>
        /// Retrieves a list of all mail server user accounts, with optional filtering by search term.
        /// </summary>
        /// <param name="search">Optional search query to filter users by username or email address.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>An HTTP 200 OK response containing a collection of user data transfer objects.</returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers(
            [FromQuery] string? search = null,
            CancellationToken cancellationToken = default)
        {
            var users = await _dbService.GetUsersAsync(search, cancellationToken);

            var retVal = users
                .Select(user => new UserDto(
                    UserId: user.UserId.ToString(),
                    UserName: user.UserName,
                    Description: user.Description,
                    EmailAddresses: user.EmailAddresses,
                    StorageQuotaBytes: user.QuotaMailboxSize,
                    StorageUsageBytes: user.QuotaCurrentUsageBytes,
                    MaxDailySends: user.QuotaMaxDailySends,
                    SentCountToday: user.QuotaSentCountToday,
                    CreateDate: user.CreateDate,
                    LastLoginDate: user.LastLoginDate,
                    IsEnabled: user.IsEnabled
                ))
                .ToList();

            return Ok(retVal);
        }

        /// <summary>
        /// Retrieves a specific user account by its username.
        /// </summary>
        /// <param name="userName">The primary username or account handle to look up.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An HTTP 200 OK response containing the <see cref="UserDto"/> details if found;
        /// otherwise, an HTTP 404 Not Found response if the user does not exist.
        /// </returns>
        [HttpGet("{userName}")]
        public async Task<ActionResult<UserDto>> GetUser(string userName, CancellationToken cancellationToken = default)
        {
            var user = await _dbService.GetUserAsync(userName, cancellationToken);
            if (user == null) {
                return NotFound(new { error = "User not found." });
            }

            var retVal = new UserDto(
                UserId: user.Value.UserId.ToString(),
                UserName: user.Value.UserName,
                Description: user.Value.Description,
                EmailAddresses: user.Value.EmailAddresses,
                StorageQuotaBytes: user.Value.QuotaMailboxSize,
                StorageUsageBytes: user.Value.QuotaCurrentUsageBytes,
                MaxDailySends: user.Value.QuotaMaxDailySends,
                SentCountToday: user.Value.QuotaSentCountToday,
                CreateDate: user.Value.CreateDate,
                LastLoginDate: user.Value.LastLoginDate,
                IsEnabled: user.Value.IsEnabled
            );

            return Ok(retVal);
        }

        /// <summary>
        /// Creates a new mail server user account.
        /// </summary>
        /// <param name="request">The payload containing the user's account details and credentials.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An HTTP 201 Created response containing the newly created user's <see cref="UserDto"/> if successful;
        /// an HTTP 400 Bad Request if required fields are missing;
        /// or an HTTP 409 Conflict if the username already exists.
        /// </returns>
        [HttpPost]
        public async Task<ActionResult<UserDto>> CreateUser(
            [FromBody] CreateUserRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.UserName)) {
                return BadRequest(new { error = "User name cannot be empty." });
            }
            if (string.IsNullOrWhiteSpace(request.Password)) {
                return BadRequest(new { error = "Password cannot be empty." });
            }

            string normalizedUserName = request.UserName.Trim().ToLowerInvariant();

            long userId = await _dbService.CreateUserAsync(
                normalizedUserName,
                request.Password,
                request.Description?.Trim(),
                request.IsEnabled,
                request.StorageQuotaBytes,
                request.MaxDailySends,
                cancellationToken);

            if (userId == -1) {
                return Conflict(new { error = "User already exists." });
            }

            // Fetch the created user record
            var createdUser = await _dbService.GetUserAsync(normalizedUserName, cancellationToken);
            if (createdUser == null) {
                return StatusCode(500, new { error = "User was created but could not be retrieved." });
            }

            // Map UserInfo to UserDto
            var userDto = new UserDto(
                UserId: createdUser.Value.UserId.ToString(),
                UserName: createdUser.Value.UserName,
                Description: createdUser.Value.Description,
                EmailAddresses: createdUser.Value.EmailAddresses,
                StorageQuotaBytes: createdUser.Value.QuotaMailboxSize,
                StorageUsageBytes: createdUser.Value.QuotaCurrentUsageBytes,
                MaxDailySends: createdUser.Value.QuotaMaxDailySends,
                SentCountToday: createdUser.Value.QuotaSentCountToday,
                CreateDate: createdUser.Value.CreateDate,
                LastLoginDate: createdUser.Value.LastLoginDate,
                IsEnabled: createdUser.Value.IsEnabled
            );

            return CreatedAtAction(nameof(GetUser), new { userName = normalizedUserName }, userDto);
        }

        /// <summary>
        /// Updates an existing user account's credentials, status, or quota settings.
        /// </summary>
        /// <param name="userId">The string representation of the unique 64-bit user ID from the route.</param>
        /// <param name="request">The payload containing the fields to update (unprovided or null fields remain unchanged).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An HTTP 200 OK response with a success message if updated;
        /// an HTTP 400 Bad Request if the user ID format is invalid;
        /// or an HTTP 404 Not Found if the user does not exist.
        /// </returns>
        [HttpPut("{userId}")]
        public async Task<IActionResult> UpdateUser(
            string userId,
            [FromBody] UpdateUserRequest request,
            CancellationToken cancellationToken = default)
        {
            // Safely parse string ID back to long for the database service
            if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedUserId)) {
                return BadRequest(new { error = "Invalid User ID format." });
            }

            bool updated = await _dbService.UpdateUserAsync(
                parsedUserId,
                request.Password,
                request.Description?.Trim(),
                request.IsEnabled,
                request.StorageQuotaBytes,
                request.MaxDailySends,
                cancellationToken);

            if (!updated) {
                return NotFound(new { error = "User not found or update failed." });
            }

            return Ok(new { message = "User updated successfully." });
        }

        /// <summary>
        /// Deletes an existing user account and all of its associated email addresses.
        /// </summary>
        /// <param name="userId">The string representation of the unique 64-bit user ID from the route.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An HTTP 204 No Content response if the deletion is successful;
        /// an HTTP 400 Bad Request if the user ID format is invalid;
        /// or an HTTP 404 Not Found if the user does not exist.
        /// </returns>
        [HttpDelete("{userId}")]
        public async Task<IActionResult> DeleteUser(string userId, CancellationToken cancellationToken = default)
        {
            // Safely parse string ID back to long for the database service
            if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedUserId)) {
                return BadRequest(new { error = "Invalid User ID format." });
            }

            string? error = await _dbService.DeleteUserAsync(parsedUserId, cancellationToken);
            if (error is not null) {
                return NotFound(new { error });
            }

            return NoContent();
        }

        /// <summary>
        /// Associates a new email address or alias with an existing user account.
        /// </summary>
        /// <param name="userId">The string representation of the unique 64-bit user ID from the route.</param>
        /// <param name="request">The payload containing the email address to add.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An HTTP 200 OK response with a success message if added;
        /// an HTTP 400 Bad Request if the user ID format is invalid or the email is empty;
        /// or an HTTP 409 Conflict if the email address already exists or validation fails.
        /// </returns>
        [HttpPost("{userId}/emails")]
        public async Task<IActionResult> CreateEmailAddress(
            string userId,
            [FromBody] CreateEmailAddressRequest request,
            CancellationToken cancellationToken = default)
        {
            // Safely parse string ID back to long for the database service
            if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedUserId)) {
                return BadRequest(new { error = "Invalid User ID format." });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.EmailAddress)) {
                return BadRequest(new { error = "Email address cannot be empty." });
            }

            string normalizedEmail = request.EmailAddress.Trim().ToLowerInvariant();

            string? error = await _dbService.CreateEmailAddressAsync(parsedUserId, normalizedEmail, cancellationToken);
            if (error is not null) {
                return Conflict(new { error });
            }

            return Ok(new { message = "Email address added successfully.", emailAddress = normalizedEmail });
        }
        
        /// <summary>
        /// Removes an associated email address or alias from a specific user account.
        /// </summary>
        /// <param name="userId">The string representation of the unique 64-bit user ID from the route.</param>
        /// <param name="request">The payload containing the email address to remove.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// An HTTP 204 No Content response if successfully removed;
        /// an HTTP 400 Bad Request if the user ID format is invalid or the email is empty;
        /// or an HTTP 404 Not Found if the email address is not associated with the user.
        /// </returns>
        [HttpDelete("{userId}/emails")]
        public async Task<IActionResult> RemoveEmailAddress(
            string userId,
            [FromBody] CreateEmailAddressRequest request,
            CancellationToken cancellationToken = default)
        {
            // Safely parse string ID back to long for the database service
            if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedUserId)) {
                return BadRequest(new { error = "Invalid User ID format." });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.EmailAddress)) {
                return BadRequest(new { error = "Email address cannot be empty." });
            }

            string normalizedEmail = request.EmailAddress.Trim().ToLowerInvariant();

            string? error = await _dbService.DeleteEmailAddressAsync(parsedUserId, normalizedEmail, cancellationToken);
            if (error is not null) {
                return NotFound(new { error });
            }

            return NoContent();
        }
    }
}