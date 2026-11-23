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

    /// <summary>The editor git opens an interactive rebase's list of steps in, given the list's file.</summary>
    public const string SequenceEditor = "GIT_SEQUENCE_EDITOR";

    /// <summary>The name, email and time (seconds since 1970 and the zone, as git writes them raw) a commit git makes is
    /// authored by, over the configuration's.</summary>
    public const string AuthorName = "GIT_AUTHOR_NAME";

    public const string AuthorEmail = "GIT_AUTHOR_EMAIL";

    public const string AuthorDate = "GIT_AUTHOR_DATE";

    /// <summary>"0" keeps git from asking for a login on a terminal.</summary>
    public const string TerminalPrompt = "GIT_TERMINAL_PROMPT";

    /// <summary>"0" keeps a git that only reads (a status, a diff) from taking the index's lock to refresh it.</summary>
    public const string OptionalLocks = "GIT_OPTIONAL_LOCKS";

    /// <summary>The program git asks for a login or a passphrase.</summary>
    public const string AskPass = "GIT_ASKPASS";

    /// <summary>The repository's git folder, which a hook is given.</summary>
    public const string Dir = "GIT_DIR";

    /// <summary>The repository's working tree, which a hook may be given, over what core.worktree says.</summary>
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

    /// <summary>How many settings git takes from its environment (<see cref="ConfigKey"/>, <see cref="ConfigValue"/>), over
    /// every configuration file's.</summary>
    public const string ConfigCount = "GIT_CONFIG_COUNT";

    /// <summary>The settings `git -c` gives, which every git it starts inherits, read after <see cref="ConfigCount"/>'s and
    /// so over them.</summary>
    public const string ConfigParameters = "GIT_CONFIG_PARAMETERS";

    /// <summary>The one file `git config` reads and writes in place of every scope's, where it is given no --file; the
    /// other commands read every scope's as ever.</summary>
    public const string Config = "GIT_CONFIG";

    /// <summary>The protocols git may reach a remote by, whatever its configuration says; empty, none.</summary>
    public const string AllowProtocol = "GIT_ALLOW_PROTOCOL";

    /// <summary>"1" has git take every pathspec as written, a magic one (:/) too.</summary>
    public const string LiteralPathspecs = "GIT_LITERAL_PATHSPECS";

    /// <summary>The key of the environment's setting at <paramref name="at"/>, from 0, kept whole as written.</summary>
    public static string ConfigKey(int at) => $"GIT_CONFIG_KEY_{at}";

    /// <summary>The value of the environment's setting at <paramref name="at"/>, from 0.</summary>
    public static string ConfigValue(int at) => $"GIT_CONFIG_VALUE_{at}";
}
