namespace Passenger.Core;

/// <summary>Git's environment variables that Passenger sets for the gits it runs, or takes away from the programs it
/// starts, each named once.</summary>
[IdentifierHome]
public static class GitVariables
{
    /// <summary>The index git reads and writes in place of the repository's own.</summary>
    public const string IndexFile = "GIT_INDEX_FILE";

    /// <summary>The editor git opens for a commit's message; "true" keeps the message it has.</summary>
    public const string Editor = "GIT_EDITOR";

    /// <summary>"0" keeps git from asking for a login on a terminal.</summary>
    public const string TerminalPrompt = "GIT_TERMINAL_PROMPT";

    /// <summary>"0" keeps a git that only reads (a status, a diff) from taking the index's lock to refresh it.</summary>
    public const string OptionalLocks = "GIT_OPTIONAL_LOCKS";

    /// <summary>The program git asks for a login or a passphrase.</summary>
    public const string AskPass = "GIT_ASKPASS";

    /// <summary>The repository's git folder, which a hook is given.</summary>
    public const string Dir = "GIT_DIR";

    /// <summary>The repository's working tree, which a hook may be given.</summary>
    public const string WorkTree = "GIT_WORK_TREE";

    /// <summary>The folder of the repository's objects.</summary>
    public const string ObjectDirectory = "GIT_OBJECT_DIRECTORY";

    /// <summary>Other repositories' object folders git reads objects from.</summary>
    public const string AlternateObjectDirectories = "GIT_ALTERNATE_OBJECT_DIRECTORIES";

    /// <summary>The git folder a linked working tree shares with its main one.</summary>
    public const string CommonDir = "GIT_COMMON_DIR";

    /// <summary>Where in the working tree the command was run, which an alias or hook is given.</summary>
    public const string Prefix = "GIT_PREFIX";

    /// <summary>The namespace git's refs are read and written in.</summary>
    public const string Namespace = "GIT_NAMESPACE";

    /// <summary>The folder a push's objects wait in while its pre-receive hook runs.</summary>
    public const string QuarantinePath = "GIT_QUARANTINE_PATH";
}
