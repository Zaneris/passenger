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

    /// <summary>The line Claude Code's drafting of its plan shows as: one per reply, however many times it writes it.</summary>
    public const string DraftTitle = "Draft the plan";

    /// <summary>The heading of the to-dos section Passenger keeps at the end of a plan's file.</summary>
    public const string ToDosHeading = "## To-dos";

    /// <summary>A plan's text with the agent's to-dos as a checklist at its end, ticked as the agent finishes each, as
    /// Cursor's plan tracks them: the section Passenger wrote before is replaced, the rest kept as it is.</summary>
    public static string WithToDos(string plan, IReadOnlyList<Acp.PlanEntry> entries)
    {
        string newline = plan.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // The section is its heading and the checklist under it; whatever the person wrote after it stays.
        List<string> lines = [.. plan.Split('\n').Select(line => line.TrimEnd('\r'))];
        int at = lines.FindIndex(line => line == ToDosHeading);
        if (at >= 0)
        {
            int end = at + 1;
            while (end < lines.Count && (lines[end].Trim().Length == 0 || lines[end].StartsWith("- [", StringComparison.Ordinal)))
            {
                end++;
            }
            lines.RemoveRange(at, end - at);
        }
        StringBuilder text = new(string.Join(newline, lines).TrimEnd());
        if (entries.Count > 0)
        {
            text.Append(newline).Append(newline).Append(ToDosHeading).Append(newline).Append(newline);
            foreach (Acp.PlanEntry entry in entries)
            {
                string mark = entry.Status == "completed" ? "x" : " ";
                text.Append("- [").Append(mark).Append("] ").Append(entry.Content.ReplaceLineEndings(" ")).Append(entry.Status == "in_progress" ? " (in progress)" : string.Empty).Append(newline);
            }
            return text.ToString();
        }
        return text.Append(newline).ToString();
    }
    /// <summary>What a plan's card says of it, as Cursor's does: its first paragraph of words (no heading, list or code),
    /// its Markdown marks left out, cut short past a few lines; empty where it has none.</summary>
    public static string SummaryOf(string plan)
    {
        StringBuilder paragraph = new();
        bool code = false;
        foreach (string raw in plan.Split('\n'))
        {
            string line = raw.Trim();
            code ^= line.StartsWith("```", StringComparison.Ordinal);
            bool words = !code && line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("```", StringComparison.Ordinal)
                && !line.StartsWith('|') && !line.StartsWith('>') && !ListMark().IsMatch(line);
            if (words)
            {
                paragraph.Append(paragraph.Length > 0 ? " " : string.Empty).Append(line);
            }
            else if (paragraph.Length > 0)
            {
                break;
            }
        }
        string said = paragraph.ToString().Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        return said.Length <= 280 ? said : said[..said.LastIndexOf(' ', 279)] + "…";
    }

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s")]
    private static partial Regex ListMark();

    /// <summary>Whether a file is one of the drafts Claude Code writes its plan to as it plans (~/.claude/plans): the
    /// plan the person reads is the folder's copy, so the draft is neither a change to review nor a second plan.</summary>
    public static bool IsAgentDraft(string path) =>
        PathNames.IsUnder(PathNames.Canonical(path), PathNames.Canonical(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plans")));

    /// <summary>Whether a file is one of <paramref name="root"/>'s plans, which reads as a page alone, its source behind
    /// the preview's toggle.</summary>
    public static bool IsPlan(string path, string? root) =>
        root is not null && path.EndsWith(".plan.md", StringComparison.OrdinalIgnoreCase)
        && PathNames.IsUnder(PathNames.Canonical(path), PathNames.Canonical(FolderOf(root)));

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
