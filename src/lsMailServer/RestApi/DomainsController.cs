using lsMailServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace lsMailServer.RestApi 
{
    /// <summary>
    /// Manages mail server domains, including creation, retrieval, updates, and deletion.
    /// </summary>
    [ApiController]
    [Route("api/domains")]
    [Authorize]
    public class DomainsController : ControllerBase
    { 
        /// <summary>
        /// Represents a mail domain data transfer object.
        /// </summary>
        /// <param name="Name">The unique domain name.</param>
        /// <param name="Description">Optional description or notes for the domain.</param>
        /// <param name="IsEnabled">Indicates whether mail routing is active for this domain.</param>
        public record DomainDto(string Name, string? Description, bool IsEnabled);

        /// <summary>
        /// Represents the payload required to create a new mail domain.
        /// </summary>
        /// <param name="DomainName">The unique domain name to be created.</param>
        /// <param name="Description">Optional description or notes for the domain.</param>
        /// <param name="IsEnabled">Indicates whether mail routing should be active immediately upon creation.</param>
        public record CreateDomainRequest(string DomainName, string? Description, bool IsEnabled = true);

        /// <summary>
        /// Represents the payload required to update an existing mail domain.
        /// </summary>
        /// <param name="Description">The updated description or notes for the domain (can be null to clear).</param>
        /// <param name="IsEnabled">Indicates whether mail routing should be active for this domain.</param>
        public record UpdateDomainRequest(string? Description, bool IsEnabled);

        private readonly IDatabaseService _dbService;

        /// <summary>
        /// Initializes a new instance of the <see cref="DomainsController"/> class.
        /// </summary>
        /// <param name="dbService">The database service used for domain operations.</param>
        public DomainsController(IDatabaseService dbService)
        {
            _dbService = dbService;
        }

        
        /// <summary>
        /// Retrieves all configured mail domains, with optional filtering.
        /// </summary>
        /// <param name="isEnabled">Optional filter to include only enabled or disabled domains.</param>
        /// <param name="search">Optional search term to filter domains by name.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of <see cref="DomainDto"/> objects.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<DomainDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetDomains(
            [FromQuery] bool? isEnabled = null,
            [FromQuery] string? search = null,
            CancellationToken cancellationToken = default)
        {
            var domains = await _dbService.GetDomainsAsync(isEnabled, search, cancellationToken);

            var retVal = domains
                .Select(domain => new DomainDto(
                    Name: domain.Name,
                    Description: domain.Description,
                    IsEnabled: domain.IsEnabled
                ))
                .ToList();

            return Ok(retVal);
        }

        /// <summary>
        /// Retrieves a specific mail domain by its name.
        /// </summary>
        /// <param name="domainName">The domain name to retrieve.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The domain details if found; otherwise, a 404 Not Found response.</returns>
        [HttpGet("{domainName}")]
        [ProducesResponseType(typeof(DomainDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetDomain(string domainName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(domainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            var domain = await _dbService.GetDomainAsync(domainName.Trim().ToLowerInvariant(), cancellationToken);
            if (domain == null) {
                return NotFound(new { error = "Domain not found." });
            }

            var retVal = new DomainDto(Name: domain.Name, Description: domain.Description, IsEnabled: domain.IsEnabled);

            return Ok(retVal);
        }
        
        /// <summary>
        /// Creates a new mail domain.
        /// </summary>
        /// <param name="request">The domain creation payload.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The created domain details.</returns>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateDomain([FromBody] CreateDomainRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.DomainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            var normalizedDomainName = request.DomainName.Trim().ToLowerInvariant();
            var trimmedDescription = request.Description?.Trim();
            var isEnabled = request.IsEnabled;

            bool added = await _dbService.CreateDomainAsync(normalizedDomainName, trimmedDescription, isEnabled, cancellationToken); 
            if (!added) {
                return Conflict(new { error = "Domain already exists." });
            }

            var createdDto = new DomainDto(normalizedDomainName, trimmedDescription, request.IsEnabled);

            return CreatedAtAction(nameof(GetDomain), new { domainName = normalizedDomainName }, createdDto);
        }
        
        /// <summary>
        /// Updates an existing mail domain.
        /// </summary>
        /// <param name="domainName">The name of the domain to update.</param>
        /// <param name="request">The update payload containing the new description and enabled status.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The updated domain details if successful; otherwise, a 404 Not Found response.</returns>
        [HttpPut("{domainName}")]
        [ProducesResponseType(typeof(DomainDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> UpdateDomain(
            string domainName,
            [FromBody] UpdateDomainRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(domainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            var normalizedDomainName = domainName.Trim().ToLowerInvariant();
            var trimmedDescription = request.Description?.Trim();

            bool updated = await _dbService.UpdateDomainAsync(
                normalizedDomainName,
                trimmedDescription,
                request.IsEnabled,
                cancellationToken);

            if (!updated) {
                return NotFound(new { error = "Domain not found." });
            }

            var updatedDto = new DomainDto(normalizedDomainName, trimmedDescription, request.IsEnabled);

            return Ok(updatedDto);
        }

        /// <summary>
        /// Deletes an existing mail domain.
        /// </summary>
        /// <param name="domainName">The name of the domain to delete.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A 204 No Content response if successful; otherwise, a 404 Not Found or 400 Bad Request.</returns>
        [HttpDelete("{domainName}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> DeleteDomain(string domainName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(domainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            bool deleted = await _dbService.DeleteDomainAsync(domainName.Trim().ToLowerInvariant(), cancellationToken);
            if (!deleted) {
                return NotFound(new { error = "Domain not found." });
            }

            return NoContent();
        }
    }    
}
