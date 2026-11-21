namespace lsMailServer.Services 
{
    /// <summary>
    /// Represents Domain entry.
    /// </summary>
    /// <param name="Name">The domain name (e.g., example.com).</param>
    /// <param name="Description">An optional description or administrative notes for the domain.</param>
    /// <param name="IsEnabled">A value indicating whether the domain is currently active and processing mail.</param>
    public record DomainInfo(string Name, string? Description, bool IsEnabled);
}
