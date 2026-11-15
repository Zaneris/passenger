using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An Azure Functions app's own Run through a stand-in func put first on the PATH, which says what it was given
/// and ends (on Windows a .cmd, as npm installs Core Tools' func), so no Core Tools are needed: a console project that says
/// it is a Functions app, which builds without their SDK.</summary>
public sealed class FunctionsStandInTests
{
    /// <summary>Words cmd.exe would read as its own (an operator, a pipe, an escape) in the app's launch profile reach func
    /// after its start as written.</summary>
    [AvaloniaFact]
    public void A_Functions_apps_launch_profile_words_reach_func_word_for_word_through_cmds_operators()
    {
        const string Words = "R&D a|b x^y";
        string folder = PathNames.Canonical(TestFolders.Create("functions-words-"));
        TestFolders.Write(folder, "Fn/Fn.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <AzureFunctionsVersion>v4</AzureFunctionsVersion>
              </PropertyGroup>
            </Project>
            """);
        TestFolders.Write(folder, "Fn/Program.cs", "System.Console.WriteLine(\"worker\");\n");
        TestFolders.Write(folder, "Fn/Properties/launchSettings.json",
            $$"""{ "profiles": { "Fn": { "commandName": "Project", "commandLineArgs": "--port 7071 {{Words}}" } } }""");
        Restore.Run(Path.Combine(folder, "Fn"));
        string tools = TestFolders.Create("functions-tools-");
        TestFolders.StandIn(tools, "func", "echo stand-in func %*", "echo \"stand-in func $*\"");
        string? was = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + was);
        try
        {
            MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
            try
            {
                Ui.WaitFor(() => window.Configurations.Any(c => c.LaunchProfile == "Fn"),
                    () => $"the Functions app's own configuration, where there are {string.Join(", ", window.Configurations.Select(c => c.Name))}", TimeSpan.FromMinutes(2));
                string own = window.Configurations.First(c => c.LaunchProfile == "Fn").Name;
                Assert.True(window.SelectConfiguration(own));
                Ui.Launched(window, window.RunAsync(), "the Functions app's build and func's start", TimeSpan.FromMinutes(3));
                Ui.WaitFor(() => window.Shown(window.Configuration!) == own && Output(window).Contains("stand-in func", StringComparison.Ordinal),
                    () => $"the stand-in func to say what it was given, and end:\n{Output(window)}\n{Ui.Said(window)}");
                string given = Output(window).Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("stand-in func ", StringComparison.Ordinal))["stand-in func ".Length..];
                Assert.True(given.Replace("\"", string.Empty, StringComparison.Ordinal) == $"start --port 7071 {Words}",
                    $"func was given other words than the launch profile's {Words}, as cmd.exe read them as its own:\n{Output(window)}");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", was);
        }
    }

    private static string Output(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }
}
