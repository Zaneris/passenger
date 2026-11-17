using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What Passenger looks for above a folder it opens (an .editorconfig, a project's own TypeScript in a
/// node_modules) is read from the person's own parent folders, every one from their home to its disk's root among them,
/// and passed over in a parent every user may write, where anyone may have put it: the walk ends there, the log saying
/// where and why. Windows' folders have no mode letting every user write, so there the person's own folders alone are
/// checked, by their owners.</summary>
public sealed class ParentFoldersTests : IDisposable
{
    private const UnixFileMode Shown = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Everyones = Shown | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private readonly string parent = PathNames.Canonical(TestFolders.Create("parent-"));

    public void Dispose()
    {
        Shared(false);
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
    public void An_editorconfig_in_the_persons_own_parent_folder_decides_the_indent()
    {
        string app = App();
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
    public void An_editorconfig_in_a_parent_folder_every_user_may_write_is_passed_over_and_the_log_says_where_the_walk_ended()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' folders have no mode that lets every user write in them");
        string app = App();
        Shared(true);
        MainWindow window = Open(app, "Spaced.cs");
        try
        {
            Assert.True(IndentShown(window) == "4 spaces, detected",
                $"The file indents as {IndentShown(window)}, so the .editorconfig in {parent}, which every user may write, decided it");
            string said = $"stop below {parent}: every user may write in it";
            Ui.WaitFor(() => Ui.Shared(IdeErrors.LogPath).Contains(said, StringComparison.Ordinal), $"the log to say the walk up ended below {parent}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_TypeScript_in_a_parent_every_user_may_write_never_runs_and_one_in_the_persons_own_parent_serves_the_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' folders have no mode that lets every user write in them");
        WebLanguagesTests.SkipWithoutNode(typescript: true);
        string ran = Path.Combine(parent, "tsserver.marker");
        string typescript = Path.Combine(parent, "node_modules", "typescript");
        WebLanguagesTests.CopyFolder(Path.Combine(Passenger.App.Web.WebLanguages.Modules, "typescript"), typescript);
        string server = Path.Combine(typescript, "lib", "tsserver.js");
        File.WriteAllText(server, $"require('fs').writeFileSync({System.Text.Json.JsonSerializer.Serialize(ran)}, 'ran');\n" + File.ReadAllText(server));
        string app = Path.Combine(parent, "app");
        TestFolders.Write(app, "tsconfig.json", "{ \"compilerOptions\": { \"strict\": true } }\n");
        TestFolders.Write(app, "main.ts", "const n: number = 1;\n");

        Shared(true);
        MainWindow window = Open(app, "main.ts");
        try
        {
            Ui.WaitFor(() => window.LanguageShown?.Contains("(Passenger's)", StringComparison.Ordinal) == true,
                () => $"the status bar to name Passenger's TypeScript over the parent every user may write ({window.LanguageShown})");
            Assert.False(File.Exists(ran), $"The TypeScript in {parent}, which every user may write, ran for the folder below it");
        }
        finally
        {
            window.Close();
        }

        Shared(false);
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

    /// <summary>A folder in the parent, its file indented by four spaces, under the parent's .editorconfig of tabs.</summary>
    private string App()
    {
        TestFolders.Write(parent, ".editorconfig", "root = true\n\n[*]\nindent_style = tab\n");
        string app = Path.Combine(parent, "app");
        TestFolders.Write(app, "Spaced.cs", "class Spaced\n{\n    void Run()\n    {\n        int a = 1;\n    }\n}\n");
        return app;
    }

    /// <summary>The parent made one every user may write in, or the person's alone again.</summary>
    private void Shared(bool shared)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(parent, shared ? Everyones : Shown);
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
