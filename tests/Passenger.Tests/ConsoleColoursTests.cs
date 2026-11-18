using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A program's colours in the Run window, as it writes them itself and as .NET writes them for it once Passenger
/// tells it it may (an ASP.NET app's log levels, Console.ForegroundColor), drawn in the theme's terminal colours under
/// either theme, while what is no colour shows nothing.</summary>
public sealed class ConsoleColoursTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly RestoredFolders folders;
    private string? taken;

    public ConsoleColoursTests(RestoredFolders folders) => this.folders = folders;

    public void Dispose()
    {
        if (taken is not null)
        {
            TestFolders.Delete(taken);
        }
    }

    /// <summary>An ASP.NET app writing colours of its own (a named one, a bold 24-bit one) between a title and a line's
    /// erasing, then .NET's console colour (with TERM set, as an IDE started from a terminal passes on) and its logger's
    /// warning and error, then its own colour on standard error: the Run window shows its text alone, each stretch in the
    /// look it was given, the named colour drawn in each theme's terminal colour and the 24-bit one as it is, and the logger's
    /// levels in theirs, .NET having been told it may write them to the console's pipe.</summary>
    [AvaloniaFact]
    public void An_apps_own_colours_and_dotnets_reach_the_run_window_in_the_themes_terminal_colours_and_what_is_no_colour_shows_nothing()
    {
        string folder = taken ??= folders.Take("colours-", made =>
        {
            TestFolders.Write(made, "Shop.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
            TestFolders.Write(made, "Program.cs",
                "Environment.SetEnvironmentVariable(\"TERM\", \"xterm-256color\");\n"
                + "Console.WriteLine(\"plain \\u001b[31mred\\u001b[0m \\u001b[1;38;2;40;120;220mtrue blue\\u001b[0m\\u001b]0;a title\\u0007\\u001b[2K done\");\n"
                + "Console.ForegroundColor = ConsoleColor.DarkGreen;\nConsole.Write(\"told green\");\nConsole.ResetColor();\nConsole.WriteLine();\n"
                + "using (WebApplication app = WebApplication.CreateBuilder(args).Build())\n{\n"
                + "    app.Logger.LogWarning(\"the orders are late\");\n    app.Logger.LogError(\"the orders failed\");\n}\n"
                + "Console.Error.WriteLine(\"\\u001b[33mwarned\\u001b[0m on error\");\n");
            return Path.Combine(made, "Shop.csproj");
        });
        MainWindow window = Ui.OpenFolder(folder);
        OutputView run = window.BottomPanel.Run;
        try
        {
            window.Perform(AppActions.RunProgram);
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), () => $"the app to finish:\n{Output(window)}", TimeSpan.FromMinutes(2));
            string output = Output(window);
            Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
            Assert.Contains("plain red true blue done\ntold green\n", output, StringComparison.Ordinal);
            Assert.Contains("warned on error\n", output, StringComparison.Ordinal);

            Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Terminal_Ansi1), default, LookStyle.None), LookAt(run, "red"));
            Assert.Equal(new TextLook(LookColour.Of(0x2878DC), default, LookStyle.Bold), LookAt(run, "true blue"));
            Assert.Null(LookAt(run, " done"));
            Assert.Equal(TokenKeys.Terminal_Ansi3, LookAt(run, "warned")?.Fore.Token);
            if (!OperatingSystem.IsWindows())
            {
                // Windows' .NET sets a console's colours through the console alone, writing none into a pipe.
                Assert.Equal(TokenKeys.Terminal_Ansi2, LookAt(run, "told green")?.Fore.Token);
            }
            TextLook warn = LookAt(run, "warn") ?? throw new Xunit.Sdk.XunitException($"The logger's warning is drawn in no colour of its own:\n{output}");
            Assert.True(warn.Fore.Token is { } yellow && (yellow == TokenKeys.Terminal_Ansi3 || yellow == TokenKeys.Terminal_Ansi11), $"The logger's warning is drawn in {warn}");
            TextLook fail = LookAt(run, "fail") ?? throw new Xunit.Sdk.XunitException($"The logger's error is drawn in no colour of its own:\n{output}");
            Assert.Equal(TokenKeys.Terminal_Ansi1, fail.Back.Token);

            int red = output.IndexOf("red", StringComparison.Ordinal);
            int blue = output.IndexOf("true blue", StringComparison.Ordinal);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                // The app's first lines on screen, wherever the console stood.
                run.Editor.ScrollTo(new Avalonia.Vector(0, 0));
                Ui.Settle();
                Color ansi = FileColoursFolder.Token(theme, TokenKeys.Terminal_Ansi1);
                Color drawn = FileColoursFolder.Ink(window, run.Editor, red, 3);
                Assert.True(FileColoursFolder.Distance(drawn, ansi) < 90, $"In the {theme} theme the program's red is drawn {drawn}, not the theme's terminal red {ansi}");
                Color given = Color.FromRgb(40, 120, 220);
                drawn = FileColoursFolder.Ink(window, run.Editor, blue, 9);
                Assert.True(FileColoursFolder.Distance(drawn, given) < 90, $"In the {theme} theme the program's 24-bit blue is drawn {drawn}, not {given}");
            }
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the run to end");
            window.Close();
        }
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>The look the console draws a stretch of its text in, found by its first occurrence: the program's own
    /// colours where the whole stretch is in one, else null.</summary>
    internal static TextLook? LookAt(OutputView console, string stretch)
    {
        TextSnapshot text = console.Document.Snapshot;
        int at = text.ToString().IndexOf(stretch, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The console shows no \"{stretch}\"");
        IReadOnlyList<SyntaxSpan> spans = console.Editor.Highlighter!.GetSpans(text, at, stretch.Length);
        SyntaxSpan? over = spans.Where(s => s.Start <= at && s.End >= at + stretch.Length).Cast<SyntaxSpan?>().FirstOrDefault();
        Assert.True(over is not null || !spans.Any(s => s.Start < at + stretch.Length && s.End > at),
            $"\"{stretch}\" is drawn in more than one look: {string.Join(", ", spans.Select(s => $"{text.GetText(s.Start, s.Length)} {s.Kind}"))}");
        return over is { } span && TextLooks.TryGet(span.Kind, out TextLook look) ? look : null;
    }
}
