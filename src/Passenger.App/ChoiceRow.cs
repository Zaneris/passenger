using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Passenger.App;

/// <summary>A row of toggles of which one is chosen, each standing for a key (a page, a scope, a session): a press chooses
/// its toggle and leaves the others unchosen, a press on the chosen one leaves it chosen, and <see cref="Chosen"/> says
/// every press, which the view follows by showing what it stands for. The view says what it shows through
/// <see cref="Choice"/>, which raises nothing. Its toggles wrap onto another line where the row has no room for them all, so
/// none is cut. Each kind is its own look: a tool window's tabs (<see cref="ToolWindows.ToolTabs.Strip{TKey}"/>), a few
/// choices side by side (<see cref="Segments{TKey}"/>).</summary>
public abstract class ChoiceRow<TKey> : WrapPanel
    where TKey : notnull
{
    private readonly List<(TKey Key, ToggleButton Toggle)> toggles = [];

    protected ChoiceRow()
    {
        Orientation = Orientation.Horizontal;
        ItemSpacing = Gaps.Hair;
        LineSpacing = Gaps.Hair;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>A toggle was pressed, the chosen one too: the key it stands for.</summary>
    public event EventHandler<TKey>? Chosen;

    /// <summary>The key the chosen toggle stands for, default while none is chosen. Set, it chooses that key's toggle
    /// alone (none for a key the row lacks), raising nothing.</summary>
    public TKey? Choice
    {
        get
        {
            foreach ((TKey key, ToggleButton toggle) in toggles)
            {
                if (toggle.IsChecked == true)
                {
                    return key;
                }
            }
            return default;
        }
        set
        {
            foreach ((TKey key, ToggleButton toggle) in toggles)
            {
                toggle.IsChecked = value is not null && EqualityComparer<TKey>.Default.Equals(key, value);
            }
        }
    }

    /// <summary>Whether a toggle is chosen.</summary>
    public bool HasChoice => toggles.Any(t => t.Toggle.IsChecked == true);

    /// <summary>The keys, in the toggles' order.</summary>
    public IReadOnlyList<TKey> Keys => [.. toggles.Select(t => t.Key)];

    /// <summary>The toggles, in their order.</summary>
    public IReadOnlyList<ToggleButton> Toggles => [.. toggles.Select(t => t.Toggle)];

    /// <summary>A key's toggle.</summary>
    public ToggleButton this[TKey key] => toggles.First(t => EqualityComparer<TKey>.Default.Equals(t.Key, key)).Toggle;

    /// <summary>Chooses the toggle <paramref name="step"/> places on from the chosen one among those shown, round at the
    /// ends, as a press on it does; false where no other is shown.</summary>
    protected bool StepChoice(int step)
    {
        List<(TKey Key, ToggleButton Toggle)> shown = [.. toggles.Where(t => t.Toggle.IsVisible)];
        int at = shown.FindIndex(t => t.Toggle.IsChecked == true);
        if (shown.Count < (at < 0 ? 1 : 2))
        {
            return false;
        }
        int next = at < 0 ? (step > 0 ? 0 : shown.Count - 1) : (((at + step) % shown.Count) + shown.Count) % shown.Count;
        Choice = shown[next].Key;
        Chosen?.Invoke(this, shown[next].Key);
        return true;
    }

    /// <summary>Takes every toggle away, for a view listing its choices again.</summary>
    public void Clear()
    {
        toggles.Clear();
        Children.Clear();
    }

    /// <summary>Adds a toggle in the kind's look, standing for <paramref name="key"/>, unchosen, named before it is styled
    /// (a screenshot's --click-name, a test's) where <paramref name="name"/> is given.</summary>
    protected T Join<T>(TKey key, T toggle, string? name)
        where T : ToggleButton
    {
        toggle.Name = name;
        toggles.Add((key, toggle));
        Children.Add(toggle);
        toggle.Click += (_, _) =>
        {
            Choice = key;
            Chosen?.Invoke(this, key);
        };
        return toggle;
    }
}

/// <summary>A toggle a press turns on but never off, as one of a row of which one is chosen is (a tab, a segment): pressed
/// while chosen, it stays chosen. It wears a ToggleButton's styles.</summary>
public sealed class ChoiceToggle : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle() => IsChecked = true;
}
