using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>How a screen reader takes words it is told: after what it is saying (what was done, a stage, a question), or
/// at once, cutting it short (a failure, a refusal).</summary>
public enum Urgency
{
    Polite,
    Assertive,
}

/// <summary>Words a screen reader may read as a live region whose changes it is told of only by the window's
/// <see cref="Announcer"/>, never by the words changing: a key typed on Vim's command line or the file's path put in the
/// status bar's place is shown, not heard. Styled as the TextBlock it is.</summary>
public sealed class LiveText : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override AutomationPeer OnCreateAutomationPeer() => new LiveTextPeer(this);

    private sealed class LiveTextPeer(LiveText owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string? GetNameCore() => ((LiveText)Owner).Text;
    }
}

/// <summary>The one place what a window tells the person reaches a screen reader (VoiceOver, Narrator, NVDA, Orca): what
/// the status bar says, a work's stages and what it came to (<see cref="Working.Tell"/>), a window opened without the
/// keys (a notification, an agent's question). Words told within a moment are gathered and heard once each, the most
/// urgent way they were told, in the order told; a stage that changes only its numbers (a progress tick) is not heard.
/// Each is heard through the window's one announcing region, a live region its words are put in, whose name change
/// Avalonia's bridges turn into the system's own: UI Automation's LiveRegionChanged on Windows, AXLiveRegionChanged with an
/// announcement on macOS; on Linux its AT-SPI bridge sends only the region's new name, as a property change. UI thread
/// only.</summary>
public sealed class Announcer
{
    /// <summary>How long words told are gathered, so the status bar's words and the same words told as a work's end are
    /// heard once.</summary>
    private static readonly TimeSpan Gathering = TimeSpan.FromMilliseconds(150);

    private static readonly ConditionalWeakTable<TopLevel, Announcer> windows = [];

    private readonly LiveText region = new()
    {
        Width = 1,
        Height = 1,
        Opacity = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    private readonly List<(string Words, Urgency Urgency)> told = [];
    private readonly Debounce later;

    /// <summary>The last stage heard with its numbers left out, which a tick of it is not heard against.</summary>
    private string? stageHeard;

    /// <summary>Puts the window's announcing region at the end of <paramref name="root"/>, the window's top panel, where a
    /// reader that reached the window has reached it.</summary>
    public Announcer(TopLevel window, Panel root)
    {
        UiThread.Check();
        Live(region, Urgency.Polite);
        root.Children.Add(region);
        windows.AddOrUpdate(window, this);
        later = new Debounce(Gathering, () =>
        {
            Hear();
            return Task.CompletedTask;
        });
    }

    /// <summary>The announcer of a window or of the window that owns it, through its owners; null where none has one.</summary>
    public static Announcer? Of(TopLevel? window)
    {
        for (TopLevel? at = window; at is not null; at = (at as Window)?.Owner as TopLevel)
        {
            if (windows.TryGetValue(at, out Announcer? announcer))
            {
                return announcer;
            }
        }
        return null;
    }

    /// <summary>Marks words a screen reader may take as a live region, read the way <paramref name="urgency"/> says.</summary>
    public static void Live(Avalonia.StyledElement words, Urgency urgency) =>
        AutomationProperties.SetLiveSetting(words, urgency == Urgency.Assertive ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

    /// <summary>The window's announcing region, as a screen reader meets it: its name the words heard last.</summary>
    public Control Region => region;

    /// <summary>Tells the person <paramref name="words"/>, heard within a moment unless said already then.</summary>
    public void Tell(string? words, Urgency urgency)
    {
        UiThread.Check();
        if (string.IsNullOrWhiteSpace(words))
        {
            return;
        }
        told.Add((words, urgency));
        if (!later.IsWaiting)
        {
            later.Restart();
        }
    }

    /// <summary>Tells a work's stage, unless it is the stage heard last with only its numbers changed.</summary>
    public void TellStage(string stage)
    {
        string shape = string.Concat(stage.Where(c => !char.IsAsciiDigit(c)));
        if (stageHeard == shape)
        {
            return;
        }
        stageHeard = shape;
        Tell(stage, Urgency.Polite);
    }

    /// <summary>Tells a window opened without the keys, its title and what it says.</summary>
    public static void TellOpened(Window opened)
    {
        string? words = AutomationProperties.GetHelpText(opened);
        Of(opened.Owner as TopLevel)?.Tell(string.IsNullOrWhiteSpace(words) ? opened.Title : $"{opened.Title}. {words}", Urgency.Polite);
    }

    private void Hear()
    {
        List<(string Words, Urgency Urgency)> heard = [];
        foreach ((string words, Urgency urgency) in told)
        {
            int same = heard.FindIndex(h => h.Words == words);
            if (same < 0)
            {
                heard.Add((words, urgency));
            }
            else if (urgency > heard[same].Urgency)
            {
                heard[same] = (words, urgency);
            }
        }
        told.Clear();
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(region);
        foreach ((string words, Urgency urgency) in heard)
        {
            string? before = region.Text;
            Live(region, urgency);
            region.Text = words;
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, before, words);
        }
    }
}
