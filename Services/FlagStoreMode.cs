namespace lsMailServer.Services 
{
    /// <summary>
    /// Specifies the operation mode when updating or modifying message flags in a mailbox store.
    /// </summary>
    public enum FlagStoreMode
    {
        /// <summary>
        /// Replaces the existing message flags entirely with the specified flag set.
        /// </summary>
        Replace = 0, 

        /// <summary>
        /// Adds the specified flags to the message's existing set of flags.
        /// </summary>
        Add = 1,     

        /// <summary>
        /// Removes the specified flags from the message's existing set of flags.
        /// </summary>
        Remove = 2
    }
}
