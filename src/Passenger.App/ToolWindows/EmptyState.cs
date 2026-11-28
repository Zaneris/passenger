using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>What a list or a pane says in place of what it would show: its words, muted and centred as every such text
/// is (<c>emptyText</c>), and under them, while one is offered, a link to the action that would give it something to show
/// (Create Git Repository where the folder is in no repository), named with its keys in the keymap in force, which
/// <see cref="ActionRequested"/> asks the window for. Words that name an action's keys follow the keymap too
/// (<see cref="SayWithKeys(string, Func{string?, Words})"/>). Where its window leaves it less room than its words take,
/// they wrap and are cut, never drawn over what is beside it. It is the one place such words are made.</summary>
public sealed class EmptyState : StackPanel
{
    private readonly TextBlock words = new() { Classes = { StyleClasses.EmptyText } };
    private readonly Button link = new() { Classes = { StyleClasses.Link }, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    private string? offered;

    public EmptyState()
    {
        VerticalAlignment = VerticalAlignment.Center;
        ClipToBounds = true;
        Children.Add(words);
        Children.Add(link);
        link.Click += (_, _) =>
        {
            if (offered is { } action)
            {
                ActionRequested?.Invoke(this, action);
            }
        };
    }

    /// <summary>One saying <paramref name="text"/>.</summary>
    public EmptyState(Words text) : this() => Say(text);

    /// <summary>What it says, in the language in force; words set here are verbatim, said the same in every language.</summary>
    public string? Text
    {
        get => words.Text;
        set => Say(value);
    }

    /// <summary>Has it say <paramref name="said"/>, again in place as the language changes.</summary>
    public void Say(Words? said) => ShownWords.Set(words, TextBlock.TextProperty, said);

    /// <summary>Has it say words naming an action's keys as the keymap in force has them, null while nothing runs it.</summary>
    public void SayWithKeys(string action, Func<string?, Words> said) => ActionTips.SetWords(words, action, said);

    /// <summary>Has it say words naming several actions' keys, each as <c>keys(action)</c> gives it.</summary>
    public void SayWithKeys(Func<Func<string, string?>, Words> said) => ActionTips.SetWords(words, said);

    /// <summary>The action its link asks for, by its id and title, the link naming its keys; none with null.</summary>
    public void Offer(string? action, Words? title)
    {
        offered = action;
        link.IsVisible = action is not null;
        if (action is null)
        {
            words.ClearValue(MarginProperty);
            return;
        }
        Words named = title ?? action;
        ActionTips.SetWords(link, action, keys => ActionTips.Named(named, keys));
        // The link sits under the words as their next line, not a paragraph apart.
        words.Margin = new Thickness(Gaps.Group, Gaps.Group, Gaps.Group, Gaps.Tight);
    }

    /// <summary>The link's words, naming the action offered and its keys, or null while none is.</summary>
    public string? Offered => link.IsVisible ? link.Content as string : null;

    /// <summary>The link pressed, naming the action it offers.</summary>
    public event EventHandler<string>? ActionRequested;
}
