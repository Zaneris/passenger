using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Passenger.Editor;

/// <summary>What a screen reader meets of a control drawing its own text (the code editor, the terminal): a document named
/// as its host names the control (by its file, by its tab), read-only where it is, whose plain value is the caret's line,
/// raised as changed as the caret goes to another line or the line changes, for a reader that reads only a value; a
/// system's own bridge reads the whole text from <see cref="Text"/>.</summary>
public sealed class AccessibleTextPeer : ControlAutomationPeer, IValueProvider
{
    /// <summary>The start of each peer's automation id, which a system's bridge finds the peer's text by
    /// (<see cref="Find"/>).</summary>
    public const string IdStart = "Passenger.AccessibleText.";

    /// <summary>Each peer made, by the number in its id, until its control is let go of; held under its own lock, as each
    /// test session's window makes peers on a thread of its own.</summary>
    private static readonly Dictionary<long, WeakReference<AccessibleTextPeer>> made = [];

    private static long count;

    /// <summary>How many peers were kept at the last sweep for those let go of, so a sweep comes as their number doubles.</summary>
    private static int swept;

    private readonly string id;

    public AccessibleTextPeer(Control owner, AccessibleText text) : base(owner)
    {
        Text = text;
        id = Keep(this);
        text.CaretLineChanged += (_, _) => PeerChanges.Tell(this, ValuePatternIdentifiers.ValueProperty, null, Value);
    }

    /// <summary>The peer a control drawing its own text gives a screen reader: on Windows, where the control stands in a
    /// window of the system's, the element UI Automation reads its text pattern from
    /// (<see cref="UiAutomation.UiaTextElement"/>); elsewhere this peer.</summary>
    public static AutomationPeer For(Control owner, AccessibleText text) =>
        (OperatingSystem.IsWindows() ? UiAutomation.UiaTextElement.PeerFor(owner, text) : null) ?? new AccessibleTextPeer(owner, text);

    /// <summary>The text as a system's bridge reads it.</summary>
    public AccessibleText Text { get; }

    public bool IsReadOnly => Text.IsReadOnly;

    public string? Value => Text.CaretLine;

    /// <summary>Puts a value in the place of the caret's line, refused where the text is read-only.</summary>
    public void SetValue(string? value)
    {
        if (Text.IsReadOnly)
        {
            throw new InvalidOperationException("The text is read-only");
        }
        Text.Replace(Text.LineAt(Text.Caret), value ?? string.Empty);
    }

    /// <summary>The peer whose automation id is <paramref name="automationId"/>, while its control is kept.</summary>
    public static AccessibleTextPeer? Find(string? automationId)
    {
        if (automationId is null || !automationId.StartsWith(IdStart, StringComparison.Ordinal)
            || !long.TryParse(automationId.AsSpan(IdStart.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long number))
        {
            return null;
        }
        lock (made)
        {
            return made.TryGetValue(number, out WeakReference<AccessibleTextPeer>? kept) && kept.TryGetTarget(out AccessibleTextPeer? peer) ? peer : null;
        }
    }

    /// <summary>An id unique in the process, as the control's own would not be.</summary>
    protected override string GetAutomationIdCore() => id;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    /// <summary>Keeps a peer by a new number, sweeping out those let go of as their number doubles, and answers its id.</summary>
    private static string Keep(AccessibleTextPeer peer)
    {
        long number;
        lock (made)
        {
            if (made.Count >= Math.Max(16, swept * 2))
            {
                foreach (long gone in made.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToList())
                {
                    made.Remove(gone);
                }
                swept = made.Count;
            }
            number = ++count;
            made[number] = new WeakReference<AccessibleTextPeer>(peer);
        }
        return IdStart + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
