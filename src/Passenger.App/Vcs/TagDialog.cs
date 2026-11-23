using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>What New Tag… opens on: the revision it tags (a commit's hash, a branch, HEAD), a tag's name where one is
/// moved, how commits are signed, and the remotes it may be pushed to (none, with why, where none can be reached).</summary>
public sealed record TagStart(string At, string? Name, SigningSetup Signing, IReadOnlyList<string> Remotes, string? WhyNotPush);

/// <summary>New Tag… and Move Tag…: a tag's name, the commit it names (any revision, looked up as typed), lightweight or
/// annotated with a message, signed where a key is set up, and pushed to a remote or kept here; and under them what it
/// will do before anything runs, in words and in git's: a tag made, or one of the name moved from the commit it names, what
/// signing and pushing mean, and what a remote or whoever fetched it keeps. Create Tag closes it with the plan, greyed
/// with why while git would refuse it.</summary>
public sealed class TagDialog : IdeWindow
{
    private readonly Func<string, Task<CommitInfo?>> commitOf;
    private readonly Func<string, Task<TagInfo?>> tagOf;
    private readonly SigningSetup signing;
    private readonly TextBox name;
    private readonly TextBox at;
    private readonly TextBlock atNote = FormRows.Note();
    private readonly Segments<bool> kind = new();
    private readonly TextBlock kindNote = FormRows.Note();
    private readonly TextBox message;
    private readonly Grid messageRow;
    private readonly Grid signRow;
    private readonly CheckBox sign;
    private readonly FormDropDown push;
    private readonly StackPanel said = new() { Spacing = Gaps.Tight };
    private readonly TextBlock gitWords = new() { Classes = { StyleClasses.Code, StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap };
    private readonly Debounce looking;
    private readonly IReadOnlyList<string> remotes;
    private bool touched;

    /// <summary>The name and revision last looked up, and what they named.</summary>
    private (string TagName, string At, TagInfo? Tag, CommitInfo? Commit, CommitInfo? TagCommit) looked = (string.Empty, string.Empty, null, null, null);

    /// <param name="start">What it opens on.</param>
    /// <param name="commitOf">The commit a revision names, or null.</param>
    /// <param name="tagOf">The tag of a name as it stands, or null.</param>
    public TagDialog(TagStart start, Func<string, Task<CommitInfo?>> commitOf, Func<string, Task<TagInfo?>> tagOf)
    {
        this.commitOf = commitOf;
        this.tagOf = tagOf;
        signing = start.Signing;
        remotes = start.WhyNotPush is null ? start.Remotes : [];
        FormRows.Scope(this);
        Title = start.Name is null ? "New Tag" : "Move Tag";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        looking = new Debounce(TypingPauses.GitNames, LookAsync);

        name = new TextBox { Text = start.Name ?? string.Empty, PlaceholderText = "v1.0" };
        at = new TextBox { Text = start.At };
        name.TextChanged += (_, _) => Typed();
        at.TextChanged += (_, _) => Typed();
        kind.Add(false, "Lightweight", "Lightweight");
        kind.Add(true, "Annotated", "Annotated");
        kind.Choice = signing.SignsTags;
        kind.Chosen += (_, _) => Lay();
        message = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 88,
            PlaceholderText = "Why this commit is tagged: a release's notes, say",
        };
        message.TextChanged += (_, _) => Lay();
        sign = new CheckBox { Content = signing.CanSign ? $"Sign it with {signing.KeyShown}" : "Sign it", IsChecked = signing.CanSign && (signing.SignsTags || signing.Signs) };
        ToolButtons.Grey(sign, signing.CanSign ? null : $"No key is set up to sign with: Git › {SigningFailure.SetUp} sets one up");
        sign.IsCheckedChanged += (_, _) => Lay();
        push = new FormDropDown { ItemsSource = (string[])["Keep it in this repository", .. remotes.Select(r => $"Push it to {r}")], SelectedIndex = 0 };
        ToolButtons.Grey(push, start.WhyNotPush ?? (start.Remotes.Count == 0 ? "The repository has no remote to push it to" : null));
        push.SelectionChanged += (_, _) => Lay();

        messageRow = FormRows.Of("Message", message, top: true);
        signRow = FormRows.Of(null, sign);
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children =
            {
                FormRows.Of("Name", name),
                FormRows.Of("At", at, [atNote]),
                FormRows.Of("Kind", kind, [kindNote]),
                messageRow,
                signRow,
                FormRows.Of("Then", push),
            },
        };
        StackPanel preview = new()
        {
            Spacing = Gaps.Tight,
            Children =
            {
                new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = "What it does" },
                new Border { Classes = { StyleClasses.Framed }, Padding = new Thickness(Gaps.Normal), Child = new StackPanel { Spacing = Gaps.Normal, Children = { said, gitWords } } },
            },
        };
        CreateButton = DialogFooter.Default(Title == "Move Tag" ? "Move Tag" : "Create Tag");
        CreateButton.Click += (_, _) => IdeErrors.Observe(StartAsync());
        Content = DialogFooter.Headed(Title,
            new TextBlock
            {
                Classes = { StyleClasses.Muted },
                TextWrapping = TextWrapping.Wrap,
                Text = "A tag names a commit for good, as a release's version does, where a branch moves on with each commit.",
            },
            new StackPanel { Spacing = Gaps.Group, Children = { fields, preview } },
            DialogFooter.OkCancel(CreateButton, () => Close(null), problem));
        Lay();
        Opened += (_, _) =>
        {
            looking.Restart();
            Dispatcher.UIThread.Post(() =>
            {
                name.Focus();
                name.SelectAll();
            }, DispatcherPriority.Loaded);
        };
    }

    public Button CreateButton { get; }

    public TextBox NameBox => name;

    public TextBox AtBox => at;

    public TextBox MessageBox => message;

    public CheckBox SignBox => sign;

    /// <summary>Lightweight or Annotated.</summary>
    public Segments<bool> KindBox => kind;

    public ComboBox PushBox => push;

    /// <summary>Whether the tag is annotated, as its kind is chosen.</summary>
    public bool Annotated
    {
        get => kind.Choice;
        set
        {
            kind.Choice = value;
            Lay();
        }
    }

    /// <summary>What it will do, a sentence each, as the preview says it.</summary>
    public IReadOnlyList<string> WhatItDoes => [.. said.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>What it does in git's words.</summary>
    public string GitWords => gitWords.Text ?? string.Empty;

    /// <summary>Why Create Tag will not make it, once the person has had a hand in it; empty while it will.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>The plan it closed with; null until then, and when given up.</summary>
    public TagPlan? Plan { get; private set; }

    /// <summary>Create Tag: the name and revision looked up as they stand, then the plan taken where git would make it, else
    /// why not said.</summary>
    public async Task StartAsync()
    {
        touched = true;
        if (looked.TagName != TagName || looked.At != At)
        {
            looking.Stop();
            await LookAsync();
        }
        if (Planned() is { } plan)
        {
            Plan = plan;
            Close(plan);
        }
    }

    private string TagName => (name.Text ?? string.Empty).Trim();

    private string At => (at.Text ?? string.Empty).Trim();

    private string? PushTo => push.SelectedIndex > 0 ? remotes[push.SelectedIndex - 1] : null;

    private void Typed()
    {
        touched = true;
        looking.Restart();
        Lay();
    }

    /// <summary>What the name and the revision name now, then the preview again; a newer lookup's answer stands.</summary>
    private async Task LookAsync()
    {
        string asked = TagName;
        string atAsked = At;
        TagInfo? tag = TagNames.WhyNot(asked) is null ? await tagOf(asked) : null;
        CommitInfo? commit = atAsked.Length > 0 ? await commitOf(atAsked) : null;
        CommitInfo? tagCommit = tag is null ? null : commit?.Hash == tag.Commit ? commit : await commitOf(tag.Commit);
        if (asked != TagName || atAsked != At)
        {
            return;
        }
        bool newlyFound = tag is not null && looked.Tag?.Name != tag.Name;
        looked = (asked, atAsked, tag, commit, tagCommit);
        if (newlyFound && tag!.IsAnnotated)
        {
            // A tag moved keeps its kind and message unless the person changes them.
            kind.Choice = true;
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                message.Text = tag.Message;
            }
        }
        Lay();
    }

    /// <summary>The plan as the dialog stands where git would carry it out; null, with why said, where not.</summary>
    private TagPlan? Planned()
    {
        string? why = WhyNot();
        problem.Text = touched ? why : null;
        CreateButton.IsEnabled = why is null;
        return why is null ? new TagPlan(TagName, looked.Commit!, kind.Choice ? message.Text!.TrimEnd() : null, kind.Choice && sign.IsChecked == true, PushTo, looked.Tag) : null;
    }

    private string? WhyNot()
    {
        if (TagNames.WhyNot(TagName) is { } badName)
        {
            return badName;
        }
        if (At.Length == 0)
        {
            return "Say which commit to tag: a hash, a branch or HEAD";
        }
        if (looked.TagName != TagName || looked.At != At)
        {
            return "Looking up the name and the commit…";
        }
        if (looked.Commit is null)
        {
            return $"{At} names no commit here";
        }
        if (kind.Choice && string.IsNullOrWhiteSpace(message.Text))
        {
            return "An annotated tag needs a message";
        }
        if (looked.Tag is { } tag && tag.Commit == looked.Commit.Hash && !kind.Choice && !tag.IsAnnotated)
        {
            return $"{TagName} names {looked.Commit.Short} already";
        }
        return null;
    }

    /// <summary>The message row shown for an annotated tag alone, the signing with it, then what it will do said again.</summary>
    private void Lay()
    {
        bool annotated = kind.Choice;
        messageRow.IsVisible = annotated;
        signRow.IsVisible = annotated;
        kindNote.Text = annotated
            ? "A record of its own: who tagged the commit, when and why, which can be signed, and which git describe and release tools go by."
            : "A name for the commit and nothing more: no message, tagger or date of its own, and never signed.";
        bool current = looked.TagName == TagName && looked.At == At;
        atNote.Text = !current || At.Length == 0 ? null : looked.Commit is { } commit ? $"{commit.Short} {commit.Subject}" : $"{At} names no commit here";
        TagPlan? plan = Planned();
        string? moving = looked.Tag is { } tag && current && looked.Commit is not null ? Shown(tag, looked.TagCommit) : null;
        CreateButton.Content = moving is null ? "Create Tag" : "Move Tag";
        said.Children.Clear();
        foreach (string sentence in Sentences(plan, moving))
        {
            said.Children.Add(new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap });
        }
        gitWords.Text = plan is null ? null : GitSaid(plan);
        gitWords.IsVisible = plan is not null;
    }

    /// <summary>Where a tag stands now, as the preview names it.</summary>
    private static string Shown(TagInfo tag, CommitInfo? commit) => commit is null ? GitHashes.Short(tag.Commit) : $"{commit.Short} ({commit.Subject})";

    /// <summary>The preview's sentences: what is made or moved, what an annotated tag keeps and signing means, and where it
    /// goes; with no plan yet, only what is known.</summary>
    private IEnumerable<string> Sentences(TagPlan? plan, string? moving)
    {
        if (plan is null)
        {
            yield return TagName.Length == 0 ? "Name the tag, and say which commit it names." : "Nothing yet: what stands in the way is said below.";
            yield break;
        }
        string kindWords = plan.IsAnnotated ? "an annotated" : "a lightweight";
        string target = $"{plan.Commit.Short} ({plan.Commit.Subject})";
        yield return moving is null
            ? $"Makes {kindWords} tag {plan.Name} at {target}."
            : plan.Moving!.Commit == plan.Commit.Hash
                ? $"Makes {plan.Name} again at {target}, as {kindWords} tag in place of the {(plan.Moving.IsAnnotated ? "annotated" : "lightweight")} one there."
                : $"Moves {plan.Name} from {moving} to {target}.";
        if (plan.IsAnnotated)
        {
            yield return plan.Sign
                ? $"Keeps the message, you as its tagger and the time with it; signs it with {signing.KeyShown}, so whoever fetches it can check it is yours."
                : "Keeps the message, you as its tagger and the time with it.";
        }
        if (plan.PushTo is { } remote)
        {
            yield return plan.Moving is null
                ? $"Pushes it to {remote}, where whoever fetches from {remote} gets it."
                : $"Pushes it to {remote} in place of the {plan.Name} there, if it has one: whoever fetched that one keeps it until they fetch tags again with --force.";
        }
        else
        {
            yield return plan.Moving is null
                ? "Keeps it in this repository alone: others get it once it is pushed (Push… in its menu in the Git log's branches)."
                : $"A remote that has {plan.Name}, and whoever fetched it, keeps it where it was until it is pushed again.";
        }
    }

    /// <summary>The plan in git's own words, a message standing as "…".</summary>
    private static string GitSaid(TagPlan plan)
    {
        string force = plan.Moving is null ? string.Empty : " -f";
        string tag = plan.IsAnnotated
            ? $"git tag -a{(plan.Sign ? " -s" : string.Empty)}{force} -m \"…\" {plan.Name} {plan.Commit.Short}"
            : $"git tag{force} {plan.Name} {plan.Commit.Short}";
        return plan.PushTo is { } remote ? $"{tag}\ngit push {remote} {(plan.Moving is null ? string.Empty : "+")}{plan.Name}" : tag;
    }
}
