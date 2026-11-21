using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace lsMailServer.RestApi 
{  
    /// <summary>
    /// Manages administrative authentication, session lifecycle, and current user verification
    /// using database credentials and secure cookie-based authentication.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        /// <summary>
        /// Represents the data payload required for user authentication.
        /// </summary>
        /// <param name="Username">The administrator username.</param>
        /// <param name="Password">The plain-text password for verification.</param>
        public record LoginRequest(string Username, string Password);

        private readonly IConfiguration _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="AuthController"/> class.
        /// </summary>
        /// <param name="config">The application configuration instance.</param>
        public AuthController(IConfiguration config)
        {
            _config = config;
        }

        /// <summary>
        /// Authenticates an user against the database and issues a secure, 
        /// encrypted cookie-based session ticket.
        /// </summary>
        /// <param name="req">The login request payload containing the username and password.</param>
        /// <returns>
        /// An <see cref="IActionResult"/> containing the authenticated username on success, 
        /// or a <see cref="BadRequestResult"/> / <see cref="UnauthorizedResult"/> on failure.
        /// </returns>
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.Username) || string.IsNullOrWhiteSpace(req?.Password))
                return BadRequest(new { error = "Username and password are required." });

            var expectedUser = _config["AdminAuth:Username"] ?? "admin";
            var expectedPass = _config["AdminAuth:Password"] ?? string.Empty;

            // Constant-time comparison to prevent timing attacks
            bool userMatch = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(req.Username),
                Encoding.UTF8.GetBytes(expectedUser));

            bool passMatch = !string.IsNullOrEmpty(expectedPass) && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(req.Password),
                Encoding.UTF8.GetBytes(expectedPass));

            if (!userMatch || !passMatch)
                return Unauthorized(new { error = "Invalid username or password." });

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, expectedUser),
                new Claim(ClaimTypes.Role, "Admin")
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                });

            return Ok(new { username = expectedUser });
        }

        /// <summary>
        /// Terminates the current administrator session and clears the authentication cookie.
        /// </summary>
        /// <returns>An <see cref="IActionResult"/> indicating successful session termination.</returns>
        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok(new { message = "Logged out successfully." });
        }

        /// <summary>
        /// Retrieves the profile details of the currently authenticated administrator.
        /// </summary>
        /// <returns>
        /// An <see cref="IActionResult"/> containing the username of the active session, 
        /// or an <see cref="UnauthorizedResult"/> if the session is invalid or missing.
        /// </returns>
        [HttpGet("me")]
        [Authorize]
        public IActionResult Me()
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username))
                return Unauthorized();

            return Ok(new { username });
        }
    }
}