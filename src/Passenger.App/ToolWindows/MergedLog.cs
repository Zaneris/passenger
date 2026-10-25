using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Passenger.App.ToolWindows;

/// <summary>A console of several sources' lines merged, a Compose project's services' and an Aspire AppHost's resources'
/// alike: a toggle per source whose lines it shows, the editor's own Find over it (its button, and Find's keys while the
/// window has them: case, whole words, regular expressions, every match marked), and the log's own words or toggles at the
/// bar's right (a container's Timestamps). Its owner streams what the toggles choose, again as one changes.</summary>
public sealed class MergedLog : DockPanel
{
    // In a narrow log the last toggles give way first, their words ending in an ellipsis, never under Find and the log's own.
    private readonly ShrinkingRow sources = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);

    /// <param name="findName">The Find button's name.</param>
    /// <param name="own">What stands after Find at the bar's right.</param>
    public MergedLog(string findName, params Control[] own)
    {
        Console = new OutputView(toolbar: false);
        find = new Editors.FindBar { [DockProperty] = Dock.Top };
        Button finding = ToolButtons.Of(IconKeys.Search, "Find in the log", OpenFind);
        finding.Name = findName;
        finding.VerticalAlignment = VerticalAlignment.Center;
        StackPanel right = new() { Orientation = Orientation.Horizontal, Spacing = 4, [DockProperty] = Dock.Right, Children = { finding } };
        foreach (Control control in own)
        {
            right.Children.Add(control);
        }
        DockPanel bar = new() { [DockProperty] = Dock.Top, Height = 32, Margin = new Thickness(8, 0) };
        bar.Children.Add(right);
        bar.Children.Add(sources);
        Children.Add(bar);
        Children.Add(find);
        Children.Add(Console);
    }

    /// <summary>The console the sources' lines stream into.</summary>
    public OutputView Console { get; }

    private readonly Editors.FindBar find;
    private bool findAttached;

    /// <summary>Find over the log, over the console's editor once the log is shown or Find asked for.</summary>
    public Editors.FindBar Find
    {
        get
        {
            if (!findAttached)
            {
                findAttached = true;
                find.Attach(Console.Editor);
            }
            return find;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = Find;
    }

    /// <summary>Opens Find over the log.</summary>
    public void OpenFind() => Find.Open(replace: false);

    /// <summary>The sources' toggles, each on while its lines are shown.</summary>
    public IReadOnlyList<ToggleButton> Toggles => [.. sources.Children.OfType<ToggleButton>()];

    /// <summary>A toggle was turned: the owner streams what is shown now.</summary>
    public event EventHandler? Toggled;

    /// <summary>Whether a source's lines are shown (its toggle on).</summary>
    public bool Shows(string source) => !hidden.Contains(source);

    /// <summary>The sources offered, a toggle each (by key, in its words); one turned off before stays off unless
    /// <paramref name="forget"/> says the sources are another's.</summary>
    public void Offer(IReadOnlyList<(string Key, string Words)> offered, bool forget)
    {
        if (forget)
        {
            hidden.Clear();
        }
        sources.Children.Clear();
        foreach ((string key, string words) in offered)
        {
            ToggleButton toggle = new()
            {
                Classes = { "segment" }, Content = words, IsChecked = !hidden.Contains(key), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
            };
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked == true)
                {
                    hidden.Remove(key);
                }
                else
                {
                    hidden.Add(key);
                }
                Toggled?.Invoke(this, EventArgs.Empty);
            };
            sources.Children.Add(toggle);
        }
    }
}
