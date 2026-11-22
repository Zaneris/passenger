using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App;
using Passenger.Core;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>The system's high contrast, stood in for (<see cref="SystemContrast.StandIn"/>), as a person meets it: High
/// Contrast shows in the chosen theme's place, in the system's dark or light, Settings saying why its Theme row has no
/// effect; Use High Contrast turned off brings the chosen theme back while the system still asks; and the chosen theme
/// comes back once the system no longer asks.</summary>
[SameOnEverySystem]
public sealed class SystemContrastTests : IDisposable
{
    private static readonly SourcedTheme Cubicle = SourcedTheme.BuiltIn("Cubicle");

    private readonly string state = TestFolders.Create("system-contrast-state-");

    public void Dispose() => TestFolders.Delete(state);

    /// <summary>While the system asks for high contrast High Contrast shows in its dark or light until it no longer
    /// asks, the system's own ask read whatever stands in for it, as one flow, each step the check one test made in an
    /// instance of its own.</summary>
    [AvaloniaFact]
    public void High_Contrast_follows_the_systems_own_ask_whatever_stands_in_for_it()
    {
        Flow.StepApart<SystemContrastTests>("while the system asks for high contrast High Contrast shows in its dark or light until it no longer asks", t => t.While_the_system_asks_for_high_contrast_High_Contrast_shows_in_its_dark_or_light_until_it_no_longer_asks());
        Flow.StepApart<SystemContrastTests>("the systems own high contrast is read whatever stands in for it", t => t.The_systems_own_high_contrast_is_read_whatever_stands_in_for_it());
    }

    private void While_the_system_asks_for_high_contrast_High_Contrast_shows_in_its_dark_or_light_until_it_no_longer_asks()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false };
        window.Show();
        window.ApplySettings(window.Settings with { Look = Cubicle, Theme = ThemeChoice.Dark });
        Ui.Settle();
        try
        {
            Color cubicle = Token(window, TokenKeys.Editor_Background);
            Color highDark = HighContrast(ThemeVariant.Dark);
            Color highLight = HighContrast(ThemeVariant.Light);
            using (SystemContrast.StandIn(new ContrastAsked(true, false)))
            {
                Ui.Settle();
                Assert.True(Application.Current!.RequestedThemeVariant == ThemeVariant.Light && Token(window, TokenKeys.Editor_Background) == highLight,
                    $"With a light high contrast asked, the window shows {Token(window, TokenKeys.Editor_Background)} in {Application.Current.RequestedThemeVariant}, not High Contrast's light ground {highLight}");
                Assert.Equal(Cubicle, window.Settings.Look);
                SettingsDialog dialog = window.ShowSettings();
                Ui.Settle();
                Assert.False(Declared.Look.Row!.When!.Holds(dialog.Working), "Settings does not say the Theme row stands aside while the system asks for high contrast");
                dialog.Close();

                window.ApplySettings(window.Settings with { FollowSystemContrast = false });
                Ui.Settle();
                Assert.True(Token(window, TokenKeys.Editor_Background) == cubicle && Application.Current.RequestedThemeVariant == ThemeVariant.Dark,
                    "Use High Contrast turned off did not bring back Cubicle in Dark while the system asks for high contrast");
                window.ApplySettings(window.Settings with { FollowSystemContrast = true });
                Ui.Settle();
                Assert.Equal(highLight, Token(window, TokenKeys.Editor_Background));
            }
            Ui.Settle();
            Assert.True(Token(window, TokenKeys.Editor_Background) == cubicle && Application.Current.RequestedThemeVariant == ThemeVariant.Dark,
                "Once the system no longer asks for high contrast, the chosen Cubicle in Dark did not come back");

            using (SystemContrast.StandIn(new ContrastAsked(true, true)))
            {
                Ui.Settle();
                Assert.True(Token(window, TokenKeys.Editor_Background) == highDark, "A dark high contrast asked did not show High Contrast's dark ground");
            }
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>What the system asks of contrast where a run sets it before it starts (a Windows contrast theme turned on in
    /// a VM's session, say): <c>none</c>, <c>high</c>, <c>high-dark</c> or <c>high-light</c>, as the stand-in names it.</summary>
    [IdentifierHome]
    public const string ExpectedVariable = "PASSENGER_TEST_SYSTEM_CONTRAST";

    /// <summary>The system's own high contrast is read whatever stands in for it, and is what a run that set it says it set.</summary>
    private void The_systems_own_high_contrast_is_read_whatever_stands_in_for_it()
    {
        ContrastAsked read = Ui.Await(SystemContrast.ReadSystemAsync(), "the system's high contrast");
        TestContext.Current.TestOutputHelper?.WriteLine($"{Environment.OSVersion}: the system asks for {read}");
        if (Environment.GetEnvironmentVariable(ExpectedVariable) is { Length: > 0 } expected)
        {
            Assert.True(read == ContrastAsked.Parse(expected), $"The system was set to {ContrastAsked.Parse(expected)} before the run, but reads as {read}");
        }
    }

    /// <summary>The editor's ground High Contrast gives in dark or light, as the theme Passenger carries says it.</summary>
    private static Color HighContrast(ThemeVariant variant) =>
        ThemeResources.Dictionary(CarriedThemes.Theme(ThemeSource.HighContrast)!).TryGetResource(TokenKeys.Editor_Background, variant, out object? found)
            && found is ISolidColorBrush brush ? brush.Color : default;

    private static Color Token(MainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;
}
