using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Core;
using Passenger.Notices;

namespace Passenger.Tests;

/// <summary>About Passenger, naming the version, the commit and the runtime, and the licence and notices the app
/// carries beside it: Passenger's own licence, and every package it carries (the app's, the debugger's and Roslyn's
/// server's) with its licence's text.</summary>
[AboutMacOS]
public sealed partial class AboutTests : IDisposable
{
    private readonly string folder = TestFolders.Create("about-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>About names the version, commit and runtime and opens the notices and licence beside the app, which
    /// carries every package's notice with its licence text, as one flow, each step the check one test made in an
    /// instance of its own.</summary>
    [AvaloniaFact]
    public void About_and_the_app_carry_the_version_the_licence_and_every_packages_notice()
    {
        Flow.StepApart<AboutTests>("About names the version the commit and the runtime and opens the notices and the licence beside the app", t => t.About_names_the_version_the_commit_and_the_runtime_and_opens_the_notices_and_the_licence_beside_the_app());
        Flow.StepApart<AboutTests>("the app carries its licence and the notices of every package it carries each with its licence text", t => t.The_app_carries_its_licence_and_the_notices_of_every_package_it_carries_each_with_its_licence_text());
    }

    private void About_names_the_version_the_commit_and_the_runtime_and_opens_the_notices_and_the_licence_beside_the_app()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            AboutDialog about = OpenAbout(window);
            string shown = Shown(about);
            string version = VersionRegex().Match(File.ReadAllText(Path.Combine(Repository, "Directory.Build.props"))).Groups[1].Value;
            Assert.True(shown.Contains($"Passenger {version}", StringComparison.Ordinal),
                $"About does not name the version Directory.Build.props gives the build, {version}: it shows\n{shown}");
            string head = TestGit.Run(Repository, "rev-parse", "HEAD").Trim();
            Assert.True(shown.Contains($"Commit {head[..12]}", StringComparison.Ordinal),
                $"About does not name the commit the IDE was built from, {head[..12]}: it shows\n{shown}");
            Assert.True(shown.Contains(RuntimeInformation.FrameworkDescription, StringComparison.Ordinal),
                $"About does not name the runtime the IDE runs on, {RuntimeInformation.FrameworkDescription}: it shows\n{shown}");
            Assert.Contains(head, about.Text);

            Ui.Click(Link(about, AboutDialog.NoticesLink));
            Assert.False(about.IsVisible, "About stays open over the notices its link opened");
            Assert.Equal(PathNames.Canonical(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")), window.EditorArea.Active?.Path);
            string notices = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
            Assert.StartsWith("Third-party notices for Passenger", notices, StringComparison.Ordinal);
            window.EditorArea.CodeEditor.Focus();
            Ui.Type(window, "X", "X");
            Assert.True(window.EditorArea.CodeEditor.Document.Snapshot.ToString() == notices && !window.EditorArea.Active!.IsModified,
                "A key typed into the notices the app carries changed them, which a save would write into the installed app");

            about = OpenAbout(window);
            Ui.Click(Link(about, AboutDialog.LicenseLink));
            Assert.Equal(PathNames.Canonical(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt")), window.EditorArea.Active?.Path);
            Assert.Equal(File.ReadAllText(Path.Combine(Repository, "LICENSE")).ReplaceLineEndings("\n"),
                window.EditorArea.CodeEditor.Document.Snapshot.ToString().ReplaceLineEndings("\n"));
        }
        finally
        {
            window.Close();
        }
    }

    private void The_app_carries_its_licence_and_the_notices_of_every_package_it_carries_each_with_its_licence_text()
    {
        string beside = AppContext.BaseDirectory;
        Assert.True(File.Exists(Path.Combine(beside, "LICENSE.txt")), $"The app carries no LICENSE.txt beside it, in {beside}");
        Assert.Equal(File.ReadAllText(Path.Combine(Repository, "LICENSE")), File.ReadAllText(Path.Combine(beside, "LICENSE.txt")));
        string path = Path.Combine(beside, "THIRD-PARTY-NOTICES.txt");
        Assert.True(File.Exists(path), $"The app carries no THIRD-PARTY-NOTICES.txt beside it, in {beside}");
        string notices = File.ReadAllText(path);

        // Each package's lines, and the licence under them: "Id Version (where): copyright", then "Licence: ...", its text.
        Dictionary<string, (string Licence, string Text)> named = new(StringComparer.OrdinalIgnoreCase);
        foreach (string block in notices.ReplaceLineEndings("\n").Split("\n" + new string('-', 80) + "\n"))
        {
            string[] parts = block.Split("\n\n", 3);
            if (parts.Length < 3 || !parts[1].StartsWith("Licence: ", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match package in PackageLine().Matches(parts[0]))
            {
                named[$"{package.Groups[1].Value}/{package.Groups[2].Value}"] = (parts[1]["Licence: ".Length..], parts[2].Trim());
            }
        }

        SortedSet<string> carried = new(StringComparer.OrdinalIgnoreCase);
        carried.UnionWith(CarriedPackages.In(Path.Combine(beside, "Passenger.deps.json")).Select(p => $"{p.Id}/{p.Version}"));
        foreach (string deps in Directory.EnumerateFiles(Path.Combine(beside, "debugger"), "*.deps.json")
            .Concat(Directory.EnumerateFiles(Path.Combine(beside, "roslyn"), "*.deps.json", SearchOption.AllDirectories)))
        {
            carried.UnionWith(Packages(deps));
        }
        Assert.True(carried.Count > 100, $"Only {carried.Count} packages were found carried beside the tests, which is not the app's whole");
        string[] missing = [.. carried.Where(p => !named.ContainsKey(p))];
        Assert.True(missing.Length == 0, $"The app's notices do not name {missing.Length} package(s) it carries: {string.Join(", ", missing)}");
        string[] textless = [.. carried.Where(p => named[p].Licence.Length == 0 || named[p].Text.Length == 0)];
        Assert.True(textless.Length == 0, $"The app's notices name these packages without a licence's text: {string.Join(", ", textless)}");
        (string licence, string text) = named[carried.First(p => p.StartsWith("Avalonia/", StringComparison.Ordinal))];
        Assert.True(licence == "MIT" && text.Contains("Permission is hereby granted, free of charge", StringComparison.Ordinal),
            $"Avalonia, under MIT as its package states, is under '{licence}' in the notices, with this text:\n{text}");

        Assert.Contains("## SharpDbg and ICorDebugSharp", notices);
        Assert.Contains("Copyright (c) 2026 Matthew Parker", notices);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", notices);
    }

    /// <summary>About as a person opens it: the application menu's first item on macOS, Help's About elsewhere.</summary>
    private static AboutDialog OpenAbout(MainWindow window)
    {
        NativeMenu help = Open(window.MenuBar, "Help");
        if (OperatingSystem.IsMacOS())
        {
            Assert.DoesNotContain(help.Items.OfType<NativeMenuItem>(), i => i.Header == "About");
            NativeMenuItem first = MainWindow.ApplicationMenu!.Items.OfType<NativeMenuItem>().First();
            Assert.Equal("About Passenger", first.Header);
            first.Command!.Execute(null);
        }
        else
        {
            NativeMenuItem last = help.Items.OfType<NativeMenuItem>().Last();
            Assert.Equal("About", last.Header);
            last.Command!.Execute(null);
        }
        Ui.Settle();
        return Assert.IsType<AboutDialog>(window.Frontmost);
    }

    /// <summary>A menu as the platform opens it: asked to bring its items up to date first.</summary>
    private static NativeMenu Open(NativeMenu parent, string title)
    {
        NativeMenu menu = parent.Items.OfType<NativeMenuItem>().First(i => i.Header == title).Menu!;
        Type bridge = menu.GetType().GetInterfaces().First(i => i.GetMethod("RaiseNeedsUpdate") is not null);
        bridge.GetMethod("RaiseNeedsUpdate")!.Invoke(menu, null);
        return menu;
    }

    /// <summary>Every line of text About shows.</summary>
    private static string Shown(Window about) =>
        string.Join("\n", about.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    private static Button Link(Window about, string words) =>
        about.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains(StyleClasses.Link) && (string?)b.Content == words);

    /// <summary>The packages a .deps.json file's program carries (<see cref="CarriedPackages"/>) whose files are beside it,
    /// as Id/Version: those with none of their files there (Visual Studio's left out of roslyn/) are not carried.</summary>
    private static IEnumerable<string> Packages(string deps)
    {
        HashSet<string> beside = [.. Directory.EnumerateFiles(Path.GetDirectoryName(deps)!, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f))];
        return [.. CarriedPackages.In(deps).Where(p => p.Files.Any(beside.Contains)).Select(p => $"{p.Id}/{p.Version}")];
    }

    /// <summary>The repository the tests were built from: the folder above them with Passenger.slnx.</summary>
    private static string Repository
    {
        get
        {
            for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
            {
                if (File.Exists(Path.Combine(at.FullName, "Passenger.slnx")))
                {
                    return at.FullName;
                }
            }
            throw new DirectoryNotFoundException($"No folder above {AppContext.BaseDirectory} holds Passenger.slnx");
        }
    }

    [GeneratedRegex(@"^(\S+) (\S+) \([^)]*\): ", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    [GeneratedRegex("<Version>(.*?)</Version>")]
    private static partial Regex VersionRegex();
}
