using Avalonia.Controls;
using Passenger.App.ToolWindows;

namespace Passenger.App;

/// <summary>A button that puts words on the clipboard for the person to paste elsewhere (Copy Command beside a refusal that
/// leaves the person a command to run, each agent's setup in Settings), then says Copied until what it copies changes or
/// another button of its group copies, when it says its own words again. UiRulesTests fails one built by hand.</summary>
public sealed class CopyButton : Button
{
    /// <summary>What the button says once it copied.</summary>
    public const string Copied = "Copied";

    private readonly string label;
    private readonly List<CopyButton>? group;
    private string? text;

    /// <param name="label">What the button says until it copies.</param>
    /// <param name="group">The buttons copying beside it, of which only the last to copy says Copied; it joins them.</param>
    public CopyButton(string label, List<CopyButton>? group = null)
    {
        this.label = label;
        this.group = group;
        Content = label;
        group?.Add(this);
        Click += (_, _) => IdeErrors.Observe(CopyAsync());
    }

    /// <summary>Styled as the Button it is.</summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>What a press copies, null for nothing; set anew, the button says its own words again.</summary>
    public string? Text
    {
        get => text;
        set
        {
            text = value;
            Content = label;
        }
    }

    private async Task CopyAsync()
    {
        if (text is not { } copied)
        {
            return;
        }
        await ListMenus.CopyAsync(this, copied);
        foreach (CopyButton other in group ?? [])
        {
            other.Content = other.label;
        }
        Content = Copied;
    }
}
