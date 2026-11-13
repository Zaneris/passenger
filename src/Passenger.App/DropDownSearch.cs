using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Every drop-down's search, installed once for all: in a list longer than <see cref="LongList"/> (the fonts, the
/// themes), letters typed open it and narrow it to the items whose words hold them, said at its top, Backspace taking the
/// last back and Escape all of them before it closes the list; the first item left takes the keys, Enter choosing it. A
/// shorter list keeps Fluent's jump to the item a letter starts. An item's words are what it shows itself as (its
/// ToString, a ComboBoxItem's content).</summary>
public static class DropDownSearch
{
    /// <summary>The most items a list has that a letter only jumps in.</summary>
    public const int LongList = 12;

    private static readonly ConditionalWeakTable<ComboBox, Search> Searches = [];

    public static void Install()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ComboBox>(Fitted);
        InputElement.TextInputEvent.AddClassHandler<ComboBox>(Typed, RoutingStrategies.Tunnel);
        InputElement.KeyDownEvent.AddClassHandler<ComboBox>(Pressed, RoutingStrategies.Tunnel);
        ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>((box, e) =>
        {
            if (e.NewValue is false && Searches.TryGetValue(box, out Search? search))
            {
                search.Narrow(string.Empty);
            }
        });
    }

    /// <summary>What a drop-down's search holds now; empty while none is typed.</summary>
    public static string Query(ComboBox box) => Searches.TryGetValue(box, out Search? search) ? search.Typed : string.Empty;

    /// <summary>The words over the list where its search is said, put at the top of the list's popup as its template is
    /// applied, and the list's items hearing the search as they are made.</summary>
    private static void Fitted(ComboBox box, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<Popup>("PART_Popup") is not { Child: Border { Child: Control list } border })
        {
            return;
        }
        Search search = Searches.GetValue(box, made => new Search(made));
        search.List = list;
        border.Child = null;
        DockPanel.SetDock(search.Said, Dock.Top);
        (search.Said.Parent as Panel)?.Children.Remove(search.Said);
        border.Child = new DockPanel { Children = { search.Said, list } };
    }

    private static void Typed(ComboBox box, TextInputEventArgs e)
    {
        // An editable drop-down's own field takes what is typed.
        if (e.Text is not { Length: > 0 } text || box.IsEditable || box.ItemCount <= LongList || text.Any(char.IsControl) || !Searches.TryGetValue(box, out Search? search))
        {
            return;
        }
        if (search.Typed.Length == 0 && string.IsNullOrWhiteSpace(text))
        {
            // A space before any letter opens or chooses, as Fluent's own.
            return;
        }
        box.IsDropDownOpen = true;
        search.Narrow(search.Typed + text);
        e.Handled = true;
    }

    private static void Pressed(ComboBox box, KeyEventArgs e)
    {
        if (!Searches.TryGetValue(box, out Search? search) || search.Typed.Length == 0 || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Back:
                search.Narrow(search.Typed[..^1]);
                e.Handled = true;
                break;
            case Key.Escape:
                search.Narrow(string.Empty);
                e.Handled = true;
                break;
        }
    }

    /// <summary>One drop-down's search: what is typed, the words saying it, and its items shown as they match it.</summary>
    private sealed class Search
    {
        private readonly ComboBox box;

        public Search(ComboBox box)
        {
            this.box = box;
            Said.Classes.Add(StyleClasses.Muted);
            box.ContainerPrepared += (_, e) => e.Container.IsVisible = Matches(e.Index);
        }

        public string Typed { get; private set; } = string.Empty;

        /// <summary>What holds the list's rows in its popup, scrolled to its top as the list narrows.</summary>
        public Control? List { get; set; }

        public TextBlock Said { get; } = new() { IsVisible = false, Margin = new Thickness(10, 6, 10, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        /// <summary>The list narrowed to what is typed, the first item left given the keys; nothing typed shows every item.</summary>
        public void Narrow(string typed)
        {
            Typed = typed;
            int left = 0;
            int first = -1;
            for (int i = 0; i < box.ItemCount; i++)
            {
                bool shown = Matches(i);
                if (shown)
                {
                    left++;
                    first = first < 0 ? i : first;
                }
                if (box.ContainerFromIndex(i) is { } container)
                {
                    container.IsVisible = shown;
                }
            }
            Said.IsVisible = typed.Length > 0;
            Said.Text = left == 0 ? $"Nothing holds “{typed}”" : $"“{typed}”: {Plural.Of(left, "match", "matches")}";
            if (typed.Length > 0 && first >= 0)
            {
                // From the top, so the rows hidden before the first left are laid out at no height, not left as a gap.
                if (List is ScrollViewer scroller)
                {
                    scroller.Offset = default;
                    scroller.UpdateLayout();
                }
                box.ContainerFromIndex(first)?.Focus(NavigationMethod.Directional);
            }
        }

        private bool Matches(int index) =>
            Typed.Length == 0 || (box.Items[index] is ContentControl { Content: var content } ? content : box.Items[index])?.ToString()?.Contains(Typed, StringComparison.CurrentCultureIgnoreCase) == true;
    }
}
