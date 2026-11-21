using lsMailServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace lsMailServer.RestApi 
{
    /// <summary>
    /// Provides REST API endpoints for inspecting and managing outbound messages in the SMTP relay queue.
    /// </summary>
    [ApiController]
    [Route("api/relay-queue")]
    public sealed class RelayQueueController : ControllerBase
    {
        /// <summary>
        /// Represents a queued outbound message exposed by the management REST API.
        /// </summary>
        public sealed record RelayQueueItemDto(
            string Id,
            string EnvelopeSender,
            string RecipientAddress,
            string RecipientDomain,
            int AttemptCount,
            DateTime FirstAttemptTime,
            DateTime NextRetryTime,
            bool DelayedNoticeSent,
            string? LastError,
            long? MessageSizeBytes
        );

        private readonly SmtpRelayService _relayService;

        /// <summary>
        /// Initializes a new instance of the <see cref="RelayQueueController"/> class.
        /// </summary>
        /// <param name="relayService">The <see cref="SmtpRelayService"/> instance used to inspect and manage queued outbound messages.</param>
        public RelayQueueController(SmtpRelayService relayService)
        {
            _relayService = relayService;
        }

        /// <summary>
        /// Retrieves all outbound messages currently queued for retry delivery.
        /// </summary>
        /// <returns>
        /// An <see cref="ActionResult{TValue}"/> containing a read-only list of <see cref="RelayQueueItemDto"/>
        /// objects representing the deferred messages in the relay queue.
        /// </returns>
        [HttpGet]
        [Authorize]
        public ActionResult<IReadOnlyList<RelayQueueItemDto>> GetQueue()
        {
            return Ok(_relayService.GetQueuedItems());
        }
    }
}
