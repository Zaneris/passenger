using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What Passenger looks for above a folder it opens (an .editorconfig, a project's own TypeScript in a
/// node_modules) is read from the person's own parent folders, whatever their mode, every one from their home to its disk's
/// root among them, and passed over in a parent another user owns, or where another user put it in the person's own: the
/// log says where and why; an exFAT volume's folders are the person's. Another user's folder or file is the walk's stand-in
/// for one (<see cref="FoldersUp.HeldByAnother"/>), as a test makes none of its own.</summary>
public sealed class ParentFoldersTests : IDisposable
{
    private const UnixFileMode Shown = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Everyones = Shown | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private readonly string parent = PathNames.Canonical(TestFolders.Create("parent-"));

    public void Dispose()
    {
        Mode(Shown);
        TestFolders.Delete(parent);
    }

    [Fact]
    public void A_walk_up_from_the_persons_home_reads_every_folder_to_the_root_of_its_disk()
    {
        string home = PathNames.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string[] walked = [.. FoldersUp.From(home)];
        Assert.True(walked.Length > 0 && walked[^1] == Path.GetPathRoot(home),
            $"The walk up from {home} read {string.Join(", ", walked)} and no further, so a global.json or .editorconfig the person keeps above their projects there goes unread");
    }

    [AvaloniaFact]
    public void An_editorconfig_in_the_persons_own_parent_folder_decides_the_indent_whatever_the_folders_mode()
    {
        string app = App();
        Mode(Everyones);
        MainWindow window = Open(app, "Spaced.cs");
        try
        {
            Assert.Equal("Tab, from .editorconfig", IndentShown(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_editorconfig_in_a_parent_folder_another_user_owns_is_passed_over_and_the_log_says_where_the_walk_ended()
    {
        string app = App();
        using IDisposable another = FoldersUp.HeldByAnother(parent);
        MainWindow window = Open(app, "Spaced.cs");
        try
        {
            Assert.True(IndentShown(window) == "4 spaces, detected",
                $"The file indents as {IndentShown(window)}, so the .editorconfig in {parent}, which another user owns, decided it");
            string said = $"stop below {parent}: another ";
            Ui.WaitFor(() => Ui.Shared(IdeErrors.LogPath).Contains(said, StringComparison.Ordinal), $"the log to say the walk up ended below {parent}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_editorconfig_another_user_put_in_the_persons_own_parent_is_passed_over()
    {
        string app = App();
        string planted = Path.Combine(parent, ".editorconfig");
        using IDisposable another = FoldersUp.HeldByAnother(planted);
        MainWindow window = Open(app, "Spaced.cs");
        try
        {
            Assert.True(IndentShown(window) == "4 spaces, detected",
                $"The file indents as {IndentShown(window)}, so {planted}, which another user put there, decided it");
            Ui.WaitFor(() => Ui.Shared(IdeErrors.LogPath).Contains($"passes over {planted}", StringComparison.Ordinal), $"the log to say {planted} was passed over");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_TypeScript_in_a_parent_another_user_owns_never_runs_and_one_in_the_persons_own_parent_serves_the_folder()
    {
        WebLanguagesTests.SkipWithoutNode(typescript: true);
        string ran = Path.Combine(parent, "tsserver.marker");
        string typescript = Path.Combine(parent, "node_modules", "typescript");
        WebLanguagesTests.CopyFolder(Path.Combine(Passenger.App.Web.WebLanguages.Modules, "typescript"), typescript);
        string server = Path.Combine(typescript, "lib", "tsserver.js");
        File.WriteAllText(server, $"require('fs').writeFileSync({System.Text.Json.JsonSerializer.Serialize(ran)}, 'ran');\n" + File.ReadAllText(server));
        string app = Path.Combine(parent, "app");
        TestFolders.Write(app, "tsconfig.json", "{ \"compilerOptions\": { \"strict\": true } }\n");
        TestFolders.Write(app, "main.ts", "const n: number = 1;\n");

        MainWindow window;
        using (FoldersUp.HeldByAnother(parent))
        {
            window = Open(app, "main.ts");
            try
            {
                Ui.WaitFor(() => window.LanguageShown?.Contains("(Passenger's)", StringComparison.Ordinal) == true,
                    () => $"the status bar to name Passenger's TypeScript under the parent another user owns ({window.LanguageShown})");
                Assert.False(File.Exists(ran), $"The TypeScript in {parent}, which another user owns, ran for the folder below it");
            }
            finally
            {
                window.Close();
            }
        }

        window = Open(app, "main.ts");
        try
        {
            Ui.WaitFor(() => File.Exists(ran), () => $"the TypeScript in the person's own parent folder to run ({window.LanguageShown})");
            Ui.WaitFor(() => window.LanguageShown?.Contains("TypeScript", StringComparison.Ordinal) == true
                && !window.LanguageShown.Contains("(Passenger's)", StringComparison.Ordinal),
                () => $"the status bar to name the project's own TypeScript ({window.LanguageShown})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_editorconfig_at_the_top_of_an_exFAT_volume_decides_the_indent_of_a_project_below_it()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "A test's exFAT volume is a disk image, on macOS alone");
        string volume = Directory.CreateDirectory(Path.Combine(parent, "volume")).FullName;
        string image = Path.Combine(parent, "volume.dmg");
        ChildResult made = Ran("/usr/bin/hdiutil", "create", "-size", "20m", "-fs", "ExFAT", "-volname", "PASSENGER", "-ov", image, "-quiet");
        Assert.SkipUnless(made.Succeeded, $"hdiutil made no exFAT disk image (exit code {made.ExitCode}): {made.Error}{made.Output}");
        ChildResult attached = Ran("/usr/bin/hdiutil", "attach", image, "-nobrowse", "-noautoopen", "-mountpoint", volume, "-quiet");
        Assert.SkipUnless(attached.Succeeded, $"hdiutil attached no exFAT disk image (exit code {attached.ExitCode}): {attached.Error}{attached.Output}");
        try
        {
            File.WriteAllText(Path.Combine(volume, ".metadata_never_index"), string.Empty);
            TestFolders.Write(volume, ".editorconfig", "root = true\n\n[*]\nindent_style = tab\n");
            string app = Path.Combine(PathNames.Canonical(volume), "repo", "app");
            TestFolders.Write(app, "Spaced.cs", "class Spaced\n{\n    void Run()\n    {\n        int a = 1;\n    }\n}\n");
            MainWindow window = Open(app, "Spaced.cs");
            try
            {
                Assert.True(IndentShown(window) == "Tab, from .editorconfig",
                    $"The file indents as {IndentShown(window)}, so the .editorconfig at the top of the exFAT volume, the person's, was passed over");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            TestVolumes.Detach(volume, image);
        }
    }

    /// <summary>A system tool run on the thread pool and waited for with the window's loop running.</summary>
    private static ChildResult Ran(string tool, params string[] arguments)
    {
        TimeSpan limit = TimeSpan.FromMinutes(1);
        return Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tool, arguments), limit: limit.Scaled())),
            $"{tool} {string.Join(' ', arguments)}", limit.Scaled() + TestFolders.Spare);
    }

    /// <summary>A folder in the parent, its file indented by four spaces, under the parent's .editorconfig of tabs.</summary>
    private string App()
    {
        TestFolders.Write(parent, ".editorconfig", "root = true\n\n[*]\nindent_style = tab\n");
        string app = Path.Combine(parent, "app");
        TestFolders.Write(app, "Spaced.cs", "class Spaced\n{\n    void Run()\n    {\n        int a = 1;\n    }\n}\n");
        return app;
    }

    /// <summary>The parent's mode, where folders have one.</summary>
    private void Mode(UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(parent, mode);
        }
    }

    private static MainWindow Open(string folder, string file)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
        window.OpenFile(Path.Combine(folder, file));
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        return window;
    }

    private static string? IndentShown(MainWindow window) => window.FindControl<TextBlock>("IndentText")!.Text;
}
