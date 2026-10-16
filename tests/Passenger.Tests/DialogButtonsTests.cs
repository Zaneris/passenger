using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    private static int captureNumber;
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
    public void Keyboard_focus_surrounds_the_actual_checkbox_with_a_centered_rounded_non_intercepting_ring(bool light)
    {
        MainWindow window = new() { Width = 683, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show();
        window.OpenFolder(folder);
        try
        {
            window.Open(other);
            OpenProjectDialog dialog = Opened<OpenProjectDialog>(window);
            dialog.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            dialog.SetRenderScaling(2);
            CheckBox option = Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>(), check => Equals(check.Content, "Don't ask again"));
            for (int attempt = 0; attempt < 20 && !option.IsFocused; attempt++)
            {
                Ui.Press(dialog, "tab");
            }
            Assert.True(option.IsFocused, "The footer checkbox must be reachable with the actual Tab key");
            UiAudit.Capture(dialog, $"checkbox-focus-{(light ? "light" : "dark")}");
            Border ring = CheckGlyphRing(dialog, option);
            bool before = option.IsChecked == true;
            Button primary = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => button.IsDefault);
            int beforeInk = ButtonInk(dialog, primary);
            Assert.True(beforeInk > 20, "The primary action must paint its label before keyboard focus moves to it");
            Ui.Press(dialog, "space");
            Assert.Equal(!before, option.IsChecked);
            Assert.True(option.IsFocused);
            UiAudit.Capture(dialog, $"checkbox-focus-checked-{(light ? "light" : "dark")}");
            Point at = option.TranslatePoint(new Point(10, option.Bounds.Height / 2), dialog)!.Value;
            Control hit = Assert.IsAssignableFrom<Control>(dialog.InputHitTest(at));
            Assert.True(hit == option || hit.GetVisualAncestors().Contains(option), "The focus ring must leave the real checkbox under its pointer target");
            dialog.MouseDown(at, MouseButton.Left);
            dialog.MouseUp(at, MouseButton.Left);
            Ui.Settle();
            Assert.Equal(before, option.IsChecked);
            for (int attempt = 0; attempt < 10 && !primary.IsFocused; attempt++)
            {
                Ui.Press(dialog, "tab");
            }
            Assert.DoesNotContain(ring, dialog.GetVisualDescendants());
            Button next = Assert.IsAssignableFrom<Button>(dialog.FocusManager!.GetFocusedElement());
            Assert.Same(primary, next);
            Border buttonRing = Assert.Single(dialog.GetVisualDescendants().OfType<Border>(), border => border.Name == "ControlFocusRing");
            Assert.True(buttonRing.CornerRadius.TopLeft > next.CornerRadius.TopLeft);
            Assert.Equal(new Size(next.Bounds.Width + 6, next.Bounds.Height + 6), buttonRing.Bounds.Size);
            Assert.Equal(new Point(-3, -3), buttonRing.TranslatePoint(default, next));
            Assert.False(buttonRing.IsHitTestVisible);
            Assert.Equal(Colour(dialog, "Passenger.Accent"), Ui.Pixel(dialog, buttonRing.TranslatePoint(new Point(0.5, buttonRing.Bounds.Height / 2), dialog)!.Value));
            Assert.True(buttonRing.Background is null || Solid(buttonRing.Background).A == 0, "A decorative focus outline must leave the action's label visible");
            int focusedInk = ButtonInk(dialog, primary);
            Assert.True(focusedInk >= beforeInk * 0.8, $"Keyboard focus must retain the primary label's actual painted ink: {beforeInk} before, {focusedInk} focused");
            UiAudit.Capture(dialog, $"button-focus-{(light ? "light" : "dark")}");
            // An opaque decorative fill would cover this very label: prove the rendered check detects that failure.
            IBrush? clear = buttonRing.Background;
            int coveredInk;
            try
            {
                buttonRing.Background = primary.Background;
                Ui.Settle();
                coveredInk = ButtonInk(dialog, primary);
                Assert.True(coveredInk < beforeInk * 0.2, "The rendered label check must detect an opaque adorner covering it");
            }
            finally
            {
                buttonRing.Background = clear;
                Ui.Settle();
            }
            Assert.True(ButtonInk(dialog, primary) >= beforeInk * 0.8);
            TestContext.Current.TestOutputHelper?.WriteLine($"Primary label ink: before={beforeInk}, focused={focusedInk}, opaque-adornment control={coveredInk}");
            for (int attempt = 0; attempt < 20 && !option.IsFocused; attempt++)
            {
                Ui.Press(dialog, "tab");
            }
            Assert.True(option.IsFocused);
            option.IsEnabled = false;
            Ui.Settle();
            Assert.False(option.IsFocused);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Border>(), border => border.Name == "CheckboxFocusRing");
            UiAudit.Capture(dialog, $"checkbox-disabled-{(light ? "light" : "dark")}");
            dialog.Close();
            Ui.Settle();

            SettingsDialog settings = window.ShowSettings();
            settings.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            settings.SetRenderScaling(2);
            settings.Query = "wrapped fragments";
            settings.Width = 640;
            Ui.Settle();
            CheckBox wrapped = Assert.Single(settings.GetVisualDescendants().OfType<CheckBox>(), check => check.IsEffectivelyVisible);
            Assert.True(wrapped.Bounds.Height > 28, "The real Settings label must wrap at its default font size for this focus alignment check");
            for (int attempt = 0; attempt < 20 && !wrapped.IsFocused; attempt++)
            {
                Ui.Press(settings, "tab");
            }
            Assert.True(wrapped.IsFocused);
            foreach (double width in new double[] { 640, 980, 683, 640 })
            {
                settings.Width = width;
                Ui.Settle();
                Assert.True(wrapped.IsFocused, "The same Settings checkbox must retain its keys through wrapping and column changes");
                CheckGlyphRing(settings, wrapped);
                UiAudit.Capture(settings, $"checkbox-wrapped-{width:0}-{(light ? "light" : "dark")}");
            }
            settings.Close();
        }
        finally
        {
            window.Close();
        }
    }

    private static Border CheckGlyphRing(Window dialog, CheckBox option)
    {
        Border ring = Assert.Single(dialog.GetVisualDescendants().OfType<Border>(), border => border.Name == "CheckboxFocusRing");
        Border glyph = Assert.Single(option.GetVisualDescendants().OfType<Border>(), border => border.Name == "NormalRectangle");
        Assert.True(ring.CornerRadius.TopLeft > glyph.CornerRadius.TopLeft);
        Assert.Equal(new Size(glyph.Bounds.Width + 6, glyph.Bounds.Height + 6), ring.Bounds.Size);
        Assert.Equal(new Point(-3, -3), ring.TranslatePoint(default, glyph));
        Assert.Equal(Colour(dialog, "Passenger.Accent"), Solid(ring.BorderBrush));
        Assert.Equal(Colour(dialog, "Passenger.Accent"), Ui.Pixel(dialog, ring.TranslatePoint(new Point(0.5, ring.Bounds.Height / 2), dialog)!.Value));
        Assert.False(ring.IsHitTestVisible, "A focus ring must not intercept checkbox clicks");
        return ring;
    }

    private static int ButtonInk(Window window, Button button)
    {
        TextBlock label = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(), text => Equals(text.Text, button.Content));
        Color ink = Solid(label.Foreground);
        using Avalonia.Media.Imaging.WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The dialog has no rendered frame");
        PixelPoint at = PixelPoint.FromPoint(label.TranslatePoint(default, window)!.Value, window.RenderScaling);
        int width = (int)Math.Ceiling(label.Bounds.Width * window.RenderScaling);
        int height = (int)Math.Ceiling(label.Bounds.Height * window.RenderScaling);
        byte[] pixels = new byte[width * height * 4];
        System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(at.X, at.Y, width, height), pinned.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            pinned.Free();
        }
        int red = frame.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? 0 : 2;
        int blue = red == 0 ? 2 : 0;
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (Math.Abs(pixels[offset + red] - ink.R) + Math.Abs(pixels[offset + 1] - ink.G) + Math.Abs(pixels[offset + blue] - ink.B) <= 15)
            {
                count++;
            }
        }
        return count;
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
            settings.Page = SettingsDialog.LiveTemplatesPage;
            Ui.Settle();
            Assert.True(settings.Templates.Choose("cw"));
            Button availability = settings.Templates.AvailabilityButton;
            Point availabilityAt = availability.TranslatePoint(new Point(availability.Bounds.Width / 2, availability.Bounds.Height / 2), settings)!.Value;
            settings.MouseDown(availabilityAt, MouseButton.Left);
            settings.MouseUp(availabilityAt, MouseButton.Left);
            Ui.WaitFor(() => settings.OwnedWindows.OfType<AvailabilityDialog>().Any(w => w.IsVisible), "the Availability configuration dialog");
            Closed(settings.OwnedWindows.OfType<AvailabilityDialog>().Single(w => w.IsVisible));
            Check(settings).Close();
            Ui.Settle();

            window.Perform(AppActions.NewSolution);
            NewProjectDialog solution = Opened<NewProjectDialog>(window);
            Ui.WaitFor(() => solution.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
            Check(solution);
            solution.SearchBox.Focus();
            Ui.Press(solution, OperatingSystem.IsMacOS() ? "meta A" : "control A");
            solution.KeyTextInput("No_template_can_match_this_audit_search");
            Ui.Settle();
            Assert.False(solution.CreateButton.IsEnabled);
            Ui.Press(solution, "ENTER");
            Assert.True(solution.IsVisible && !solution.IsCreating, "A disabled default must not start creation on Enter");
            Point disabledAt = solution.CreateButton.TranslatePoint(new Point(10, 10), solution)!.Value;
            solution.MouseMove(disabledAt);
            Check(solution);
            solution.Query = string.Empty;
            Ui.Settle();
            Assert.True(solution.CreateButton.IsEnabled, "Clearing the search must make the chosen installed template available again");
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
        if (Environment.GetEnvironmentVariable("PASSENGER_UI_AUDIT") is { Length: > 0 })
        {
            dialog.SetRenderScaling(2);
            ThemeVariant? requested = dialog.RequestedThemeVariant;
            try
            {
                foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    dialog.RequestedThemeVariant = theme;
                    CheckCurrent(dialog);
                }
            }
            finally
            {
                dialog.RequestedThemeVariant = requested;
                Ui.Settle();
            }
            return dialog;
        }
        return CheckCurrent(dialog);
    }

    private static T CheckCurrent<T>(T dialog) where T : Window
    {
        Ui.Settle();
        string title = dialog.Title ?? dialog.GetType().Name;
        CheckPage(dialog);
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
        foreach (Button button in shown.Where(b => !b.IsEffectivelyEnabled && b.Classes.Contains("primary")))
        {
            ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(Colour(dialog, "Passenger.DisabledText"), Solid(presenter.Foreground));
            Assert.Equal(Colour(dialog, "Passenger.DisabledBackground"), Solid(presenter.Background));
            Assert.Equal(Colour(dialog, "Passenger.DisabledBorder"), Solid(presenter.BorderBrush));
            Assert.NotEqual(Colour(dialog, "Passenger.Text"), Solid(presenter.Foreground));
            Assert.Equal(new Thickness(1), presenter.BorderThickness);
        }
        foreach (Button button in shown.Where(b => !b.IsEffectivelyEnabled && b.Classes.Contains("tool")))
        {
            ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(0, Solid(presenter.Background).A);
            Assert.Equal(default, presenter.BorderThickness);
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
        UiAudit.Capture(dialog, $"dialog-{dialog.GetType().Name}-{dialog.ActualThemeVariant}-{dialog.Bounds.Width:0}-{Interlocked.Increment(ref captureNumber):D3}");
        return dialog;
    }

    private static void CheckPage(Window dialog)
    {
        // Diff and merge windows are full code surfaces, rather than form pages over the chrome.
        if (dialog is DiffWindow or MergeWindow)
        {
            return;
        }
        Color chrome = Colour(dialog, "Passenger.Chrome");
        Color editor = Colour(dialog, "Passenger.Editor.Background");
        Assert.Equal(chrome, Solid(dialog.Background));
        foreach (Border border in dialog.GetVisualDescendants().OfType<Border>().Where(b => b.TemplatedParent is null && b.IsEffectivelyVisible))
        {
            if (border.Classes.Contains("dialogPage"))
            {
                Assert.Equal(chrome, Solid(border.Background));
            }
            // Framed data, code previews and arguments may use the editor's ground; an unframed page may not.
            if (border.Background is ISolidColorBrush brush && brush.Color == editor && border.BorderThickness == default)
            {
                Assert.Fail($"{dialog.Title} has an unframed editor-coloured page instead of the shared chrome");
            }
        }
    }

    private static Color Colour(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush
            ? brush.Color : throw new InvalidOperationException($"No solid theme token {key}");

    private static Color Solid(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

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
        if (dialog.CanResize && dialog.MinWidth > 0 && dialog.MinWidth < dialog.Bounds.Width)
        {
            dialog.Width = dialog.MinWidth;
            Ui.Settle();
            Check(dialog);
        }
        dialog.Close();
        Ui.Settle();
    }
}
