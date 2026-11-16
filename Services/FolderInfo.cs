using System.Globalization;
using System.Xml.Linq;

namespace lsMailServer.Services 
{
    /// <summary>
    /// Represents an mailbox folder, including its path/name, UIDVALIDITY, 
    /// schema version, and the collection of messages contained within it.
    /// </summary>
    public class FolderInfo
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FolderInfo"/> class with the specified folder details.
        /// </summary>
        /// <param name="name">The name or hierarchical path of the folder (e.g., INBOX, Sent).</param>
        /// <param name="uidValidity">The unique identifier validity value (UIDVALIDITY) used to ensure message UIDs remain consistent across client sessions.</param>
        /// <param name="version">The schema or protocol version associated with the folder record.</param>
        public FolderInfo(string name, long uidValidity, long version)
        {
            Name = name;
            UidValidity = uidValidity;
            Version = version;
            Messages = new List<MessageInfo>();
        }

        /// <summary>
        /// Gets the name or hierarchical path of the folder.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the IMAP UIDVALIDITY value for the folder.
        /// </summary>
        public long UidValidity { get; }

        /// <summary>
        /// Gets the schema or protocol version associated with the folder record.
        /// </summary>
        public long Version { get; }

        /// <summary>
        /// Gets the list of message metadata objects contained within this folder.
        /// </summary>
        public List<MessageInfo> Messages { get; }
    }
}
