using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Templates;

namespace Passenger.Tests;

/// <summary>A dialog's buttons as a person sees them: each one's words centred in it, unless it reads as text (a link,
/// a crumb); a dialog's text buttons, the default among them, one height and one corner radius; the default button and
/// Cancel in the platform's order, the default rightmost on macOS and first elsewhere. <see cref="Check"/> is the rule,
/// which the tests that open the other dialogs (a refactoring's, Conflicts, a feed's) hold their dialogs to as
/// well.</summary>
public sealed class DialogButtonsTests : IDisposable
{
    /// <summary>The buttons that read as text, whose words stand at their left as a line of text's do.</summary>
    private static readonly string[] ReadAsText = ["link", "crumb", "statusItem"];

    private readonly string folder = TestFolders.Create("dialog-buttons-");
    private readonly string other = TestFolders.Create("dialog-buttons-other-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(other);
    }

    [AvaloniaTheory]
    [InlineData(Look.Passenger)]
    [InlineData(Look.Harbour)]
    public void Every_dialog_has_its_buttons_words_centred_and_its_text_buttons_alike_with_the_default_where_Rider_puts_it(Look look)
    {
        using IDisposable worn = UiRulesTests.Wearing(look);
        // A solution, which Add Aspire Orchestration adds to; no C# language server, which no dialog here asks of.
        File.WriteAllText(Path.Combine(folder, "Dialogs.slnx"), "<Solution />\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.Perform(AppActions.About);
            Closed(Opened<AboutDialog>(window));

            SettingsDialog settings = window.ShowSettings();
            Ui.Settle();
            Check(settings);
            settings.Page = SettingsDialog.KeymapPage;
            Ui.Settle();
            Ui.ChooseFromMenu(settings.KeymapRow(AppActions.SplitRight), "Add Keyboard Shortcut");
            Ui.WaitFor(() => settings.OwnedWindows.OfType<KeyboardShortcutDialog>().Any(w => w.IsVisible), "the Keyboard Shortcut dialog");
            Check(settings.OwnedWindows.OfType<KeyboardShortcutDialog>().Single(w => w.IsVisible)).Close();
            Ui.Settle();
            Check(settings).Close();
            Ui.Settle();

            window.Perform(AppActions.NewSolution);
            NewProjectDialog solution = Opened<NewProjectDialog>(window);
            Ui.WaitFor(() => solution.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
            Closed(solution);

            window.Perform(AppActions.EditConfigurations);
            Ui.Settle();
            Closed(window.RunConfigurationsDialog ?? throw new Xunit.Sdk.XunitException("Edit Configurations opened no dialog"));

            window.Perform(AppActions.AttachToProcess);
            AttachToProcessDialog attach = Opened<AttachToProcessDialog>(window);
            Ui.WaitFor(() => attach.Listed.IsCompleted, "Attach to Process to list the .NET programs");
            Closed(attach);

            Closed(window.ShowCloneRepository());

            Assert.True(window.Perform(AppActions.AddAspireOrchestration), $"Add Aspire Orchestration could not run: {window.Status}");
            Closed(Opened<Passenger.App.Aspire.AspireOrchestrationDialog>(window));

            // The Open Solution question, as another folder opens over the one open.
            window.Open(other);
            Ui.Settle();
            Closed(window.Asking ?? throw new Xunit.Sdk.XunitException("Opening another folder over one open did not ask where"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Holds a dialog's buttons to the rule: each one shown has its words (or icon) centred in it unless it
    /// reads as text; its text buttons (plain or the default) are one height and one corner radius; and the default
    /// button and Cancel, when it has both, stand in the platform's order. Returns the dialog.</summary>
    public static T Check<T>(T dialog) where T : Window
    {
        Ui.Settle();
        string title = dialog.Title ?? dialog.GetType().Name;
        Button[] shown = [.. dialog.GetVisualDescendants().OfType<Button>()
            .Where(b => b is not ToggleButton and not RepeatButton && b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0)];
        foreach (Button button in shown.Where(b => !b.Classes.Any(ReadAsText.Contains)))
        {
            ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            if (presenter.Child is not { } content || content.DesiredSize.Width <= 0)
            {
                continue;
            }
            // Where its words are laid out: a text stretched across the button still draws them from its left.
            double left = presenter.Bounds.X + content.Bounds.X;
            double right = button.Bounds.Width - left - Math.Min(content.DesiredSize.Width, content.Bounds.Width);
            Assert.True(Math.Abs(left - right) <= 1.5,
                $"{Name(button)} in {title} has its words {left:0.#} px from its left edge and {right:0.#} px from its right: they are not centred in the button");
        }

        // A button that asks more before it acts ends in the one ellipsis, never three dots.
        Assert.False(title.Contains("...", StringComparison.Ordinal), $"The dialog's title '{title}' ends in three dots, where every label has the one ellipsis, '…'");
        foreach (Button button in shown.Where(b => b.Content is string words && words.Contains("...", StringComparison.Ordinal)))
        {
            Assert.Fail($"{Name(button)} in {title} has three dots, where every label has the one ellipsis, '…'");
        }

        Button[] text = [.. shown.Where(b => b.Content is string && b.Classes.Where(c => !c.StartsWith(':')).All(c => c == "primary"))];
        foreach (Button button in text.Skip(1))
        {
            Button first = text[0];
            Assert.True(Math.Abs(button.Bounds.Height - first.Bounds.Height) < 0.5 && button.CornerRadius == first.CornerRadius,
                $"In {title}, {Name(first)} is {first.Bounds.Height:0.#} px tall with corners of {first.CornerRadius.TopLeft} while {Name(button)} "
                + $"is {button.Bounds.Height:0.#} px with corners of {button.CornerRadius.TopLeft}: alike buttons beside each other differ");
        }

        if (shown.FirstOrDefault(b => b.IsDefault) is { } chosen && shown.FirstOrDefault(b => b.IsCancel && !b.IsDefault) is { } cancel)
        {
            double chosenAt = chosen.TranslatePoint(default, dialog)!.Value.X;
            double cancelAt = cancel.TranslatePoint(default, dialog)!.Value.X;
            Assert.True(OperatingSystem.IsMacOS() ? chosenAt > cancelAt : chosenAt < cancelAt,
                $"In {title}, {Name(chosen)} stands {(chosenAt > cancelAt ? "right" : "left")} of {Name(cancel)}, where Rider puts the default "
                + (OperatingSystem.IsMacOS() ? "rightmost, after Cancel" : "first, before Cancel"));
        }

        // A plain button: its own ground (white in light) inside a field's edge.
        Color plain = Token(dialog, TokenKeys.Button);
        foreach (Button button in text.Where(b => !b.Classes.Contains("primary") && b.IsEffectivelyEnabled))
        {
            ContentPresenter frame = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.True(frame.Background is ISolidColorBrush ground && ground.Color == plain && frame.BorderThickness.Left > 0,
                $"In {title}, {Name(button)} is drawn on {(frame.Background as ISolidColorBrush)?.Color} with an edge of {frame.BorderThickness}, where Rider's plain button has its own ground and a field's edge");
        }

        // A default button that cannot be used looks disabled, not like the default it would be.
        Color accent = Token(dialog, TokenKeys.Accent);
        foreach (Button idle in shown.Where(b => (b.IsDefault || b.Classes.Contains("primary")) && !b.IsEffectivelyEnabled))
        {
            ContentPresenter frame = idle.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.False(frame.Background is ISolidColorBrush ground && ground.Color == accent,
                $"In {title}, {Name(idle)} cannot be used now yet keeps the default button's accent, where Rider's looks disabled");
        }

        // The page is a tool window's ground; a list framed in it stands on the list's (Border.framed).
        Assert.True(dialog.Background is ISolidColorBrush page && page.Color == Token(dialog, TokenKeys.Panel),
            $"{title}'s page is not a tool window's ground, as every dialog's page is");
        // A dialog's buttons stand on its footer's bar, never straight on its page.
        foreach (StackPanel footer in dialog.GetVisualDescendants().OfType<StackPanel>().Where(s => s.Classes.Contains("footer") && s.IsEffectivelyVisible))
        {
            Assert.True(footer.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("footerBar")),
                $"{title}'s buttons ({string.Join(", ", footer.Children.OfType<Button>().Select(Name))}) stand on its page, not on the footer's bar every dialog has");
        }
        // The footer stands on the frame's ground, told from the page by shade, with no rule between them.
        foreach (Border bar in dialog.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("footerBar") && b.IsEffectivelyVisible))
        {
            Assert.True(bar.BorderThickness == default && bar.Background is ISolidColorBrush ground && ground.Color == Token(dialog, TokenKeys.Frame),
                $"{title}'s footer is drawn on {(bar.Background as ISolidColorBrush)?.Color} with a rule of {bar.BorderThickness}, where a footer is the frame's ground and no rule");
        }
        Color editor = Token(dialog, TokenKeys.Editor_Background);
        double area = dialog.Bounds.Width * dialog.Bounds.Height;
        foreach (Border pane in dialog.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Background is ISolidColorBrush ground && ground.Color == editor
            && b.TemplatedParent is null && !b.Classes.Contains("framed") && b.FindAncestorOfType<Passenger.Editor.CodeEditor>() is null))
        {
            Assert.True(pane.Bounds.Width * pane.Bounds.Height < area / 3,
                $"{title} lays a third of its page on the editor's ground, where its page is the chrome");
        }

        // The drop-downs of a column (starting at one place) are one width, their right edges in line.
        foreach (IGrouping<double, ComboBox> column in dialog.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0)
            .GroupBy(c => Math.Round(c.TranslatePoint(default, dialog)!.Value.X)))
        {
            ComboBox first = column.First();
            ComboBox? other = column.FirstOrDefault(c => Math.Abs(c.Bounds.Width - first.Bounds.Width) >= 0.5);
            Assert.True(other is null, $"In {title}, drop-downs of one column are {first.Bounds.Width:0.#} and {other?.Bounds.Width:0.#} px wide, where a form's are one width");
        }

        // Don't ask again stands at the footer's left, beside the buttons.
        foreach (CheckBox ask in dialog.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible && c.Content is string words
            && (words.StartsWith("Don't ask", StringComparison.Ordinal) || words.StartsWith("Do not ask", StringComparison.Ordinal))))
        {
            StackPanel? footer = ask.GetVisualParent() is DockPanel row && row.Classes.Contains("footerRow")
                ? row.Children.OfType<StackPanel>().FirstOrDefault(s => s.Classes.Contains("footer")) : null;
            Assert.True(footer is not null, $"In {title}, '{ask.Content}' is not in the footer's row beside the buttons, as Rider puts it");
            Rect box = new(ask.TranslatePoint(default, dialog)!.Value, ask.Bounds.Size);
            Rect buttons = new(footer!.TranslatePoint(default, dialog)!.Value, footer.Bounds.Size);
            Assert.True(box.Right <= buttons.Left && Math.Abs(box.Center.Y - buttons.Center.Y) < 1.5,
                $"In {title}, '{ask.Content}' is not at the footer's left, level with the buttons");
        }
        return dialog;
    }

    private static Color Token(Window dialog, string key) =>
        dialog.TryFindResource(key, dialog.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;

    private static string Name(Button button) => button.Content is string words ? $"'{words}'" : ToolTip.GetTip(button) is string tip ? $"'{tip}'" : "a button";

    private static T Opened<T>(MainWindow window) where T : Window
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<T>().Any(w => w.IsVisible), typeof(T).Name);
        return window.OwnedWindows.OfType<T>().Single(w => w.IsVisible);
    }

    /// <summary>Holds the dialog to the rule, then closes it.</summary>
    private static void Closed(Window dialog)
    {
        Check(dialog);
        dialog.Close();
        Ui.Settle();
    }
}
