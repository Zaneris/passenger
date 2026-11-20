using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Endpoints;

/// <summary>A test's answer: the status, time and size, each criterion passed or failed with what the response had, each
/// place its body breaks the schema its app's OpenAPI document gives it, and the body (JSON indented) or the headers.</summary>
public sealed class ResponseView : DockPanel
{
    private readonly TextBlock summary = new() { Classes = { StyleClasses.Strong }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel criteria = new() { Margin = new Thickness(Gaps.Normal, 0, Gaps.Normal, Gaps.Tight) };
    private readonly ToggleButton bodyTab = new() { Classes = { StyleClasses.Segment, StyleClasses.Caption }, Content = "Body", IsChecked = true };
    private readonly ToggleButton headersTab = new() { Classes = { StyleClasses.Segment, StyleClasses.Caption }, Content = "Headers" };
    private readonly TextBox text = new TextBox
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Classes = { StyleClasses.Code },
        Margin = new Thickness(Gaps.Normal, Gaps.Tight, Gaps.Normal, Gaps.Normal),
    };
    private readonly EmptyState empty;
    private readonly Panel shown;

    public ResponseView(string emptyText)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        bodyTab.Click += (_, _) => ShowPart(body: true);
        headersTab.Click += (_, _) => ShowPart(body: false);
        StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Tight, Children = { bodyTab, headersTab } };
        DockPanel head = new() { Margin = new Thickness(Gaps.Normal, Gaps.Small), LastChildFill = true };
        DockPanel.SetDock(tabs, Dock.Right);
        head.Children.Add(tabs);
        head.Children.Add(summary);
        DockPanel content = new();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(criteria, Dock.Top);
        content.Children.Add(head);
        content.Children.Add(criteria);
        content.Children.Add(text);
        empty = new EmptyState { Text = emptyText };
        shown = content;
        shown.IsVisible = false;
        Children.Add(new Panel { Children = { shown, empty } });
    }

    /// <summary>The answer shown, null before any.</summary>
    public EndpointResponse? Response { get; private set; }

    /// <summary>The criteria checked against it.</summary>
    public IReadOnlyList<ExpectationResult> Results { get; private set; } = [];

    /// <summary>Where its body breaks the schema its app's OpenAPI document gives it.</summary>
    public IReadOnlyList<SchemaBreak> Breaks { get; private set; } = [];

    /// <summary>Whether every criterion passed, and the body keeps to its schema.</summary>
    public bool Passed => Response is not null && Results.All(r => r.Passed) && Breaks.All(b => b.Unjudged);

    /// <summary>What the summary line says.</summary>
    public string Summary => summary.Text ?? string.Empty;

    /// <summary>The body or headers as shown.</summary>
    public string Shown => text.Text ?? string.Empty;

    /// <summary>A request under way: its line said, the last answer's parts cleared.</summary>
    public void Sending(string what)
    {
        Response = null;
        Results = [];
        Breaks = [];
        shown.IsVisible = true;
        empty.IsVisible = false;
        summary.Text = what;
        Paint(summary, TokenKeys.TextMuted);
        criteria.Children.Clear();
        text.Text = string.Empty;
    }

    /// <summary>A request that could not be sent, and why.</summary>
    public void Refused(string why)
    {
        Sending(why);
        Paint(summary, TokenKeys.Test_Failed);
    }

    public void Show(EndpointResponse response, IReadOnlyList<ExpectationResult> results, IReadOnlyList<SchemaBreak>? breaks = null)
    {
        Response = response;
        Results = results;
        Breaks = breaks ?? [];
        shown.IsVisible = true;
        empty.IsVisible = false;
        bool passed = Passed;
        summary.Text = response.Error is { } error
            ? $"Failed: {error} ({Durations.Said(response.Elapsed)})"
            : $"{response.Status} {response.Reason}   {Durations.Said(response.Elapsed)}   {Sizes.Said(response.Size)}{(response.Cut ? ", the rest unread" : string.Empty)}";
        Paint(summary, passed ? TokenKeys.Test_Passed : TokenKeys.Test_Failed);
        criteria.Children.Clear();
        foreach (string note in response.Notes)
        {
            TextBlock said = new() { Classes = { StyleClasses.Caption }, Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, Gaps.Tight) };
            Paint(said, TokenKeys.TextMuted);
            criteria.Children.Add(said);
        }
        foreach (ExpectationResult result in results)
        {
            PathIcon mark = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, Gaps.Small, 0) };
            mark.Bind(PathIcon.DataProperty, mark.GetResourceObservable(result.Passed ? IconKeys.TestPassed : IconKeys.TestFailed));
            Paint(mark, result.Passed ? TokenKeys.Test_Passed : TokenKeys.Test_Failed);
            TextBlock what = new TextBlock { Classes = { StyleClasses.Code }, Text = result.Expectation.ToString(), VerticalAlignment = VerticalAlignment.Center };
            TextBlock had = new() { Classes = { StyleClasses.Caption }, Text = $"  got {result.Actual}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Paint(had, TokenKeys.TextMuted);
            // What the answer had takes what the row leaves, so a narrow pane ends it in an ellipsis rather than cutting it.
            DockPanel.SetDock(mark, Dock.Left);
            DockPanel.SetDock(what, Dock.Left);
            criteria.Children.Add(new DockPanel { Height = ListRow.Height, Children = { mark, what, had } });
        }
        foreach (SchemaBreak broken in Breaks)
        {
            criteria.Children.Add(BreakRow(broken));
        }
        ShowPart(bodyTab.IsChecked == true);
    }

    /// <summary>A place the body breaks its schema, or one its check could not tell of: the member in the code font, then
    /// what is wrong there or why it was not told.</summary>
    private static Control BreakRow(SchemaBreak broken)
    {
        PathIcon mark = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, Gaps.Small, 0) };
        mark.Bind(PathIcon.DataProperty, mark.GetResourceObservable(broken.Unjudged ? IconKeys.TestSkipped : IconKeys.TestFailed));
        Paint(mark, broken.Unjudged ? TokenKeys.Test_Skipped : TokenKeys.Test_Failed);
        TextBlock lead = new() { Classes = { StyleClasses.Caption }, Text = broken.Unjudged ? "Not checked at " : "Breaks its schema at ", VerticalAlignment = VerticalAlignment.Center };
        TextBlock at = new TextBlock { Classes = { StyleClasses.Code }, Text = broken.At, VerticalAlignment = VerticalAlignment.Center };
        TextBlock said = new() { Classes = { StyleClasses.Caption }, Text = $": {broken.Said}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Paint(said, TokenKeys.TextMuted);
        ToolTip.SetTip(said, broken.Said);
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(lead, Dock.Left);
        DockPanel.SetDock(at, Dock.Left);
        return new DockPanel { Height = ListRow.Height, Children = { mark, lead, at, said } };
    }

    public void Clear()
    {
        Response = null;
        Results = [];
        Breaks = [];
        shown.IsVisible = false;
        empty.IsVisible = true;
    }

    private void ShowPart(bool body)
    {
        bodyTab.IsChecked = body;
        headersTab.IsChecked = !body;
        if (Response is not { } response)
        {
            return;
        }
        text.Text = body
            ? response.Pretty
            : string.Join("\n", [$"{response.Version} {response.Status} {response.Reason}", .. response.Headers.Select(h => $"{h.Key}: {h.Value}")]);
    }

    private static void Paint(Control control, string token)
    {
        AvaloniaProperty property = control is PathIcon ? PathIcon.ForegroundProperty : TextBlock.ForegroundProperty;
        control.Bind(property, control.GetResourceObservable(token));
    }
}
