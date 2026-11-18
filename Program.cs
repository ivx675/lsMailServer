using lsMailServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies; 
using System.Security.Cryptography.X509Certificates;

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
            string mailStoreDir = Path.Combine(baseDir, "Data", "MailStore");

            // CreateDirectory is completely safe if the folder already exists
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(Path.Combine(dataDir, "MailStore", "Mailboxes"));
            Directory.CreateDirectory(Path.Combine(dataDir, "MailStore", "Relay"));

            // 2. Load appsettings.json directly from inside the Data directory
            builder.Configuration
                .SetBasePath(dataDir)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            // 3. Register platform service hooks
            builder.Services.AddWindowsService();
            builder.Services.AddSystemd();

            // 4. Pass the data directory path into your database initializer
            builder.Services.AddSingleton(new DatabaseService(dataDir));
            builder.Services.AddSingleton(sp => new MailStoreService(sp.GetRequiredService<DatabaseService>(), mailStoreDir));
            builder.Services.AddSingleton<SmtpRelayService>(sp => 
                new SmtpRelayService(
                    sp.GetRequiredService<ILogger<SmtpRelayService>>(),
                    sp.GetRequiredService<IConfiguration>(),
                    mailStoreDir
                ));

            
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SmtpRelayService>());
            builder.Services.AddHostedService<MailServerHostedService>();

            // 5. Register Cookie Authentication
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = "MailAdmin.Auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Automatically handles HTTPS/HTTP
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

            // 6. Register Controllers, API Explorer, and Swagger Generation
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSwaggerGen(options => {
                options.IncludeXmlComments(Path.Combine(baseDir,"lsMailServer.xml"));
            });

            var app = builder.Build();

            // 7. Enable Swagger middleware 
            app.UseSwagger();
            app.UseSwaggerUI();

            // 8. Enable static files if you host your web UI in wwwroot
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // 9. Enable Authentication & Authorization middleware (CRITICAL: Must be before MapControllers)
            app.UseAuthentication();
            app.UseAuthorization();

            // 10. Map route endpoints for your controllers
            app.MapControllers();

            await app.RunAsync();
        }
    }
}