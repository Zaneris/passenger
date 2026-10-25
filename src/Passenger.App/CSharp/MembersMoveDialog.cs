using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>The Pull Members Up and Push Members Down dialogs: the base type to pull up to, or the inheritors to push
/// down to, and the class's members in a table of which go and which are made abstract where they stay (in the base
/// type pulled up to, or the class pushed down from), none of them chosen at first.</summary>
public sealed class MembersMoveDialog : RefactoringDialog
{
    private readonly bool up;
    private readonly Hierarchy hierarchy;
    private readonly Func<string, IReadOnlyList<string>, IReadOnlyList<string>, Task<string?>> make;
    private readonly List<(Member Member, CheckBox Moved, CheckBox Abstract)> members = [];
    private readonly List<(Member Type, CheckBox Box)> inheritors = [];

    private MembersMoveDialog(Hierarchy hierarchy, bool up, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, Task<string?>> make)
        : base(up ? "Pull Members Up" : "Push Members Down", up ? "Select base type and members" : "Choose inheritors",
            up
                ? $"Choose the base type of '{hierarchy.Type}' and the members that you want to move to base type"
                : $"Choose inheritors of '{hierarchy.Type}' and members that you want to push to derived classes",
            560)
    {
        this.up = up;
        this.hierarchy = hierarchy;
        this.make = make;
        BaseBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (Member type in hierarchy.Bases)
        {
            BaseBox.Items.Add(type.Text);
        }
        BaseBox.SelectedIndex = hierarchy.Bases.Count > 0 ? 0 : -1;
        BaseBox.SelectionChanged += (_, _) => Changed();

        StackPanel options = new() { Spacing = 10 };
        if (up)
        {
            options.Children.Add(Row("Base type:", BaseBox));
        }
        else
        {
            StackPanel list = new() { Spacing = 2 };
            foreach (Member type in hierarchy.Derived)
            {
                CheckBox box = new() { Content = type.Text, IsChecked = true };
                box.IsCheckedChanged += (_, _) => Changed();
                inheritors.Add((type, box));
                list.Children.Add(box);
            }
            options.Children.Add(new TextBlock { Text = "Inheritors:" });
            options.Children.Add(new Border { Padding = new Thickness(8, 0), Child = list });
        }

        Grid header = Table();
        header.Children.Add(Muted("Members", 0));
        header.Children.Add(Muted("Make abstract", 1));
        Border headerFrame = new() { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerFrame.Bind(Border.BorderBrushProperty, headerFrame.GetResourceObservable(TokenKeys.Separator));
        DockPanel.SetDock(headerFrame, Dock.Top);
        StackPanel rows = new() { Margin = new Thickness(0, 4) };
        foreach (Member member in hierarchy.Members)
        {
            CheckBox moved = new() { Content = member.Text, Margin = new Thickness(8, 1, 0, 1) };
            CheckBox made = new() { Margin = new Thickness(8, 1, 0, 1) };
            moved.IsCheckedChanged += (_, _) => Changed();
            made.IsCheckedChanged += (_, _) => Changed();
            Grid.SetColumn(made, 1);
            Grid row = Table();
            row.Children.Add(moved);
            row.Children.Add(made);
            members.Add((member, moved, made));
            rows.Children.Add(row);
        }
        Border tableFrame = new()
        {
            Classes = { "framed" },
            ClipToBounds = true,
            Child = new DockPanel { Children = { headerFrame, new ScrollViewer { Content = rows, MaxHeight = 260, MinHeight = 100 } } },
        };
        options.Children.Add(new TextBlock { Text = up ? "Members to pull up:" : "Members to push down:" });
        options.Children.Add(tableFrame);
        Options = options;
        Changed();
        FocusOnOpen(up ? BaseBox : members.Count > 0 ? members[0].Moved : NextButton);
    }

    public static MembersMoveDialog PullUp(Hierarchy hierarchy, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, Task<string?>> make) => new(hierarchy, up: true, make);

    public static MembersMoveDialog PushDown(Hierarchy hierarchy, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, Task<string?>> make) => new(hierarchy, up: false, make);

    /// <summary>The base type pulled up to, for Pull Members Up.</summary>
    public ComboBox BaseBox { get; }

    /// <summary>Each member offered, by how it reads, with its check boxes: whether it goes, and whether it is made abstract.</summary>
    public IReadOnlyList<(string Text, CheckBox Moved, CheckBox Abstract)> Members => [.. members.Select(m => (m.Member.Text, m.Moved, m.Abstract))];

    /// <summary>The classes pushed down to, for Push Members Down.</summary>
    public IReadOnlyList<(string Text, CheckBox Box)> Inheritors => [.. inheritors.Select(i => (i.Type.Text, i.Box))];

    protected override string? Check()
    {
        bool intoInterface = up && BaseBox.SelectedIndex >= 0 && hierarchy.Bases[BaseBox.SelectedIndex].Kind == "interface";
        foreach ((Member member, CheckBox moved, CheckBox made) in members)
        {
            // An interface declares what is pulled up; a field has no abstract form.
            made.IsEnabled = moved.IsChecked == true && !intoInterface && member.Kind != "field";
            if (!made.IsEnabled && made.IsChecked == true)
            {
                made.IsChecked = false;
            }
        }
        if (!up && !inheritors.Any(i => i.Box.IsChecked == true))
        {
            return "Choose the inheritors to push members down to";
        }
        return members.Any(m => m.Moved.IsChecked == true) ? null : up ? "Select the members to pull up" : "Select the members to push down";
    }

    protected override Task<string?> MakeAsync()
    {
        string target = up
            ? hierarchy.Bases[Math.Max(0, BaseBox.SelectedIndex)].Id
            : string.Join("\n", inheritors.Where(i => i.Box.IsChecked == true).Select(i => i.Type.Id));
        return make(target, [.. members.Where(m => m.Moved.IsChecked == true).Select(m => m.Member.Id)],
            [.. members.Where(m => m.Moved.IsChecked == true && m.Abstract.IsChecked == true).Select(m => m.Member.Id)]);
    }

    private static Grid Table() => new() { ColumnDefinitions = new ColumnDefinitions("*,120") };

    private static TextBlock Muted(string text, int column)
    {
        TextBlock block = MutedText.Of(text);
        block.Margin = new Thickness(8, 5);
        Grid.SetColumn(block, column);
        return block;
    }
}
