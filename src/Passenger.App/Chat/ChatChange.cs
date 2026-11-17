namespace Passenger.App.Chat;

/// <summary>A change an agent proposes to one file: its text before (as the agent sent it, or as the file holds it when
/// the agent sent none) and after, and whether the file is new.</summary>
public sealed record ChatChange(string Path, string Before, string After, bool IsNew)
{
    /// <summary>The change as an agent proposes it, the file read for its text before where the agent sent none.</summary>
    public static ChatChange Of(string path, string? oldText, string newText)
    {
        if (oldText is not null)
        {
            return new ChatChange(path, oldText, newText, IsNew: false);
        }
        bool exists = File.Exists(path);
        return new ChatChange(path, exists ? File.ReadAllText(path) : string.Empty, newText, IsNew: !exists);
    }

    /// <summary>The file's whole text as it would be with the change made, from its text now: the whole of it where the
    /// change is the file's, else its first stretch the change replaces, in the file's own line ends; null where the
    /// file no longer holds what the change replaces.</summary>
    public string? Applied(string text)
    {
        if (IsNew || Normal(text) == Normal(Before))
        {
            return After;
        }
        if (Before.Length == 0)
        {
            return null;
        }
        foreach ((string from, string to) in new[] { (Before, After), (Before.ReplaceLineEndings("\r\n"), After.ReplaceLineEndings("\r\n")) })
        {
            int at = text.IndexOf(from, StringComparison.Ordinal);
            if (at >= 0)
            {
                return string.Concat(text.AsSpan(0, at), to, text.AsSpan(at + from.Length));
            }
        }
        return null;

        static string Normal(string text) => text.ReplaceLineEndings("\n");
    }
}
