using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using lsMailServer.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace lsMailServer.RestApi
{
    /// <summary>
    /// REST API controller for reading and updating strongly typed mail server settings
    /// and managing PKCS#12 (<c>.pfx</c> / <c>.p12</c>) TLS certificates in <c>Data/Certificates</c>.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public sealed class SettingsController : ControllerBase
    {
        /// <summary>
        /// Metadata returned for a certificate file stored in <c>Data/Certificates</c>.
        /// </summary>
        /// <param name="Name">The bare certificate file name.</param>
        /// <param name="SizeBytes">The file size in bytes.</param>
        /// <param name="LastModifiedUtc">The UTC timestamp when the file was last written.</param>
        public sealed record CertificateFileInfo(
            string Name,
            long SizeBytes,
            DateTime LastModifiedUtc);

        private static readonly HashSet<string> AllowedCertExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pfx",
            ".p12"
        };

        private readonly MailServerSettingsStore _settingsStore;
        private readonly string _certificatesDirectory;

        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsController"/> class.
        /// </summary>
        /// <param name="settingsStore">The persistence store for <see cref="MailServerSettings"/>.</param>
        /// <param name="environment">The web host environment used to resolve <c>Data/Certificates</c>.</param>
        public SettingsController(MailServerSettingsStore settingsStore, IWebHostEnvironment environment)
        {
            ArgumentNullException.ThrowIfNull(settingsStore);
            ArgumentNullException.ThrowIfNull(environment);

            _settingsStore = settingsStore;
            _certificatesDirectory = Path.Combine(environment.ContentRootPath, "Data", "Certificates");
        }

        /// <summary>
        /// Returns the current server settings with all password fields stripped to <see langword="null"/>.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The sanitized <see cref="MailServerSettings"/> payload.</returns>
        [HttpGet]
        public async Task<ActionResult<MailServerSettings>> GetSettings(CancellationToken cancellationToken)
        {
            MailServerSettings sanitized = await _settingsStore
                .GetSanitizedForUiAsync(cancellationToken)
                .ConfigureAwait(false);

            return Ok(sanitized);
        }

        /// <summary>
        /// Validates and saves updated settings from the Admin UI, keeping existing secrets when password fields are <see langword="null"/>.
        /// </summary>
        /// <param name="incoming">The settings payload submitted by the Admin UI.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns><c>204 No Content</c> on success, or <c>400 Bad Request</c> if validation fails.</returns>
        [HttpPut]
        public async Task<IActionResult> UpdateSettings(
            [FromBody] MailServerSettings incoming,
            CancellationToken cancellationToken)
        {
            if (incoming is null) {
                return BadRequest(new { error = "Request body cannot be null." });
            }

            try {
                await _settingsStore.SaveFromUiAsync(incoming, cancellationToken).ConfigureAwait(false);
                return NoContent();
            }
            catch (ArgumentException ex) {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Lists all <c>.pfx</c> and <c>.p12</c> certificate files available in <c>Data/Certificates</c>.
        /// </summary>
        /// <returns>A list of certificate metadata entries sorted alphabetically by file name.</returns>
        [HttpGet("certificates")]
        public ActionResult<IReadOnlyList<CertificateFileInfo>> GetCertificates()
        {
            if (!Directory.Exists(_certificatesDirectory)) {
                return Ok(Array.Empty<CertificateFileInfo>());
            }

            var files = new DirectoryInfo(_certificatesDirectory)
                .EnumerateFiles()
                .Where(f => AllowedCertExtensions.Contains(f.Extension))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new CertificateFileInfo(
                    f.Name,
                    f.Length,
                    f.LastWriteTimeUtc))
                .ToList();

            return Ok(files);
        }

        /// <summary>
        /// Uploads or replaces a <c>.pfx</c> / <c>.p12</c> certificate file inside <c>Data/Certificates</c>.
        /// </summary>
        /// <param name="file">The uploaded PKCS#12 certificate file.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The saved <see cref="CertificateFileInfo"/> metadata.</returns>
        [HttpPost("certificates")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB max
        public async Task<ActionResult<CertificateFileInfo>> UploadCertificate(
            IFormFile? file,
            CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0) {
                return BadRequest(new { error = "No certificate file was provided." });
            }

            // Strip any path segments so only the bare file name is used.
            string safeFileName = Path.GetFileName(file.FileName).Trim();
            if (!IsValidCertificateFileName(safeFileName)) {
                return BadRequest(new { error = "Invalid certificate file name. Only .pfx or .p12 files are allowed." });
            }

            Directory.CreateDirectory(_certificatesDirectory);
            string destinationPath = Path.Combine(_certificatesDirectory, safeFileName);
            string tempPath = destinationPath + ".tmp";

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            System.IO.File.Move(tempPath, destinationPath, overwrite: true);

            var info = new FileInfo(destinationPath);
            return Ok(new CertificateFileInfo(info.Name, info.Length, info.LastWriteTimeUtc));
        }

        /// <summary>
        /// Deletes a certificate file by name from <c>Data/Certificates</c>.
        /// </summary>
        /// <param name="fileName">The bare file name (e.g., <c>"mail.lumisoft.ee.pfx"</c>).</param>
        /// <returns><c>204 No Content</c> if deleted, <c>400 Bad Request</c> if invalid, or <c>404 Not Found</c> if missing.</returns>
        [HttpDelete("certificates/{fileName}")]
        public IActionResult DeleteCertificate(string fileName)
        {
            string safeFileName = Path.GetFileName(fileName ?? string.Empty).Trim();
            if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal) ||
                !IsValidCertificateFileName(safeFileName))
            {
                return BadRequest(new { error = "Invalid certificate file name." });
            }

            string targetPath = Path.Combine(_certificatesDirectory, safeFileName);
            if (!System.IO.File.Exists(targetPath)) {
                return NotFound(new { error = $"Certificate '{safeFileName}' was not found." });
            }

            System.IO.File.Delete(targetPath);
            return NoContent();
        }

        /// <summary>
        /// Validates that <paramref name="fileName"/> is a safe single file name with a <c>.pfx</c> or <c>.p12</c> extension.
        /// </summary>
        private static bool IsValidCertificateFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains("..", StringComparison.Ordinal) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            string ext = Path.GetExtension(fileName);
            return AllowedCertExtensions.Contains(ext);
        }
    }    
}