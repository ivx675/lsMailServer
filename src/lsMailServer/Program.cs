using lsMailServer.Configuration;
using lsMailServer.Logging;
using lsMailServer.Services;
using LumiSoft.Net.DNS.Client;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Primitives;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace lsMailServer
{
    internal class Program
    {
        private static readonly SemaphoreSlim _reloadLock = new(1, 1);

        static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });

            // 1. Establish the isolated instance Data root
            string baseDir = AppContext.BaseDirectory;
            string dataDir = Path.Combine(baseDir, "Data");
            string mailStoreDir = Path.Combine(dataDir, "MailStore");
            string logsDir = Path.Combine(dataDir, "Logs");
            string appSettingsPath = Path.Combine(dataDir, "appsettings.json");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(Path.Combine(dataDir, "Certificates"));
            Directory.CreateDirectory(Path.Combine(mailStoreDir, "Mailboxes"));
            Directory.CreateDirectory(logsDir);            

            if (!File.Exists(appSettingsPath)) {
                using Stream? resourceStream = typeof(Program).Assembly
                    .GetManifestResourceStream("lsMailServer.appsettings.json");

                if (resourceStream != null) {
                    using FileStream fileStream = File.Create(appSettingsPath);
                    resourceStream.CopyTo(fileStream);
                }
            }
                        
            // 2. Load appsettings.json directly from inside the Data directory
            //string appSettingsPath = Path.Combine(dataDir, "appsettings.json");
            builder.Configuration
                .SetBasePath(dataDir)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            builder.Services.Configure<MailServerSettings>(builder.Configuration);
            builder.Services.AddSingleton(new MailServerSettingsStore(appSettingsPath));

            // 3. Register platform service hooks
            builder.Services.AddWindowsService();
            builder.Services.AddSystemd();

            // 4. Register logging buffer, reader, writer, and ILoggerProvider
            int liveBufferCapacity = builder.Configuration.GetValue("LoggingSettings:LiveBufferCapacity", 500);
            string configuredLogsDir = builder.Configuration["LoggingSettings:LogDirectory"] ?? logsDir;
            Directory.CreateDirectory(configuredLogsDir);

            builder.Services.AddSingleton<ILiveLogBuffer>(_ => new LiveLogBuffer(liveBufferCapacity));
            builder.Services.AddSingleton<IStoredLogReader>(_ => new StoredLogReader(configuredLogsDir));
            builder.Services.AddSingleton<IStoredLogWriter>(_ => new StoredLogWriter(configuredLogsDir));
            builder.Services.AddSingleton<ILoggerProvider>(sp =>
                new CustomLogProvider(
                    sp.GetRequiredService<ILiveLogBuffer>(),
                    sp.GetRequiredService<IStoredLogWriter>()
                ));
            
            // 5. Register core data, storage, relay, and mail server hosted services
            builder.Services.AddSingleton<FailedAuthLimiterService>();
            builder.Services.AddSingleton<IDatabaseService>(_ => new SqliteDatabaseService(dataDir));
            builder.Services.AddSingleton(sp => new MailStoreService(sp.GetRequiredService<IDatabaseService>(), mailStoreDir));
            builder.Services.AddSingleton<SmtpRelayService>(sp =>
                new SmtpRelayService(
                    sp.GetRequiredService<ILogger<SmtpRelayService>>(),
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<IDatabaseService>(),
                    mailStoreDir
                ));
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SmtpRelayService>());
            builder.Services.AddSingleton<SmtpService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SmtpService>());
            builder.Services.AddSingleton<Pop3Service>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Pop3Service>());
            builder.Services.AddSingleton<ImapService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ImapService>());

            // 6. Register Cookie Authentication
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = "MailAdmin.Auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = true;

                    // Return 401 Unauthorized status instead of a redirect when unauthenticated
                    options.Events.OnRedirectToLogin = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    };
                });

            builder.Services.AddAuthorization();

            // 7. Register Controllers, API Explorer, and Swagger Generation
            builder.Services.AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new IPAddressJsonConverter());
                });
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                string xmlPath = Path.Combine(baseDir, "lsMailServer.xml");
                if (File.Exists(xmlPath))
                {
                    options.IncludeXmlComments(xmlPath);
                }
            });

            var app = builder.Build();

            // 8. Restrict Web UI, REST API, and Swagger to allowed IPs and CIDR ranges
            var allowedEntries = app.Configuration.GetSection("WebServer:AllowedIPs").Get<string[]>()
                ?? new[] { "127.0.0.1", "::1" };

            var allowedIps = new List<IPAddress>();
            var allowedNetworks = new List<IPNetwork>();

            foreach (string entry in allowedEntries) {
                string trimmed = entry.Trim();
                if (string.IsNullOrEmpty(trimmed)) {
                    continue;
                }

                if (trimmed.Contains('/')) {
                    if (IPNetwork.TryParse(trimmed, out var network)) {
                        allowedNetworks.Add(network);
                    }
                }
                else if (IPAddress.TryParse(trimmed, out var ip)) {
                    allowedIps.Add(ip);
                }
            }

            app.Use(async (context, next) => {
                var remoteIp = context.Connection.RemoteIpAddress;

                // Normalize IPv4-mapped IPv6 addresses (e.g. ::ffff:192.168.1.5 -> 192.168.1.5)
                if (remoteIp != null && remoteIp.IsIPv4MappedToIPv6) {
                    remoteIp = remoteIp.MapToIPv4();
                }

                bool isAllowed = remoteIp != null && (
                    allowedIps.Contains(remoteIp) ||
                    allowedNetworks.Any(net => net.Contains(remoteIp))
                );

                if (!isAllowed) {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next();
            });

            // 9. Enable Swagger middleware
            app.UseSwagger();
            app.UseSwaggerUI();

            // 10. Enable static files for the web UI in wwwRoot
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // 11. Enable Authentication & Authorization middleware (Must be before MapControllers)
            app.UseAuthentication();
            app.UseAuthorization();

            // 12. Map route endpoints for controllers
            app.MapControllers();

            EnsureFirstKestrelHttpsCertificate(app.Configuration, dataDir);

            ApplyDnsServers(app.Configuration);
            ChangeToken.OnChange(() => app.Configuration.GetReloadToken(), () => _= OnSettingsChangedAsync(app.Services));

            await app.RunAsync();
        }


        #region static method OnSettingsChangedAsync

        /// <summary>
        /// Handles live configuration reload notifications by updating DNS resolver settings
        /// and restarting the inbound SMTP, POP3, and IMAP protocol listeners in a serialized manner.
        /// </summary>
        /// <param name="services">The root <see cref="IServiceProvider"/> used to resolve configuration, loggers, and hosted protocol services.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous configuration reload operation.</returns>
        private static async Task OnSettingsChangedAsync(IServiceProvider services)
        {
            await _reloadLock.WaitAsync();
            try {
                var config = services.GetRequiredService<IConfiguration>();
                var logger = services.GetRequiredService<ILogger<Program>>();

                ApplyDnsServers(config);

                var smtpService = services.GetRequiredService<SmtpService>();
                var pop3Service = services.GetRequiredService<Pop3Service>();
                var imapService = services.GetRequiredService<ImapService>();
                var relayService = services.GetRequiredService<SmtpRelayService>();

                logger.LogInformation("Configuration changed; restarting SMTP, POP3, and IMAP listeners...");

                await smtpService.StopAsync(CancellationToken.None);
                await smtpService.StartAsync(CancellationToken.None);

                await pop3Service.StopAsync(CancellationToken.None);
                await pop3Service.StartAsync(CancellationToken.None);

                await imapService.StopAsync(CancellationToken.None);
                await imapService.StartAsync(CancellationToken.None);

                relayService.ApplySettings();

                logger.LogInformation("SMTP, POP3, and IMAP listeners restarted successfully.");
            }
            catch (Exception ex) {
                var logger = services.GetService<ILogger<Program>>();
                logger?.LogError(ex, "Failed to apply reloaded configuration to mail services.");
            }
            finally {
                _reloadLock.Release();
            }
        }

        #endregion

        #region satic method ApplyDnsServers

        static void ApplyDnsServers(IConfiguration config)
        {
            var servers = (config.GetSection("General:DnsServers").Get<string[]>() ?? Array.Empty<string>())
                .Select(s => IPAddress.TryParse(s?.Trim(), out var ip) ? ip : null)
                .Where(ip => ip != null)
                .Cast<IPAddress>()
                .ToArray();

            if (servers.Length > 0) {
                DNS_Client.Static.DnsServers = servers;
            }
        }

        #endregion

        #region static method EnsureFirstKestrelHttpsCertificate

        /// <summary>
        /// Checks the first endpoint under <c>Kestrel:Endpoints</c>. If it uses an <c>https://</c> URL,
        /// verifies whether the configured certificate exists in <c>Data/Certificates</c>, and generates
        /// a self-signed PKCS#12 (<c>.pfx</c>) certificate using the configured filename and password if missing.
        /// </summary>
        /// <param name="configuration">The application configuration containing the <c>Kestrel:Endpoints</c> section.</param>
        /// <param name="dataDir">The root <c>Data</c> directory path containing the <c>Certificates</c> folder.</param>
        public static void EnsureFirstKestrelHttpsCertificate(IConfiguration configuration, string dataDir)
        {
            var firstEndpoint = configuration
                .GetSection("Kestrel:Endpoints")
                .GetChildren()
                .FirstOrDefault();

            if (firstEndpoint == null) {
                return;
            }

            string? url = firstEndpoint["Url"]?.Trim();
            if (string.IsNullOrEmpty(url) ||
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Certificate:Path (e.g. "Data/Certificates/kestrel.pfx")
            string? rawCertSpec = firstEndpoint["Certificate:Path"]?.Trim();
            
            string certFileName = !string.IsNullOrEmpty(rawCertSpec)
                ? Path.GetFileName(rawCertSpec.Trim('"'))
                : "kestrel.pfx";

            if (string.IsNullOrEmpty(certFileName)) {
                certFileName = "kestrel.pfx";
            }

            string certificatesDir = Path.Combine(dataDir, "Certificates");
            Directory.CreateDirectory(certificatesDir);

            string certFilePath = Path.Combine(certificatesDir, certFileName);
            if (File.Exists(certFilePath)) {
                return;
            }

            string password = firstEndpoint["Certificate:Password"] ?? "1234";

            // Extract host name from the HTTPS URL if present (fallback to "localhost")
            string hostName = "localhost";
            if (Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost").Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var parsedUri) &&
                !string.IsNullOrWhiteSpace(parsedUri.Host))
            {
                hostName = parsedUri.Host;
            }

            using X509Certificate2 cert = CreateSelfSignedCert(password, hostName);
            byte[] pfxBytes = string.IsNullOrEmpty(password)
                ? cert.Export(X509ContentType.Pfx)
                : cert.Export(X509ContentType.Pfx, password);

            File.WriteAllBytes(certFilePath, pfxBytes);
        }

        #endregion

        #region static method LoadOrCreateCert

        /// <summary>
        /// Loads an existing SSL/TLS certificate from <c>Data/Certificates</c>, or automatically generates,
        /// saves, and loads a new self-signed certificate if the file does not exist.
        /// </summary>
        /// <param name="certFileName">The PKCS#12 (<c>.pfx</c>) certificate file name inside <c>Data/Certificates</c>.</param>
        /// <param name="password">The password used to encrypt and protect the PKCS#12 (<c>.pfx</c>) certificate file.</param>
        /// <param name="hostName">The server hostname used for the certificate's Subject Name and Subject Alternative Names (SAN).</param>
        /// <param name="logger">The logger used to record certificate generation details.</param>
        /// <returns>An initialized <see cref="X509Certificate2"/> instance configured with persistent key storage and exportable keys.</returns>
        public static X509Certificate2 LoadOrCreateCert(string certFileName, string password, string hostName, ILogger logger)
        {
            string cerFile = Path.Combine(AppContext.BaseDirectory, "Data" , "Certificates", certFileName);
            if (!File.Exists(cerFile)) {
                var cert = CreateSelfSignedCert(password,hostName);
                File.WriteAllBytes(cerFile,cert.Export(X509ContentType.Pfx,password));
                
                logger.LogInformation(
                    "Generated new self-signed certificate successfully. " +
                    "Subject: {Subject} | Thumbprint: {Thumbprint} | Expires: {Expires:yyyy-MM-dd}",
                    cert.Subject,
                    cert.Thumbprint,
                    cert.NotAfter);
            }

            return X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(cerFile),
                password, 
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
            );
        }

        #endregion

        #region static method CreateSelfSignedCert

        /// <summary>
        /// Creates a cryptographically secure, self-signed SSL/TLS certificate configured for server authentication 
        /// with comprehensive Subject Alternative Names (SANs) and persistent key storage flags.
        /// </summary>
        /// <param name="password">The password used to encrypt the exported PKCS#12 (.pfx) data.</param>
        /// <param name="hostName">The primary domain name or host name assigned to the certificate's Common Name (CN) and SAN collection.</param>
        /// <returns>
        /// An initialized, exportable <see cref="X509Certificate2"/> instance with persistent key storage, 
        /// safe for Windows Schannel binding and mail server listener encryption.
        /// </returns>
        public static X509Certificate2 CreateSelfSignedCert(string password,string hostName)
        {
            var dn = new X500DistinguishedName($"CN={hostName}");

            using var rsa = RSA.Create(2048);

            var req = new CertificateRequest(
                dn,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Basic constraints: not a CA
            req.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));

            // Key usage: required for TLS server
            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature |
                    X509KeyUsageFlags.KeyEncipherment,
                    false));

            // Subject Key Identifier
            req.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            // REQUIRED: Server Authentication EKU
            var eku = new OidCollection {
                new Oid("1.3.6.1.5.5.7.3.1") // Server Authentication
            };
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(eku, false));

            // REQUIRED: Subject Alternative Name (SAN)
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName(hostName);
            sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
            sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            // Add the local machine name if it differs from the primary hostname
            string machineName = System.Net.Dns.GetHostName();
            if (!string.IsNullOrEmpty(machineName) && 
                !string.Equals(machineName, hostName, StringComparison.OrdinalIgnoreCase))
            {
                sanBuilder.AddDnsName(machineName);
            }
            req.CertificateExtensions.Add(sanBuilder.Build());

            var cert = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(5));

            // Export to PFX and re-import to resolve Windows Schannel ephemeral key limitations
            var pfxBytes = cert.Export(X509ContentType.Pfx,password);
    
            // Dispose the original ephemeral certificate to free resources
            cert.Dispose();

            cert = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

            return cert;
        }

        #endregion        
    }
}