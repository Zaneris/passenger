using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>Extract Interface: the interface's name and the file it goes in, which follows the name until it is typed
/// in, and the type's members to give it, each checked to go, all of them at first.</summary>
public sealed class ExtractInterfaceDialog : RefactoringDialog
{
    private readonly Func<string, string, IReadOnlyList<string>, Task<string?>> make;
    private readonly List<(Member Member, CheckBox Box)> members = [];
    private bool fileTyped;
    private bool naming;

    public ExtractInterfaceDialog(InterfaceSource source, Func<string, string, IReadOnlyList<string>, Task<string?>> make)
        : base("Extract Interface", "Select base type and members",
            $"Specify the name of the interface to extract from '{source.Type}' and the members that you want to move to base type")
    {
        this.make = make;
        NameBox = Field(source.Name);
        FileBox = Field(source.FileName);
        NameBox.TextChanged += (_, _) =>
        {
            if (!fileTyped)
            {
                naming = true;
                FileBox.Text = $"{NameBox.Text?.Trim()}.cs";
                naming = false;
            }
            Changed();
        };
        FileBox.TextChanged += (_, _) =>
        {
            fileTyped |= !naming;
            Changed();
        };
        StackPanel list = new() { Margin = new Thickness(Gaps.Normal, Gaps.Tight), Spacing = Gaps.Hair };
        foreach (Member member in source.Members)
        {
            CheckBox box = new() { Content = member.Text, IsChecked = true };
            box.IsCheckedChanged += (_, _) => Changed();
            members.Add((member, box));
            list.Children.Add(box);
        }
        SelectAllButton = new Button { Content = "Select All" };
        SelectNoneButton = new Button { Content = "Select None" };
        SelectAllButton.Click += (_, _) => Check(true);
        SelectNoneButton.Click += (_, _) => Check(false);

        Border listFrame = new()
        {
            Classes = { StyleClasses.Framed },
            ClipToBounds = true,
            Child = new DockPanel { Children = { ColumnTitles.Of("*", ["Members"]), new ScrollViewer { Content = list, MaxHeight = 240, MinHeight = 100 } } },
        };

        Options = new StackPanel
        {
            Spacing = FormRows.Between,
            Children =
            {
                Row("Interface name", NameBox),
                Row("File name", FileBox),
                Row("Members to form interface", listFrame, top: true),
                Row(null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Normal, Children = { SelectAllButton, SelectNoneButton } }),
            },
        };
        Changed();
        FocusOnOpen(NameBox);
    }

    public TextBox NameBox { get; }

    public TextBox FileBox { get; }

    public Button SelectAllButton { get; }

    public Button SelectNoneButton { get; }

    /// <summary>Each member offered, by how it reads, with its check box.</summary>
    public IReadOnlyList<(string Text, CheckBox Box)> Members => [.. members.Select(m => (m.Member.Text, m.Box))];

    protected override string? Check() => RefactoringChecks.WhyNotInterfaceName(NameBox.Text?.Trim() ?? string.Empty)
        ?? RefactoringChecks.WhyNotFileName(FileBox.Text?.Trim() ?? string.Empty)
        ?? RefactoringChecks.WhyNotInterfaceMembers(members.Count(m => m.Box.IsChecked == true));

    protected override Task<string?> MakeAsync() => make(NameBox.Text?.Trim() ?? string.Empty, FileBox.Text?.Trim() ?? string.Empty,
        [.. members.Where(m => m.Box.IsChecked == true).Select(m => m.Member.Id)]);

    private void Check(bool on)
    {
        foreach ((_, CheckBox box) in members)
        {
            box.IsChecked = on;
        }
    }
}
