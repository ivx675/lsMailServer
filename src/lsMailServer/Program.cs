using lsMailServer.Logging;
using lsMailServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Net;

namespace lsMailServer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // 1. Establish the isolated instance Data root
            string baseDir = AppContext.BaseDirectory;
            string dataDir = Path.Combine(baseDir, "Data");
            string mailStoreDir = Path.Combine(dataDir, "MailStore");
            string logsDir = Path.Combine(dataDir, "Logs");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(Path.Combine(dataDir, "Certificates"));
            Directory.CreateDirectory(Path.Combine(mailStoreDir, "Mailboxes"));
            Directory.CreateDirectory(logsDir);

            // 2. Load appsettings.json directly from inside the Data directory
            builder.Configuration
                .SetBasePath(dataDir)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

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
            builder.Services.AddSingleton<MailServerHostedService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<MailServerHostedService>());

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
            builder.Services.AddControllers();
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
                    //IPAddress.IsLoopback(remoteIp) ||
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

            await app.RunAsync();
        }
    }
}