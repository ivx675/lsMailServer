namespace lsMailServer.Services 
{
    /// <summary>
    /// Represents Domain entry.
    /// </summary>
    /// <param name="Name">The domain name (e.g., example.com).</param>
    /// <param name="Description">An optional description or administrative notes for the domain.</param>
    /// <param name="IsEnabled">A value indicating whether the domain is currently active and processing mail.</param>
    /// <param name="IsDkimEnabled">A value indicating whether DKIM signing is enabled for outbound messages on this domain.</param>
    /// <param name="DkimSelector">The DKIM selector configured for this domain, or <see langword="null"/> when not set.</param>
    /// <param name="DkimPublicKey">The base64-encoded SubjectPublicKeyInfo DKIM public key, or <see langword="null"/> when no key has been generated.</param>
    /// <param name="DkimPrivateKey">The base64-encoded SubjectPrivateKeyInfo DKIM private key, or <see langword="null"/> when no key has been generated.</param>
    public record DomainInfo(
        string Name,
        string? Description,
        bool IsEnabled,
        bool IsDkimEnabled,
        string? DkimSelector,
        string? DkimPublicKey,
        string? DkimPrivateKey);
}
