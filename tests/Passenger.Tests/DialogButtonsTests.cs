using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Templates;
using Passenger.App.Vcs;

namespace Passenger.Tests;

/// <summary>A dialog's buttons as a person sees them (#213): each one's words centred in it, unless it reads as text (a
/// link, a crumb); a dialog's text buttons, the default among them, one height and one corner radius; the default button
/// and Cancel where Rider puts them, the default rightmost on macOS and first elsewhere. <see cref="Check"/> is the rule,
/// which the tests that open the other dialogs (a refactoring's, Conflicts, a feed's) hold their dialogs to as well.</summary>
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
    [InlineData(false)]
    [InlineData(true)]
    public void Every_dialog_has_its_buttons_words_centred_and_its_text_buttons_alike_with_the_default_where_Rider_puts_it(bool light)
    {
        ThemeVariant? before = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
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

            // Rider's Open Solution question, as another folder opens over the one open.
            window.Open(other);
            Ui.Settle();
            Closed(window.Asking ?? throw new Xunit.Sdk.XunitException("Opening another folder over one open did not ask where"));
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = before;
        }
    }

    /// <summary>Holds a dialog's buttons to the rule: each one shown has its words (or icon) centred in it unless it reads
    /// as text; its text buttons (plain or the default) are one height and one corner radius; and the default button
    /// and Cancel, when it has both, stand where Rider's do. Returns the dialog.</summary>
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
            // Where its words are laid out (an icon turned in place keeps its box): a text stretched across the button
            // still draws them from its left.
            double left = presenter.Bounds.X + content.Bounds.X;
            double right = button.Bounds.Width - left - Math.Min(content.DesiredSize.Width, content.Bounds.Width);
            Assert.True(Math.Abs(left - right) <= 1.5,
                $"{Name(button)} in {title} has its words {left:0.#} px from its left edge and {right:0.#} px from its right: they are not centred in the button");
        }

        Button[] text = [.. shown.Where(b => b.Content is string && b.Classes.Where(c => !c.StartsWith(':')).All(c => c == "primary"))];
        foreach (Button button in text.Skip(1))
        {
            Button first = text[0];
            Assert.True(Math.Abs(button.Bounds.Height - first.Bounds.Height) < 0.5 && button.CornerRadius == first.CornerRadius,
                $"In {title}, {Name(first)} is {first.Bounds.Height:0.#} px tall with corners of {first.CornerRadius.TopLeft} while {Name(button)} "
                + $"is {button.Bounds.Height:0.#} px with corners of {button.CornerRadius.TopLeft}: alike buttons beside each other differ");
        }
        foreach (Button button in text.Where(b => b.IsEffectivelyEnabled && !b.Classes.Contains("primary") && !b.IsPointerOver))
        {
            if (dialog.ActualThemeVariant == ThemeVariant.Light)
            {
                ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                Assert.True(presenter.Background is ISolidColorBrush painted && painted.Color == Colors.White
                    && presenter.BorderBrush is not null && presenter.BorderThickness == new Thickness(1),
                    $"{Name(button)} in {title} must be white with a border in the light theme, rather than Fluent's grey fill");
            }
        }

        if (shown.FirstOrDefault(b => b.IsDefault) is { } chosen && shown.FirstOrDefault(b => b.IsCancel && !b.IsDefault) is { } cancel)
        {
            double chosenAt = chosen.TranslatePoint(default, dialog)!.Value.X;
            double cancelAt = cancel.TranslatePoint(default, dialog)!.Value.X;
            Assert.True(OperatingSystem.IsMacOS() ? chosenAt > cancelAt : chosenAt < cancelAt,
                $"In {title}, {Name(chosen)} stands {(chosenAt > cancelAt ? "right" : "left")} of {Name(cancel)}, where Rider puts the default "
                + (OperatingSystem.IsMacOS() ? "rightmost, after Cancel" : "first, before Cancel"));

            if (dialog.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.IsEffectivelyVisible && c.Content is "Don't ask again") is { } remember)
            {
                Point option = remember.TranslatePoint(default, dialog)!.Value;
                Point button = chosen.TranslatePoint(default, dialog)!.Value;
                double firstButton = shown.Min(b => b.TranslatePoint(default, dialog)!.Value.X);
                Assert.True(option.X + remember.Bounds.Width + 8 <= firstButton
                    && Math.Abs(option.Y + remember.Bounds.Height / 2 - button.Y - chosen.Bounds.Height / 2) <= 1,
                    $"In {title}, Don't ask again must sit at the footer's left, on the same row as the buttons");
            }
        }
        return dialog;
    }

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
