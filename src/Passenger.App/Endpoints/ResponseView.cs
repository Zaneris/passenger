using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Endpoints;

/// <summary>A test's answer: the status, time and size, each criterion passed or failed with what the response had, and
/// the body (JSON indented) or the headers.</summary>
public sealed class ResponseView : DockPanel
{
    private readonly TextBlock summary = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel criteria = new() { Margin = new Thickness(8, 0, 8, 4) };
    private readonly ToggleButton bodyTab = new() { Classes = { "segment" }, Content = "Body", IsChecked = true, FontSize = 12 };
    private readonly ToggleButton headersTab = new() { Classes = { "segment" }, Content = "Headers", FontSize = 12 };
    private readonly TextBox text = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = CSharp.Fonts.Mono,
        FontSize = 12,
        Margin = new Thickness(8, 4, 8, 8),
    };
    private readonly TextBlock empty;
    private readonly Panel shown;

    public ResponseView(string emptyText)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        bodyTab.Click += (_, _) => ShowPart(body: true);
        headersTab.Click += (_, _) => ShowPart(body: false);
        StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Children = { bodyTab, headersTab } };
        DockPanel head = new() { Margin = new Thickness(8, 6), LastChildFill = true };
        DockPanel.SetDock(tabs, Dock.Right);
        head.Children.Add(tabs);
        head.Children.Add(summary);
        DockPanel content = new();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(criteria, Dock.Top);
        content.Children.Add(head);
        content.Children.Add(criteria);
        content.Children.Add(text);
        empty = new TextBlock { Text = emptyText, Classes = { "emptyText" } };
        shown = content;
        shown.IsVisible = false;
        Children.Add(new Panel { Children = { shown, empty } });
    }

    /// <summary>The answer shown, null before any.</summary>
    public EndpointResponse? Response { get; private set; }

    /// <summary>The criteria checked against it.</summary>
    public IReadOnlyList<ExpectationResult> Results { get; private set; } = [];

    /// <summary>Whether every criterion passed.</summary>
    public bool Passed => Response is not null && Results.All(r => r.Passed);

    /// <summary>What the summary line says.</summary>
    public string Summary => summary.Text ?? string.Empty;

    /// <summary>The body or headers as shown.</summary>
    public string Shown => text.Text ?? string.Empty;

    /// <summary>A request under way: its line said, the last answer's parts cleared.</summary>
    public void Sending(string what)
    {
        Response = null;
        Results = [];
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

    public void Show(EndpointResponse response, IReadOnlyList<ExpectationResult> results)
    {
        Response = response;
        Results = results;
        shown.IsVisible = true;
        empty.IsVisible = false;
        bool passed = Passed;
        summary.Text = response.Error is { } error
            ? $"Failed: {error} ({Milliseconds(response.Elapsed)})"
            : $"{response.Status} {response.Reason}   {Milliseconds(response.Elapsed)}   {Size(response.Size)}{(response.Cut ? ", the rest unread" : string.Empty)}";
        Paint(summary, passed ? TokenKeys.Test_Passed : TokenKeys.Test_Failed);
        criteria.Children.Clear();
        foreach (string note in response.Notes)
        {
            TextBlock said = new() { Text = note, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            Paint(said, TokenKeys.TextMuted);
            criteria.Children.Add(said);
        }
        foreach (ExpectationResult result in results)
        {
            PathIcon mark = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0) };
            mark.Bind(PathIcon.DataProperty, mark.GetResourceObservable(result.Passed ? IconKeys.TestPassed : IconKeys.TestFailed));
            Paint(mark, result.Passed ? TokenKeys.Test_Passed : TokenKeys.Test_Failed);
            TextBlock what = new() { Text = result.Expectation.ToString(), FontFamily = CSharp.Fonts.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            TextBlock had = new() { Text = $"  got {result.Actual}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Paint(had, TokenKeys.TextMuted);
            // What the answer had takes what the row leaves, so a narrow pane ends it in an ellipsis rather than cutting it.
            DockPanel.SetDock(mark, Dock.Left);
            DockPanel.SetDock(what, Dock.Left);
            criteria.Children.Add(new DockPanel { Height = ListRow.Height, Children = { mark, what, had } });
        }
        ShowPart(bodyTab.IsChecked == true);
    }

    public void Clear()
    {
        Response = null;
        Results = [];
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

    private static string Milliseconds(TimeSpan elapsed) => $"{elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
