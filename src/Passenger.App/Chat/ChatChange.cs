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
}
