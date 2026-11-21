using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>The Step Library window: every step definition the folder's Reqnroll and SpecFlow projects see, under the kind of
/// step it binds, its phrase with its slots, the tags its scopes name and how many feature steps use it (a link to them;
/// one no step uses said unused), then the hooks, when each runs and its tags; narrowed by the filter's words and @tags, or
/// to the unused alone. Enter or a row's Insert writes its phrase into the open feature at the caret; a double-click or F4
/// goes to its method.</summary>
public sealed class StepLibraryView : DockPanel
{
    /// <summary>A kind's heading over its rows, with how many are listed under it.</summary>
    public sealed record Heading(string Title, int Count);

    private readonly ListBox list;
    private readonly EmptyState empty;
    private readonly TextBox filter = SearchBox.Finding("Filter steps");
    private readonly FilterMenu filters;
    private IReadOnlyList<LibraryStep> steps = [];
    private IReadOnlyList<StepHook> hooks = [];
    private string? nothing;
    private bool unusedOnly;

    /// <param name="item">A menu item of one of the window's actions, greyed with why where it can't run.</param>
    public StepLibraryView(Func<string, MenuItem> item)
    {
        list = new ListBox { Classes = { StyleClasses.Tree } };
        filter.Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0);
        filter.TextChanged += (_, _) => List();
        filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.ItemCount > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                ListKeys.Hold(list, chooseFirst: true);
                e.Handled = true;
            }
        };
        filters = new FilterMenu("Filter Step Definitions", FilterItems);
        filters.Inside(filter);
        Children.Add(ToolBars.Top(filter));

        ReusedRows.Use(list, Row);
        // Enter writes the chosen phrase into the feature, which a stray double click never does: that goes to its method.
        ListKeys.Act(list, new ListActs
        {
            Open = GoToChosen,
            Source = GoToChosen,
            Enter = () =>
            {
                if (list.SelectedItem is LibraryStep chosen)
                {
                    InsertRequested?.Invoke(this, chosen);
                }
            },
        });
        ListMenus.Attach(list, () => list.SelectedItem switch
        {
            LibraryStep chosen =>
            [
                item(AppActions.InsertStep),
                ListMenus.Source(GoToChosen, chosen.Binding.MethodPlace is null ? Refusals.MethodUnplaced : null),
                ListMenus.Item("Find Usages", () => UsagesRequested?.Invoke(this, chosen), chosen.Uses is > 0 ? null : Refusals.StepUses(chosen.Uses)),
                new Separator(),
                ListMenus.Item("Copy Phrase", () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Sample.Text))),
            ],
            StepHook hook => [ListMenus.Source(GoToChosen, hook.MethodPlace is null ? Refusals.MethodUnplaced : null)],
            _ => [],
        });
        empty = new EmptyState();
        Children.Add(new Panel { Children = { list, empty } });
        List();
    }

    /// <summary>A definition whose phrase is to be written into the open feature.</summary>
    public event EventHandler<LibraryStep>? InsertRequested;

    /// <summary>A definition's or hook's method to go to.</summary>
    public event EventHandler<BindingPlace>? GoToRequested;

    /// <summary>A definition whose steps are to be listed as usages.</summary>
    public event EventHandler<LibraryStep>? UsagesRequested;

    /// <summary>The rows listed: headings, definitions and hooks.</summary>
    public IReadOnlyList<object> Rows => list.ItemsSource as IReadOnlyList<object> ?? [];

    /// <summary>The definitions listed, as the filter leaves them.</summary>
    public IReadOnlyList<LibraryStep> Steps => [.. Rows.OfType<LibraryStep>()];

    /// <summary>The hooks listed, as the filter leaves them.</summary>
    public IReadOnlyList<StepHook> Hooks => [.. Rows.OfType<StepHook>()];

    /// <summary>What the window says in place of rows; null while it lists some.</summary>
    public string? Said => empty.IsVisible ? empty.Text : null;

    /// <summary>The definition chosen, for the window's actions.</summary>
    public LibraryStep? Chosen => list.SelectedItem as LibraryStep;

    /// <summary>The filter's words and @tags.</summary>
    public string Filter
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>Whether only the definitions no feature step uses are listed.</summary>
    public bool UnusedOnly
    {
        get => unusedOnly;
        set
        {
            unusedOnly = value;
            List();
        }
    }

    public FilterMenu Filters => filters;

    /// <summary>Chooses a definition's row, as a click does.</summary>
    public void Choose(LibraryStep step) => list.SelectedItem = Rows.FirstOrDefault(r => r is LibraryStep listed && StepLibrary.KeyOf(listed.Binding) == StepLibrary.KeyOf(step.Binding));

    /// <summary>Gives the filter the keys, its text chosen: Find's keys while the window has them.</summary>
    public void FocusFilter()
    {
        filter.Focus();
        filter.SelectAll();
    }

    /// <summary>Lists the definitions and hooks, or says <paramref name="none"/> in their place while there are none to list
    /// (the bindings not read yet, or no project binding any).</summary>
    public void Show(IReadOnlyList<LibraryStep> definitions, IReadOnlyList<StepHook> found, string? none)
    {
        UiThread.Check();
        steps = definitions;
        hooks = found;
        nothing = none;
        List();
    }

    /// <summary>The rows the filter leaves, under their kinds' headings, the chosen definition kept chosen.</summary>
    private void List()
    {
        StepFilter fits = StepFilter.Parse(filter.Text);
        filters.Narrowed = unusedOnly || fits.Tags.Count > 0;
        string? chosen = list.SelectedItem is LibraryStep was ? StepLibrary.KeyOf(was.Binding) : null;
        List<object> rows = [];
        foreach (IGrouping<BindingKind, LibraryStep> kind in steps.Where(s => fits.Fits(s) && (!unusedOnly || s.Uses == 0)).GroupBy(s => s.Binding.Kind))
        {
            LibraryStep[] listed = [.. kind];
            rows.Add(new Heading(KindTitle(kind.Key), listed.Length));
            rows.AddRange(listed);
        }
        StepHook[] running = unusedOnly ? [] : [.. hooks.Where(fits.Fits)];
        if (running.Length > 0)
        {
            rows.Add(new Heading("Hooks", running.Length));
            rows.AddRange(running);
        }
        list.ItemsSource = rows;
        if (chosen is not null)
        {
            list.SelectedItem = rows.FirstOrDefault(r => r is LibraryStep step && StepLibrary.KeyOf(step.Binding) == chosen);
        }
        empty.IsVisible = rows.Count == 0;
        empty.Text = nothing ?? (steps.Count == 0 && hooks.Count == 0
            ? "No step definitions yet: a [Given], [When] or [Then] method of a [Binding] class is listed here"
            : unusedOnly && fits.IsEmpty ? "Every step definition is used by a feature step"
            : "No step definition fits the filter");
    }

    /// <summary>The filter menu: the unused alone, and each tag a definition's or hook's scope names, which puts it in the
    /// filter or takes it out.</summary>
    private IReadOnlyList<Control> FilterItems()
    {
        List<Control> items = [ListMenus.Toggle("Unused Only", unusedOnly, on => UnusedOnly = on)];
        string[] tags = [.. steps.SelectMany(s => s.Tags).Concat(hooks.SelectMany(h => StepLibrary.TagsOf(h.Scopes))).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)];
        if (tags.Length > 0)
        {
            items.Add(new Separator());
            string[] terms = Filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (string tag in tags)
            {
                bool on = terms.Contains(tag, StringComparer.OrdinalIgnoreCase);
                items.Add(ListMenus.Toggle(tag, on, turn => Filter = string.Join(' ', turn ? [.. terms, tag] : terms.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))));
            }
        }
        return items;
    }

    private static string KindTitle(BindingKind kind) => kind switch
    {
        BindingKind.Given => "Given",
        BindingKind.When => "When",
        BindingKind.Then => "Then",
        _ => "Any step",
    };

    private void GoToChosen()
    {
        if ((list.SelectedItem switch { LibraryStep step => step.Binding.MethodPlace, StepHook hook => hook.MethodPlace, _ => null }) is { } place)
        {
            GoToRequested?.Invoke(this, place);
        }
    }

    /// <summary>A row for whichever heading, definition or hook the list hands its container.</summary>
    private Control Row()
    {
        TextBlock title = new() { Classes = { StyleClasses.Strong }, VerticalAlignment = VerticalAlignment.Center };
        TextBlock count = MutedText.Of(string.Empty);
        count.Margin = new Thickness(Gaps.Small, 0, 0, 0);
        StackPanel heading = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Margin = new Thickness(Gaps.Normal, 0), Children = { title, count } };
        Panel row = new();
        (DockPanel step, Action<LibraryStep> showStep) = StepRow(row);
        (DockPanel hook, Action<StepHook> showHook) = HookRow();
        row.Children.Add(heading);
        row.Children.Add(step);
        row.Children.Add(hook);
        return row.Showing<object>(item =>
        {
            heading.IsVisible = item is Heading;
            step.IsVisible = item is LibraryStep;
            hook.IsVisible = item is StepHook;
            switch (item)
            {
                case Heading shown:
                    title.Text = shown.Title;
                    count.Text = shown.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
                    break;
                case LibraryStep shown:
                    showStep(shown);
                    break;
                case StepHook shown:
                    showHook(shown);
                    break;
            }
        });
    }

    /// <summary>A definition's line in a row: its phrase, slots in an argument's colour (muted where no step uses it), its
    /// tags, how many steps use it (a link to them) or that none does, and Insert, which writes the row's phrase.</summary>
    private (DockPanel Line, Action<LibraryStep> Show) StepRow(Control row)
    {
        Button insert = ToolButtons.Of(IconKeys.Add, "Insert into Feature", () =>
        {
            if (row.DataContext is LibraryStep step)
            {
                InsertRequested?.Invoke(this, step);
            }
        });
        insert.Name = "stepInsert";
        insert.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(insert, Dock.Right);
        Button uses = new() { Classes = { StyleClasses.Link, StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Small, 0, Gaps.Hair, 0) };
        uses.Click += (_, _) =>
        {
            if (row.DataContext is LibraryStep step)
            {
                UsagesRequested?.Invoke(this, step);
            }
        };
        TextBlock unused = new() { Classes = { StyleClasses.Caption }, Text = "Unused", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0) };
        unused.Bind(TextBlock.ForegroundProperty, unused.GetResourceObservable(TokenKeys.Warning));
        Panel use = new() { [DockPanel.DockProperty] = Dock.Right, Children = { uses, unused } };
        TextBlock tagWords = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center };
        tagWords.Classes.Add(StyleClasses.Muted);
        Border tags = new() { [DockPanel.DockProperty] = Dock.Right, Classes = { StyleClasses.Tag },  Margin = new Thickness(Gaps.Small, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 140, Child = tagWords };
        tagWords.TextTrimming = TextTrimming.CharacterEllipsis;
        TextBlock phrase = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // The phrase takes what the row leaves, so a narrow window ends it in an ellipsis and keeps its uses and Insert.
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Page, 0, Gaps.Tight, 0), Children = { insert, use, tags, phrase } };
        return (line, step =>
        {
            ShowPhrase(phrase, step.Sample);
            bool idle = step.Uses == 0;
            phrase.Classes.Set(StyleClasses.Muted, idle);
            uses.IsVisible = step.Uses is > 0;
            uses.Content = step.Uses is { } counted ? Plural.Of(counted, "use") : null;
            ToolButtons.Tip(uses, step.Uses is { } many ? $"Find Usages: the {Plural.Of(many, "feature step")} it binds" : "Find Usages");
            unused.IsVisible = idle;
            tags.IsVisible = step.Tags.Count > 0;
            tagWords.Text = string.Join(' ', step.Tags);
            ToolTip.SetTip(line, Described(step));
        });
    }

    /// <summary>A hook's line in a row: when it runs, its method (muted) and its tags.</summary>
    private static (DockPanel Line, Action<StepHook> Show) HookRow()
    {
        TextBlock when = new() { VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left };
        TextBlock method = MutedText.Caption(string.Empty);
        method.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        TextBlock tagWords = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        tagWords.Classes.Add(StyleClasses.Muted);
        Border tags = new() { [DockPanel.DockProperty] = Dock.Right, Classes = { StyleClasses.Tag },  Margin = new Thickness(Gaps.Small, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 140, Child = tagWords };
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Page, 0, Gaps.Normal, 0), Children = { tags, when, method } };
        return (line, hook =>
        {
            IReadOnlyList<string> tagged = StepLibrary.TagsOf(hook.Scopes);
            when.Text = StepLibrary.HookWords(hook.Kind);
            method.Text = $"{TypeNames.Short(hook.Type)}.{hook.Method}";
            tags.IsVisible = tagged.Count > 0;
            tagWords.Text = string.Join(' ', tagged);
            ToolTip.SetTip(line, $"[{hook.Kind}{(tagged.Count > 0 ? $"(\"{string.Join("\", \"", tagged)}\")" : string.Empty)}] {TypeNames.Short(hook.Type)}.{hook.Method}, order {hook.Order}"
                + (tagged.Count > 0 ? $"\nRuns only where a scenario is tagged {Plural.Either(tagged)}" : string.Empty));
        });
    }

    /// <summary>A phrase's words, each slot in the colour the editor gives a step's arguments.</summary>
    private static void ShowPhrase(TextBlock phrase, StepSample sample)
    {
        InlineCollection inlines = [];
        int at = 0;
        foreach (StepArgument hole in sample.Holes)
        {
            if (hole.Start > at)
            {
                inlines.Add(new Avalonia.Controls.Documents.Run(sample.Text[at..hole.Start]));
            }
            Avalonia.Controls.Documents.Run slot = new(sample.Text.Substring(hole.Start, hole.Length));
            slot.Bind(TextElement.ForegroundProperty, phrase.GetResourceObservable(TokenKeys.Syntax_Constant));
            inlines.Add(slot);
            at = hole.Start + hole.Length;
        }
        if (at < sample.Text.Length)
        {
            inlines.Add(new Avalonia.Controls.Documents.Run(sample.Text[at..]));
        }
        phrase.Inlines = inlines;
    }

    /// <summary>A definition's tip: its method, its expression as written, its scopes and its documentation's summary.</summary>
    private static string Described(LibraryStep step)
    {
        StepBinding binding = step.Binding;
        List<string> lines = [$"[{binding.Kind}{(binding.Expression is { } expression ? $"(\"{expression}\")" : string.Empty)}] {binding.Shown}"];
        foreach (StepScope scope in binding.Scopes)
        {
            lines.Add("Only where " + string.Join(", ", ((string?[])[scope.Tag is { } tag ? $"a scenario is tagged @{tag.TrimStart('@')}" : null,
                scope.Feature is { } feature ? $"the feature is \"{feature}\"" : null, scope.Scenario is { } scenario ? $"the scenario is \"{scenario}\"" : null]).OfType<string>()));
        }
        if (binding.Summary is { } summary)
        {
            lines.Add(summary);
        }
        return string.Join('\n', lines);
    }
}
