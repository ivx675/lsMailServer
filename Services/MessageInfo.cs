using LumiSoft.Net.IMAP;

namespace lsMailServer.Services 
{
    /// <summary>
    /// Represents metadata, flags, and structural information for an email message within the mail server.
    /// </summary>
    /// <param name="Version">The schema or protocol version associated with the message record.</param>
    /// <param name="MessageUid">The unique message identifier (UID) within its mailbox folder.</param>
    /// <param name="MessageSize">The total size of the message in bytes.</param>
    /// <param name="MessageFlags">An array of IMAP system and user flags assigned to the message (e.g., \Seen, \Flagged).</param>
    /// <param name="MessageInternalDate">The internal timestamp assigned to the message by the server.</param>
    /// <param name="MessageDate">The original date and time the message was sent, or <c>null</c> if not specified.</param>
    /// <param name="MessageSender">The sender's address or display name from the From header, or <c>null</c>.</param>
    /// <param name="MessageTo">The primary recipient addresses from the To header, or <c>null</c>.</param>
    /// <param name="MessageCc">The carbon copy recipient addresses from the Cc header, or <c>null</c>.</param>
    /// <param name="MessageBcc">The blind carbon copy recipient addresses from the Bcc header, or <c>null</c>.</param>
    /// <param name="MessageSubject">The subject line of the message, or <c>null</c>.</param>
    /// <param name="Envelope">The parsed IMAP envelope structure containing addressing and routing metadata, or <c>null</c>.</param>
    /// <param name="BodyStructure">The parsed MIME body structure detailing the parts and content types, or <c>null</c>.</param>
    public readonly record struct MessageInfo(
            long Version,
            long MessageUid,
            long MessageSize,
            string[] MessageFlags,
            DateTime MessageInternalDate,
            DateTime? MessageDate = null,
            string? MessageSender = null,
            string? MessageTo = null,
            string? MessageCc = null,
            string? MessageBcc = null,
            string? MessageSubject = null,
            IMAP_t_Envelope? Envelope = null,
            IMAP_t_BodyStructure? BodyStructure = null
        );
}
