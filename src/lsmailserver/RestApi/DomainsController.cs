using lsMailServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static lsMailServer.RestApi.UsersController;
using static lsMailServer.Services.DatabaseService;

namespace lsMailServer.RestApi 
{
    /// <summary>
    /// Manages mail server domains, including creation, retrieval, updates, and deletion.
    /// </summary>
    [ApiController]
    [Route("api/domains")]
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

        private readonly DatabaseService _dbService;

        /// <summary>
        /// Initializes a new instance of the <see cref="DomainsController"/> class.
        /// </summary>
        /// <param name="dbService">The database service used for domain operations.</param>
        public DomainsController(DatabaseService dbService)
        {
            _dbService = dbService;
        }

        
        /// <summary>
        /// Retrieves all configured mail domains, with optional filtering.
        /// </summary>
        /// <param name="isEnabled">Optional filter to include only enabled or disabled domains.</param>
        /// <param name="search">Optional search term to filter domains by name.</param>
        /// <returns>An enumerable collection of <see cref="DomainDto"/> objects.</returns>
        [HttpGet]
        [Authorize]
        [ProducesResponseType(typeof(IEnumerable<DomainDto>), StatusCodes.Status200OK)]
        public IActionResult GetDomains([FromQuery] bool? isEnabled = null, [FromQuery] string? search = null)
        {
            var retVal = _dbService.GetDomains(isEnabled, search)
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
        /// <returns>The domain details if found; otherwise, a 404 Not Found response.</returns>
        [HttpGet("{domainName}")]
        [Authorize]
        [ProducesResponseType(typeof(DomainDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult GetDomain(string domainName)
        {
            if (string.IsNullOrWhiteSpace(domainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            var domain = _dbService.GetDomain(domainName.Trim().ToLowerInvariant());
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
        /// <returns>The created domain details.</returns>
        [HttpPost]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public IActionResult CreateDomain([FromBody] CreateDomainRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.DomainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            var normalizedDomainName = request.DomainName.Trim().ToLowerInvariant();
            var trimmedDescription = request.Description?.Trim();

            bool added = _dbService.CreateDomain(normalizedDomainName, trimmedDescription);
            if (!added) {
                return Conflict(new { error = "Domain already exists." });
            }

            var createdDto = new DomainDto(normalizedDomainName, trimmedDescription, request.IsEnabled);

            // Ensure you have an action named "GetDomain" (taking a domainName route parameter) 
            // for proper resource location headers, or point to your lookup route.
            return CreatedAtAction(nameof(GetDomain), new { domainName = normalizedDomainName }, createdDto);
        }
        
        /// <summary>
        /// Updates an existing mail domain.
        /// </summary>
        /// <param name="domainName">The name of the domain to update.</param>
        /// <param name="request">The update payload containing the new description and enabled status.</param>
        /// <returns>The updated domain details if successful; otherwise, a 404 Not Found response.</returns>
        [HttpPut("{domainName}")]
        [Authorize]
        [ProducesResponseType(typeof(DomainDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult UpdateDomain(string domainName, [FromBody] UpdateDomainRequest request)
        {
            if (string.IsNullOrWhiteSpace(domainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            var normalizedDomainName = domainName.Trim().ToLowerInvariant();
            var trimmedDescription = request.Description?.Trim();

            bool updated = _dbService.UpdateDomain(
                normalizedDomainName, 
                trimmedDescription,
                request.IsEnabled
            );

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
        /// <returns>A 204 No Content response if successful; otherwise, a 404 Not Found or 400 Bad Request.</returns>
        [HttpDelete("{domainName}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult DeleteDomain(string domainName)
        {
            if (string.IsNullOrWhiteSpace(domainName)) {
                return BadRequest(new { error = "Domain name cannot be empty." });
            }

            bool deleted = _dbService.DeleteDomain(domainName.Trim().ToLowerInvariant());
            if (!deleted) {
                return NotFound(new { error = "Domain not found." });
            }

            return NoContent();
        }
    }    
}
