using lsMailServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace lsMailServer.RestApi
{
    /// <summary>
    /// Provides REST API endpoints for inspecting and managing active client sessions
    /// across the IMAP, POP3, and SMTP servers.
    /// </summary>
    [ApiController]
    [Route("api/sessions")]
    [Authorize]
    public sealed class SessionsController : ControllerBase
    {
        #region record SessionDto

        /// <summary>
        /// Represents a point-in-time snapshot of an active TCP client session.
        /// </summary>
        /// <param name="Id">The unique session identifier (<c>TCP_Session.ID</c>).</param>
        /// <param name="Protocol">The mail protocol handling the session (e.g., <c>"IMAP"</c>, <c>"POP3"</c>, or <c>"SMTP"</c>).</param>
        /// <param name="RemoteEndPoint">The client's remote IP address and TCP port.</param>
        /// <param name="LocalEndPoint">The local server IP address and listening TCP port.</param>
        /// <param name="IsSecure">Indicates whether the connection is encrypted via TLS.</param>
        /// <param name="IsAuthenticated">Indicates whether the client has successfully authenticated.</param>
        /// <param name="UserName">The authenticated user identity name, or <see langword="null"/> if unauthenticated.</param>
        /// <param name="Status">The current session activity status (e.g., <c>"Waiting"</c> or <c>"Executing IDLE"</c>).</param>
        /// <param name="ConnectTime">The UTC timestamp when the client initially connected.</param>
        /// <param name="LastActivity">The UTC timestamp when data was last sent or received on the session.</param>
        public sealed record SessionDto(
            string Id,
            string Protocol,
            string RemoteEndPoint,
            string LocalEndPoint,
            bool IsSecure,
            bool IsAuthenticated,
            string? UserName,
            string? Status,
            DateTime ConnectTime,
            DateTime LastActivity
        );

        #endregion

        private readonly SmtpService _smtpService;
        private readonly Pop3Service _pop3Service;
        private readonly ImapService _imapService;
        private readonly SmtpRelayService _relayService;

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionsController"/> class.
        /// </summary>
        /// <param name="smtpService">The inbound SMTP service used to inspect and disconnect active SMTP sessions.</param>
        /// <param name="pop3Service">The inbound POP3 service used to inspect and disconnect active POP3 sessions.</param>
        /// <param name="imapService">The inbound IMAP4 service used to inspect and disconnect active IMAP sessions.</param>
        /// <param name="relayService">The outbound SMTP relay service used to inspect and disconnect active relay sessions.</param>
        public SessionsController(SmtpService smtpService, Pop3Service pop3Service, ImapService imapService, SmtpRelayService relayService)
        {
            _smtpService = smtpService;
            _pop3Service = pop3Service;
            _imapService = imapService;
            _relayService = relayService;
        }

        #region method GetSessions

        /// <summary>
        /// Retrieves a snapshot of all currently connected client sessions across IMAP, POP3, SMTP and SMTP Relay
        /// ordered chronologically by connection time.
        /// </summary>
        /// <returns>An <see cref="ActionResult{T}"/> containing the list of active <see cref="SessionDto"/> entries.</returns>
        [HttpGet]
        public ActionResult<IReadOnlyList<SessionDto>> GetSessions()
        {
            var sessions = new List<SessionDto>();
            sessions.AddRange(_smtpService.GetSessions());
            sessions.AddRange(_pop3Service.GetSessions());
            sessions.AddRange(_imapService.GetSessions());
            sessions.AddRange(_relayService.GetSessions());
            sessions.Sort((a, b) => b.ConnectTime.CompareTo(a.ConnectTime));

            return Ok(sessions);
        }

        #endregion

        #region method DisconnectSession

        /// <summary>
        /// Forcefully disconnects an active client session identified by protocol and session ID.
        /// </summary>
        /// <param name="protocol">The protocol name (<c>IMAP</c>, <c>POP3</c>, or <c>SMTP</c>).</param>
        /// <param name="id">The unique session identifier to terminate.</param>
        /// <returns>
        /// <see cref="NoContentResult"/> if the session was found and disconnected;
        /// <see cref="BadRequestResult"/> if the protocol is unsupported; or
        /// <see cref="NotFoundResult"/> if no matching active session exists.
        /// </returns>
        [HttpDelete("{protocol}/{id}")]
        public IActionResult DisconnectSession(string protocol, string id)
        {
            if (string.IsNullOrWhiteSpace(protocol) || string.IsNullOrWhiteSpace(id)) {
                return BadRequest();
            }

            if(protocol.Equals("IMAP", StringComparison.OrdinalIgnoreCase)) {
                _imapService.DisconnectSession(id);
            }
            else if(protocol.Equals("POP3", StringComparison.OrdinalIgnoreCase)) {
                _pop3Service.DisconnectSession(id);
            }
            else if(protocol.Equals("SMTP", StringComparison.OrdinalIgnoreCase)) {
                _smtpService.DisconnectSession(id);
            }
            else if(protocol.Equals("Relay", StringComparison.OrdinalIgnoreCase)) {
                _relayService.DisconnectSession(id);
            }
            else{
                return BadRequest($"Unsupported protocol '{protocol}'.");
            }

            return NoContent();
        }

        #endregion
    }
}