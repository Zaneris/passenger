using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A dialog's buttons at its foot, at its right, in the order each platform lays them in, and what goes beside
/// them (Don't ask again, a problem the page has) at the row's left in every dialog. Shell.axaml's
/// <c>StackPanel.footer</c> spaces the buttons and gives each one minimum width.</summary>
public static class DialogFooter
{
    /// <summary>Marks a dialog's page, which then stands as far in from the window's edges and above the footer's bar as
    /// the footer's buttons stand in from its sides (the theme's DialogFooterPadding), so the page's fields end where the
    /// default button does, in every dialog and under every theme.</summary>
    public static readonly AttachedProperty<bool> InsetProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Inset", typeof(DialogFooter));

    static DialogFooter() => InsetProperty.Changed.AddClassHandler<Control>((page, e) =>
    {
        if (e.NewValue is true)
        {
            BySides(page, Layoutable.MarginProperty, null, null);
        }
    });

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
        StackPanel answers = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Normal };
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
            left.Margin = new Thickness(0, 0, Gaps.Group, 0);
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
            Spacing = Gaps.Tight,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, about },
        };
        BySides(header, Layoutable.MarginProperty, null, Gaps.Wide);
        Border top = new() { BorderThickness = Hairlines.Below, Child = header };
        top.Bind(Border.BorderBrushProperty, top.GetResourceObservable(TokenKeys.Divider));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel content = Page(new Border { [InsetProperty] = true, Child = page }, bar);
        content.Children.Insert(0, top);
        return content;
    }

    /// <summary>How wide a question is (<see cref="Question"/>).</summary>
    public const double QuestionWidth = 520;

    /// <summary>A question's whole window, laid out one way: its title in bold over its paragraphs (words from
    /// <see cref="Paragraph"/>, or what it is about, as a list), each wrapping, at one width and as tall as they are; its
    /// answers in the footer's bar, and what goes beside them (Don't ask again) at the bar's left. Its words are the
    /// window's description too, for a screen reader to say with its title.</summary>
    public static void Question(Window dialog, string title, IEnumerable<Control> paragraphs, StackPanel answers, Control? left = null)
    {
        Control[] said = [.. paragraphs];
        AutomationProperties.SetHelpText(dialog, string.Join(" ", said.OfType<TextBlock>().Select(words => words.Text)));
        dialog.Title = title;
        dialog.Width = QuestionWidth;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.CanResize = false;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StackPanel page = new() { [InsetProperty] = true, Spacing = Gaps.Wide };
        page.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        page.Children.AddRange(said);
        // On a screen too short for its words, they scroll above the answers, which stay in reach.
        dialog.Content = Page(new ScrollViewer { Content = page }, Bar(answers, left));
    }

    /// <summary>A question's Don't ask again, which goes beside its answers (<see cref="Question"/>'s left): ticked as the
    /// person agrees, the asker keeps the answer in Settings.</summary>
    public static CheckBox DontAskAgain() => new() { Content = "Don't ask again" };

    /// <summary>A question's paragraph of words, wrapping to its width.</summary>
    public static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    /// <summary>Gives a part of a dialog the footer's sides, its top and foot those sides too unless given.</summary>
    private static void BySides(Control part, StyledProperty<Thickness> property, double? top, double? bottom) =>
        part.Bind(property, part.GetResourceObservable(TokenKeys.DialogFooterPadding, padding => padding is Thickness footer
            ? new Thickness(footer.Left, top ?? footer.Left, footer.Right, bottom ?? footer.Left)
            : new Thickness(Gaps.Group)));

    private static IEnumerable<Button> Ordered(Button chosen, Button? cancel, Button[] more) =>
        (OperatingSystem.IsMacOS() ? [cancel, .. more, chosen] : (Button?[])[chosen, cancel, .. more]).OfType<Button>();

    private static StackPanel Buttons(IEnumerable<Button> buttons)
    {
        StackPanel footer = new() { Classes = { StyleClasses.Footer } };
        footer.Children.AddRange(buttons);
        return footer;
    }
}
