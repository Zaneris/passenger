using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Chat;

/// <summary>The agent's plans as files of the open folder, as Cursor keeps its plans: each one the agent asks to build is
/// written to .passenger/plans/2026-11-16_add-a-math-class.plan.md, named by its day and its first heading, to read as a
/// page, edit before building, and keep beside the code.</summary>
public static partial class ChatPlans
{
    /// <summary>The folder of a folder's plans.</summary>
    public static string FolderOf(string root) => Path.Combine(root, ".passenger", "plans");

    /// <summary>A plan's title: its first heading's words, else its first line's, else "Plan"; without a "Plan:" before
    /// them, which the card and the file's name say already.</summary>
    public static string TitleOf(string plan)
    {
        string[] lines = plan.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? heading = lines.FirstOrDefault(line => line.StartsWith('#'));
        string title = PlanWord().Replace((heading ?? lines.FirstOrDefault() ?? string.Empty).TrimStart('#', ' ').Trim(), string.Empty).Trim();
        return title.Length > 0 ? title : "Plan";
    }

    /// <summary>Whether a file is one of the drafts Claude Code writes its plan to as it plans (~/.claude/plans): the
    /// plan the person reads is the folder's copy, so the draft is neither a change to review nor a second plan.</summary>
    public static bool IsAgentDraft(string path) =>
        PathNames.IsUnder(PathNames.Canonical(path), PathNames.Canonical(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plans")));

    [GeneratedRegex(@"^plan\s*[:\-–—]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PlanWord();

    /// <summary>Writes a plan in <paramref name="root"/>'s plans folder, a name of its own beside any there; its path.</summary>
    public static string Save(string root, string plan, DateTime day)
    {
        string folder = FolderOf(root);
        string slug = Slug().Replace(TitleOf(plan).ToLowerInvariant(), "-").Trim('-');
        slug = slug.Length == 0 ? "plan" : slug[..Math.Min(slug.Length, 48)].TrimEnd('-');
        string name = $"{day:yyyy-MM-dd}_{slug}";
        string path = Path.Combine(folder, $"{name}.plan.md");
        for (int again = 2; File.Exists(path); again++)
        {
            path = Path.Combine(folder, $"{name}-{again}.plan.md");
        }
        FileText.Save(path, plan.EndsWith('\n') ? plan : plan + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bom: false);
        return path;
    }

    /// <summary>The plans of <paramref name="root"/>, the newest first.</summary>
    public static IReadOnlyList<string> List(string root)
    {
        string folder = FolderOf(root);
        return Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.plan.md").OrderByDescending(File.GetLastWriteTimeUtc)]
            : [];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Slug();
}
