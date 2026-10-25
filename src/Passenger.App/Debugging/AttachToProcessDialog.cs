using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>A row of Attach to Process: a .NET program, or the heading over a group of them.</summary>
public sealed record AttachRow(DotnetProcess? Process, string? Heading = null);

/// <summary>What the program chosen in Attach to Process's dialog is for: the debugger (Attach to Process), or the
/// profiler (Attach Profiler's choice among the containers' programs).</summary>
public enum AttachPurpose
{
    Debugger,
    Profiler,
}

/// <summary>Attach to Process: the .NET programs running on this machine that the debugger can attach to, each by its
/// executable, process id, user and command line, those attached to before in this window first under Recently
/// attached, then those in the engine's running containers under each container (what to know first after the command
/// line; one no debugger can attach to says why, and Attach stays off for it). The search box narrows them by any of
/// those as it is typed, the arrow keys choose among the rows left, and Enter, a double-click or Attach with .NET
/// Debugger closes it with the program chosen (null when cancelled). Refresh lists them again, for a program started
/// since it opened. For the profiler it is titled and its button named Attach Profiler
/// (<see cref="AttachPurpose"/>).</summary>
public sealed class AttachToProcessDialog : Window
{
    private const string Columns = "220,90,110,*";

    private readonly Func<Task<IReadOnlyList<DotnetProcess>>> list;
    private readonly Func<DotnetProcess, bool> recent;
    private readonly ListBox rows;
    private readonly TextBlock empty;
    private readonly Button attach;
    private IReadOnlyList<DotnetProcess>? listed;

    public AttachToProcessDialog(Func<Task<IReadOnlyList<DotnetProcess>>> list, Func<DotnetProcess, bool> recent, string search = "",
        AttachPurpose purpose = AttachPurpose.Debugger)
    {
        this.list = list;
        this.recent = recent;
        Purpose = purpose;
        Title = purpose == AttachPurpose.Profiler ? "Attach Profiler" : "Attach to Process";
        Width = 900;
        Height = 520;
        MinWidth = 560;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Search = new TextBox { Classes = { "search" }, Text = search, PlaceholderText = "Search by name, process ID, user or command line" };
        Search.TextChanged += (_, _) => ShowRows();
        Search.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Button refresh = ToolButtons.Of(IconKeys.Rerun, "Refresh", () => IdeErrors.Observe(RefreshAsync()));
        refresh.Margin = new Thickness(6, 0, 0, 0);
        refresh.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel top = new() { [DockPanel.DockProperty] = Dock.Top, Margin = new Thickness(0, 0, 0, 10), Children = { refresh, Search } };

        // Inset as the list's rows are, by its padding and theirs, so each title stands over its column.
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(10, 0) };
        string[] titles = ["Executable", "Process ID", "User", "Command Line"];
        for (int column = 0; column < titles.Length; column++)
        {
            header.Children.Add(new TextBlock { [Grid.ColumnProperty] = column, Text = titles[column], Classes = { "muted" }, Margin = new Thickness(4, 5, 10, 5) });
        }
        Border headerFrame = new() { [DockPanel.DockProperty] = Dock.Top, Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerFrame.Bind(Border.BorderBrushProperty, headerFrame.GetResourceObservable(TokenKeys.Separator));

        rows = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<AttachRow>((row, _) => row is null ? new Panel() : Row(row), supportsRecycling: false),
        };
        rows.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem { DataContext: AttachRow { Process: null } } heading)
            {
                heading.IsEnabled = false;
            }
        };
        rows.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        rows.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            {
                Attach();
            }
        };
        rows.SelectionChanged += (_, _) => attach!.IsEnabled = Chosen is { WhyNot: null };
        empty = new TextBlock { Classes = { "emptyText" }, Text = "Looking for .NET processes…" };
        Border table = new()
        {
            Classes = { "framed" },
            ClipToBounds = true,
            Child = new DockPanel { Children = { headerFrame, new Panel { Children = { rows, empty } } } },
        };

        attach = new Button { Content = purpose == AttachPurpose.Profiler ? "Attach Profiler" : "Attach with .NET Debugger", Classes = { "primary" }, IsEnabled = false };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        attach.Click += (_, _) => Attach();
        cancel.Click += (_, _) => Close(null);
        Content = DialogFooter.Page(new DockPanel { Margin = new Thickness(16), Children = { top, table } }, DialogFooter.Bar(DialogFooter.Of(attach, cancel)));
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Search.Focus();
            Search.CaretIndex = Search.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
        Listed = RefreshAsync();
    }

    /// <summary>The search box, which narrows the rows as it is typed.</summary>
    public TextBox Search { get; }

    /// <summary>What the program chosen is attached to.</summary>
    public AttachPurpose Purpose { get; }

    /// <summary>The programs listed now, in their order, what the search leaves.</summary>
    public IReadOnlyList<DotnetProcess> Shown => [.. Rows.Select(r => r.Process).OfType<DotnetProcess>()];

    /// <summary>Every row as listed, the group headings among them.</summary>
    public IReadOnlyList<AttachRow> Rows => rows.ItemsSource as IReadOnlyList<AttachRow> ?? [];

    /// <summary>The program chosen, which Attach attaches to.</summary>
    public DotnetProcess? Chosen => (rows.SelectedItem as AttachRow)?.Process;

    /// <summary>The last listing, done once the programs are shown.</summary>
    public Task Listed { get; private set; }

    /// <summary>What the list says when it shows no program: still looking, none running, or none the search finds.</summary>
    public string? Placeholder => empty.IsVisible ? empty.Text : null;

    /// <summary>Chooses a program's row, as a click does: this machine's by its process id, or a container's by its name and
    /// the program's command line there.</summary>
    public void Choose(int processId) => rows.SelectedItem = Rows.FirstOrDefault(r => r.Process is { Container: null } p && p.Id == processId);

    /// <summary>Chooses a container's program, by the container and what its command line holds.</summary>
    public void Choose(string container, string commandLine) =>
        rows.SelectedItem = Rows.FirstOrDefault(r => r.Process is { } p && p.Container == container && p.CommandLine.Contains(commandLine, StringComparison.Ordinal));

    /// <summary>Closes with the program chosen, as Attach with .NET Debugger does; one no debugger can attach to stays.</summary>
    public void Attach()
    {
        if (Chosen is { WhyNot: null } chosen)
        {
            Close(chosen);
        }
    }

    /// <summary>Lists the programs again, the one chosen kept while it runs.</summary>
    public Task RefreshAsync() => Listed = Refresh();

    private async Task Refresh()
    {
        listed = await list();
        ShowRows();
    }

    /// <summary>The rows the search leaves: those attached to before under Recently attached, then the others, with no
    /// headings when none was.</summary>
    private void ShowRows()
    {
        if (listed is null)
        {
            return;
        }
        string typed = Search.Text?.Trim() ?? string.Empty;
        List<DotnetProcess> found = [.. listed.Where(p => p.Matches(typed))];
        List<DotnetProcess> before = [.. found.Where(recent)];
        List<DotnetProcess> local = [.. found.Except(before).Where(p => p.Container is null)];
        List<IGrouping<string, DotnetProcess>> containers = [.. found.Except(before).Where(p => p.Container is not null).GroupBy(p => p.Container!)];
        List<AttachRow> shown = [];
        if (before.Count > 0)
        {
            shown.Add(new AttachRow(null, "Recently attached"));
            shown.AddRange(before.Select(p => new AttachRow(p)));
        }
        if (local.Count > 0)
        {
            if (before.Count > 0 || containers.Count > 0)
            {
                shown.Add(new AttachRow(null, before.Count > 0 ? "Other processes" : "This machine"));
            }
            shown.AddRange(local.Select(p => new AttachRow(p)));
        }
        foreach (IGrouping<string, DotnetProcess> container in containers)
        {
            // Its programs' ids there are known once Passenger's tools are in it, which attaching copies in: said before it does.
            shown.Add(new AttachRow(null, container.Any(p => p.Id == 0) ? $"Container {container.Key}: {Containers.ContainerProcesses.CopiesTools}" : $"Container {container.Key}"));
            shown.AddRange(container.Select(p => new AttachRow(p)));
        }
        DotnetProcess? kept = Chosen;
        rows.ItemsSource = shown;
        rows.SelectedItem = shown.FirstOrDefault(r => r.Process is { } p && kept is not null && p.Id == kept.Id && p.Container == kept.Container && p.CommandLine == kept.CommandLine)
            ?? shown.FirstOrDefault(r => r.Process is { WhyNot: null });
        attach.IsEnabled = Chosen is { WhyNot: null };
        empty.Text = listed.Count > 0 ? $"No .NET process matches '{typed}'"
            : Purpose == AttachPurpose.Profiler ? "Nothing of the folder's runs, and no .NET program runs in a container, to profile"
            : "No .NET processes to attach to";
        empty.IsVisible = found.Count == 0;
        if (rows.SelectedItem is { } selected)
        {
            rows.ScrollIntoView(selected);
        }
    }

    /// <summary>The arrows move among the programs from the search box as in the list, past the headings; Enter attaches.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                Attach();
                e.Handled = true;
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when ListKeys.Move(rows, e, i => Rows[i].Process is not null):
                e.Handled = true;
                break;
        }
    }

    private static Control Row(AttachRow row)
    {
        if (row.Process is not { } process)
        {
            return new TextBlock { Text = row.Heading, Classes = { "muted" }, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 6, 0, 2) };
        }
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions(Columns), Height = ListRow.Height };
        grid.Children.Add(Cell(process.Name, 0, muted: false));
        grid.Children.Add(Cell(process.Id == 0 ? string.Empty : process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, muted: false));
        grid.Children.Add(Cell(process.User, 2, muted: true));
        // Why it cannot be attached to, or what to know first (a Release build, emulation), after its command line.
        string? said = process.WhyNot ?? process.Note;
        TextBlock line = Cell(said is null ? process.CommandLine : $"{process.CommandLine}  ({said})", 3, muted: true);
        ToolTip.SetTip(line, said is null ? process.CommandLine : $"{process.CommandLine}\n{said}");
        grid.Children.Add(line);
        return grid;
    }

    private static TextBlock Cell(string text, int column, bool muted)
    {
        TextBlock cell = new()
        {
            [Grid.ColumnProperty] = column,
            Text = text,
            Margin = new Thickness(4, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (muted)
        {
            cell.Classes.Add("muted");
        }
        return cell;
    }
}
