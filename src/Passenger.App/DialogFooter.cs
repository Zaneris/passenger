using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A dialog's buttons at its foot, at its right, in the order each platform lays them in, and what goes beside
/// them (Don't ask again, a problem the page has) at the row's left in every dialog. Shell.axaml's
/// <c>StackPanel.footer</c> spaces the buttons and gives each one minimum width.</summary>
public static class DialogFooter
{
    /// <summary>A dialog's default button, OK unless its words say what it does (Next, Publish, Create): Enter clicks it,
    /// and it wears the accent.</summary>
    public static Button Default(string words = "OK") => new() { Content = words, IsDefault = true, Classes = { StyleClasses.Primary } };

    /// <summary>A dialog's Cancel, or the answer that stands for it (No): Escape clicks it too, and a click does what
    /// <paramref name="cancelled"/> does.</summary>
    public static Button Cancel(Action cancelled, string words = "Cancel")
    {
        Button cancel = new() { Content = words, IsCancel = true };
        cancel.Click += (_, _) => cancelled();
        return cancel;
    }

    /// <summary>The footer's bar of a dialog its default button and Cancel answer (<see cref="Of"/>'s order, Apply after
    /// them), and what goes beside them at its left (a problem the page has).</summary>
    public static Border OkCancel(Button chosen, Action cancelled, Control? left = null, params Button[] more) =>
        Bar(Of(chosen, Cancel(cancelled), more), left);

    /// <summary>A dialog's OK, Cancel and Apply: the default first and Cancel after it on Windows and Linux, the default
    /// rightmost with Cancel beside it on macOS, any other (Apply) after Cancel.</summary>
    public static StackPanel Of(Button chosen, Button? cancel, params Button[] more) => Buttons(Ordered(chosen, cancel, more));

    /// <summary>A popup's default and Cancel (the colour picker's, Add CLR Exception Breakpoints'), side by side in
    /// <see cref="Of"/>'s order, each as wide as its words. Escape is the popup's own to handle: the window's keys are
    /// its keys too, so its Cancel takes none of them.</summary>
    public static StackPanel Popup(Button chosen, Action cancelled)
    {
        Button cancel = new() { Content = "Cancel" };
        cancel.Click += (_, _) => cancelled();
        StackPanel answers = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        answers.Children.AddRange(Ordered(chosen, cancel, []));
        return answers;
    }

    /// <summary>Buttons in the order given on Windows and Linux, and on macOS the same with the default moved to the
    /// end, as Reload, Convert, Cancel stand.</summary>
    public static StackPanel Listed(params Button?[] buttons)
    {
        Button[] shown = [.. buttons.OfType<Button>()];
        return Buttons(OperatingSystem.IsMacOS() ? [.. shown.Where(b => !b.IsDefault), .. shown.Where(b => b.IsDefault)] : shown);
    }

    /// <summary>A question's answers: in the order asked, Cancel last, on Windows and Linux, and the whole row reversed
    /// on macOS, as This Window, New Window, Cancel stand.</summary>
    public static StackPanel Answers(params Button[] answers) => Buttons(OperatingSystem.IsMacOS() ? [.. answers.Reverse()] : answers);

    /// <summary>The footer's row: its buttons at the right, and at the left what goes beside them.</summary>
    public static DockPanel Row(StackPanel buttons, Control? left = null)
    {
        DockPanel row = new();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        if (left is not null)
        {
            left.VerticalAlignment = VerticalAlignment.Center;
            left.Margin = new Thickness(0, 0, 16, 0);
            row.Children.Add(left);
        }
        return row;
    }

    /// <summary>The bar under a dialog's page that its footer's row stands in (Shell.axaml's <c>Border.footerBar</c>).</summary>
    public static Border Bar(StackPanel buttons, Control? left = null) => new() { Classes = { StyleClasses.FooterBar }, Child = Row(buttons, left) };

    /// <summary>A dialog's whole content: its page, and under it the footer's bar across the window.</summary>
    public static DockPanel Page(Control page, Border bar)
    {
        DockPanel.SetDock(bar, Dock.Bottom);
        return new DockPanel { Children = { bar, page } };
    }

    /// <summary>A step's whole content (a refactoring's, Publish's): its title in bold over what it does, on a band ruled
    /// off from the page; the page under it; and the footer's bar, inset as the page is, so the default button ends where
    /// the page's fields do and a problem starts where its labels do.</summary>
    public static DockPanel Headed(string title, Control about, Control page, Border bar)
    {
        StackPanel header = new()
        {
            Margin = new Thickness(20, 16, 20, 12),
            Spacing = 4,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, about },
        };
        Border top = new() { BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        top.Bind(Border.BorderBrushProperty, top.GetResourceObservable(TokenKeys.Divider));
        DockPanel.SetDock(top, Dock.Top);
        bar.Padding = new Thickness(20, 10);
        DockPanel content = Page(new Border { Padding = new Thickness(20, 14, 20, 16), Child = page }, bar);
        content.Children.Insert(0, top);
        return content;
    }

    /// <summary>How wide a question is (<see cref="Question"/>).</summary>
    public const double QuestionWidth = 520;

    /// <summary>A question's whole window, laid out one way: its title in bold over its paragraphs (words from
    /// <see cref="Paragraph"/>, or what it is about, as a list), each wrapping, at one width and as tall as they are; its
    /// answers in the footer's bar, and what goes beside them (Don't ask again) at the bar's left.</summary>
    public static void Question(Window dialog, string title, IEnumerable<Control> paragraphs, StackPanel answers, Control? left = null)
    {
        dialog.Title = title;
        dialog.Width = QuestionWidth;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.CanResize = false;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StackPanel page = new() { Margin = new Thickness(20), Spacing = 12 };
        page.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        page.Children.AddRange(paragraphs);
        // On a screen too short for its words, they scroll above the answers, which stay in reach.
        dialog.Content = Page(new ScrollViewer { Content = page }, Bar(answers, left));
    }

    /// <summary>A question's paragraph of words, wrapping to its width.</summary>
    public static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static IEnumerable<Button> Ordered(Button chosen, Button? cancel, Button[] more) =>
        (OperatingSystem.IsMacOS() ? [cancel, .. more, chosen] : (Button?[])[chosen, cancel, .. more]).OfType<Button>();

    private static StackPanel Buttons(IEnumerable<Button> buttons)
    {
        StackPanel footer = new() { Classes = { StyleClasses.Footer } };
        footer.Children.AddRange(buttons);
        return footer;
    }
}
