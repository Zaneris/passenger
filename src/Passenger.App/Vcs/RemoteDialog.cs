using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>What Add Remote… and Edit Remote… open on: the remote edited (null for a new one), the other remotes' names,
/// how many of its branches this repository holds and which local branches track it, and why nothing can be fetched here
/// (a folder not trusted), if so.</summary>
public sealed record RemoteStart(RemoteInfo? Editing, IReadOnlyList<string> Others, IReadOnlyList<string> Branches, IReadOnlyList<string> Tracking, string? WhyNotFetch);

/// <summary>Add Remote… and Edit Remote…: a remote's name and URL, and for a new one whether its branches are fetched at
/// once; under them what it will do before anything runs, in words and in git's. A URL that holds a password or token is
/// shown masked, and kept as it is until the person types another. Add Remote (Save Remote) closes it with the plan,
/// greyed with why while git would refuse it.</summary>
public sealed class RemoteDialog : IdeWindow
{
    private readonly RemoteStart start;
    private readonly TextBox name;
    private readonly TextBox url;
    private readonly TextBlock urlNote = FormRows.Note();
    private readonly FormDropDown then;
    private readonly StackPanel said = new() { Spacing = Gaps.Tight };
    private readonly TextBlock gitWords = new() { Classes = { StyleClasses.Code, StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap };

    /// <summary>The URL as the box first showed it: masked where it holds a secret, which keeps the URL as it is.</summary>
    private readonly string shownUrl;
    private bool touched;

    public RemoteDialog(RemoteStart start)
    {
        this.start = start;
        FormRows.Scope(this);
        Title = start.Editing is null ? "Add Remote" : "Edit Remote";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        shownUrl = start.Editing is { } editing ? VcsController.UrlShown(editing.Url) : string.Empty;
        name = new TextBox { Text = start.Editing?.Name ?? (start.Others.Contains("origin", StringComparer.Ordinal) ? string.Empty : "origin"), PlaceholderText = "origin" };
        url = new TextBox { Text = shownUrl, PlaceholderText = "https://example.com/team/project.git" };
        urlNote.Text = shownUrl != start.Editing?.Url && start.Editing is not null ? "It holds a password or token, shown masked: type the whole URL to change it." : null;
        name.TextChanged += (_, _) => Typed();
        url.TextChanged += (_, _) => Typed();
        then = new FormDropDown { ItemsSource = (string[])["Fetch its branches now", "Leave it until fetched"], SelectedIndex = start.WhyNotFetch is null ? 0 : 1 };
        ToolButtons.Grey(then, start.WhyNotFetch);
        then.SelectionChanged += (_, _) => Lay();
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children = { FormRows.Of("Name", name), FormRows.Of("URL", url, [urlNote]) },
        };
        if (start.Editing is null)
        {
            fields.Children.Add(FormRows.Of("Then", then));
        }
        StackPanel preview = new()
        {
            Spacing = Gaps.Tight,
            Children =
            {
                new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = "What it does" },
                new Border { Classes = { StyleClasses.Framed }, Padding = new Thickness(Gaps.Normal), Child = new StackPanel { Spacing = Gaps.Normal, Children = { said, gitWords } } },
            },
        };
        SaveButton = DialogFooter.Default(start.Editing is null ? "Add Remote" : "Save Remote");
        SaveButton.Click += (_, _) => Save();
        Content = DialogFooter.Headed(Title,
            new TextBlock
            {
                Classes = { StyleClasses.Muted },
                TextWrapping = TextWrapping.Wrap,
                Text = "A remote is another copy of the repository, on a server or in a folder, that this one fetches branches from and pushes them to, by a short name.",
            },
            new StackPanel { Spacing = Gaps.Group, Children = { fields, preview } },
            DialogFooter.OkCancel(SaveButton, () => Close(null), problem));
        Lay();
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            TextBox first = start.Editing is null && name.Text!.Length > 0 ? url : name;
            first.Focus();
            first.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    public Button SaveButton { get; }

    public TextBox NameBox => name;

    public TextBox UrlBox => url;

    /// <summary>Fetch its branches now, or leave it until fetched: a new remote's.</summary>
    public ComboBox ThenBox => then;

    /// <summary>What it will do, a sentence each, as the preview says it.</summary>
    public IReadOnlyList<string> WhatItDoes => [.. said.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>What it does in git's words, a URL's secret masked.</summary>
    public string GitWords => gitWords.Text ?? string.Empty;

    /// <summary>Why it will not save, once the person has had a hand in it; empty while it will.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>The plan it closed with; null until then, and when given up.</summary>
    public RemotePlan? Plan { get; private set; }

    /// <summary>Add Remote or Save Remote: the plan taken where git would carry it out, else why not said.</summary>
    public void Save()
    {
        touched = true;
        if (Planned() is { } plan)
        {
            Plan = plan;
            Close(plan);
        }
    }

    private string RemoteName => (name.Text ?? string.Empty).Trim();

    /// <summary>The URL planned: the one the remote has while the box shows it masked, else what is typed.</summary>
    private string Url => start.Editing is { } editing && url.Text == shownUrl ? editing.Url : (url.Text ?? string.Empty).Trim();

    private bool Fetches => start.Editing is null && then.SelectedIndex == 0 && start.WhyNotFetch is null;

    private void Typed()
    {
        touched = true;
        Lay();
    }

    /// <summary>The plan as the dialog stands where git would carry it out; null, with why said, where not.</summary>
    private RemotePlan? Planned()
    {
        string? why = WhyNot();
        problem.Text = touched ? why : null;
        SaveButton.IsEnabled = why is null;
        return why is null ? new RemotePlan(start.Editing?.Name, RemoteName, Url, Fetches) : null;
    }

    private string? WhyNot()
    {
        IReadOnlyList<string> others = [.. start.Others.Where(o => o != start.Editing?.Name)];
        if ((Remotes.WhyNot(RemoteName, others) ?? Remotes.WhyNotUrl(Url)) is { } refused)
        {
            return refused;
        }
        return start.Editing is { } editing && editing.Name == RemoteName && editing.Url == Url ? "Change its name or its URL first" : null;
    }

    /// <summary>What it will do said again, in words and in git's.</summary>
    private void Lay()
    {
        RemotePlan? plan = Planned();
        said.Children.Clear();
        foreach (string sentence in Sentences(plan))
        {
            said.Children.Add(new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap });
        }
        gitWords.Text = plan is null ? null : GitSaid(plan);
        gitWords.IsVisible = plan is not null;
    }

    /// <summary>The preview's sentences: what is added or changed, what comes of it here, and what the remote keeps.</summary>
    private IEnumerable<string> Sentences(RemotePlan? plan)
    {
        if (plan is null)
        {
            yield return start.Editing is null ? "Name the remote and give its URL." : "Nothing yet: what stands in the way is said below.";
            yield break;
        }
        string shown = VcsController.UrlShown(plan.Url);
        if (start.Editing is not { } editing)
        {
            yield return $"Adds the remote {plan.Name}, which this repository fetches from and pushes to at {shown}.";
            yield return plan.Fetch
                ? $"Fetches its branches now: they come under Remote › {plan.Name} in the Git log's branches, and nothing of yours changes."
                : "Fetches nothing yet: Fetch, in the Git menu, brings its branches in.";
            yield break;
        }
        if (plan.Name != editing.Name)
        {
            yield return VcsWords.RenamesRemote(editing.Name, plan.Name, start.Branches.Count, start.Tracking.Count, Plural.Listed(start.Tracking)).Shown;
        }
        if (plan.Url != editing.Url)
        {
            yield return $"Fetches from and pushes to {shown} from now on, in place of {VcsController.UrlShown(editing.Url)}; its branches here stay as last fetched until it is fetched again.";
        }
        yield return "The repository it names is not changed.";
    }

    /// <summary>The plan in git's own words, a URL's secret masked.</summary>
    private string GitSaid(RemotePlan plan)
    {
        string shown = VcsController.UrlShown(plan.Url);
        if (plan.Was is null)
        {
            return plan.Fetch ? $"git remote add {plan.Name} {shown}\ngit fetch {plan.Name}" : $"git remote add {plan.Name} {shown}";
        }
        List<string> lines = [];
        if (plan.Name != plan.Was)
        {
            lines.Add($"git remote rename {plan.Was} {plan.Name}");
        }
        if (plan.Url != start.Editing!.Url)
        {
            lines.Add($"git remote set-url {plan.Name} {shown}");
        }
        return string.Join('\n', lines);
    }
}
