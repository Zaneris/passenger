using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.App.Settings;

/// <summary>The folders the person answered the trust question for, one row each in the order of their paths: the folder,
/// its Trusted box, which trusts it or stops trusting it, and Forget, which drops the answer so the folder is asked of
/// again the next time it opens. Where no folder was answered for, it says so in place of rows.</summary>
public sealed class TrustedFoldersTable : EditableTable
{
    /// <summary>One folder's row: its path, its Trusted box and its Forget button.</summary>
    public sealed record Row(string Folder, CheckBox Trusted, Button Forget, Border View);

    /// <summary>What it says with no folder answered for.</summary>
    public const string NoneAnswered = "None yet: a folder is asked of as it first opens, and your answer is kept here";

    private readonly List<Row> rows = [];

    /// <summary>Its columns: the folder, its Trusted box, and its Forget.</summary>
    public TrustedFoldersTable()
        : base("*,72,36", ["Folder", "Trusted", string.Empty])
    {
    }

    public IReadOnlyList<Row> Rows => rows;

    /// <summary>The row of a folder, or null where it has none.</summary>
    public Row? Of(string folder) => rows.FirstOrDefault(r => PathNames.Same(r.Folder, folder));

    /// <summary>The rows of the answers kept, without raising <see cref="EditableTable.Changed"/>.</summary>
    public void Set(TrustState answers) => Setting(() =>
    {
        rows.Clear();
        Table.Children.Clear();
        foreach ((string folder, bool trusted) in answers.Trusted.Select(f => (f, true)).Concat(answers.Untrusted.Select(f => (f, false)))
            .OrderBy(a => a.f, PathNames.Comparer))
        {
            Add(folder, trusted);
        }
        SayInstead(rows.Count == 0 ? NoneAnswered : null);
    });

    /// <summary>The answers as the rows have them: each folder trusted or opened without trusting, those forgotten gone.</summary>
    public TrustState Answers() => new()
    {
        Trusted = [.. rows.Where(r => r.Trusted.IsChecked == true).Select(r => r.Folder)],
        Untrusted = [.. rows.Where(r => r.Trusted.IsChecked != true).Select(r => r.Folder)],
    };

    private void Add(string folder, bool trusted)
    {
        TextBlock name = new() { Text = folder, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0), TextTrimming = TextTrimming.PathSegmentEllipsis };
        ToolTip.SetTip(name, folder);
        TrimmedTip.Attach(name);
        CheckBox box = CheckRows.Box();
        box.IsChecked = trusted;
        Avalonia.Automation.AutomationProperties.SetLabeledBy(box, name);
        box.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(box, "Checked, Passenger runs the folder's code; cleared, it runs none of it, and the folder opens without asking");
        Button forget = ToolButtons.Glyph(IconKeys.Close, "Forget: the folder is asked of again the next time it opens");
        forget.VerticalAlignment = VerticalAlignment.Center;
        Border view = Ruled(name, box, forget);
        Row row = new(folder, box, forget, view);
        rows.Add(row);
        Table.Children.Add(view);
        box.IsCheckedChanged += (_, _) => Tell();
        forget.Click += (_, _) =>
        {
            rows.Remove(row);
            Table.Children.Remove(view);
            SayInstead(rows.Count == 0 ? NoneAnswered : null);
            Tell();
        };
    }
}
