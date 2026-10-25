using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Files through the whole window: opened from a folder or dropped from the file manager, edited, saved with the
/// platform's own Save shortcut, and written back the way they were stored, or as UTF-8 when that would lose a character.</summary>
public sealed class FileTests : IDisposable
{
    private readonly string folder = TestFolders.Create("test-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void An_edit_saved_keeps_the_files_byte_order_mark_and_its_windows_line_endings()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        string path = Path.Combine(folder, "Program.cs");
        File.WriteAllBytes(path, [.. bom, .. Encoding.UTF8.GetBytes("class A\r\n{\r\n}\r\n")]);

        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Program.cs");
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = Text.SelectionSet.Single("class A".Length);

        Ui.Press(window, "ENTER");
        window.KeyTextInput("// added");
        Ui.Settle();
        Ui.Press(window, Ui.Shortcut(AppActions.Save));

        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal(bom, saved[..3]);
        Assert.Equal("class A\r\n// added\r\n{\r\n}\r\n", Encoding.UTF8.GetString(saved, 3, saved.Length - 3));
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    [AvaloniaFact]
    public void A_Windows_1252_file_shows_its_quotes_dashes_and_euro_and_a_character_it_cannot_hold_is_warned_of_then_saved_as_UTF_8()
    {
        // Windows-1252's quotes, dashes and euro are bytes 0x80–0x9F, which Latin-1 reads as control characters.
        string path = Path.Combine(folder, "Prices.cs");
        File.WriteAllBytes(path, [.. "// "u8, 0x93, .. "Caf"u8, 0xE9, 0x94, .. " "u8, 0x96, .. " 5 "u8, 0x80, .. " "u8, 0x97, .. " prices\nclass Prices { }\n"u8]);
        MainWindow window = Open();
        window.OpenFile("Prices.cs");
        Ui.Settle();
        Assert.Equal("// “Café” – 5 € — prices\nclass Prices { }\n", window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        Assert.Equal("windows-1252", EncodingShown(window));

        window.EditorArea.CodeEditor.Selections = Text.SelectionSet.Single("// “Café”".Length);
        window.KeyTextInput(" señor");
        Ui.Settle();
        Assert.Null(window.EditorArea.ActiveGroup.BannerText);
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal([.. "// "u8, 0x93, .. "Caf"u8, 0xE9, 0x94, .. " se"u8, 0xF1, .. "or "u8, 0x96, .. " 5 "u8, 0x80, .. " "u8, 0x97, .. " prices\nclass Prices { }\n"u8], File.ReadAllBytes(path));
        Assert.Equal("windows-1252", EncodingShown(window));

        window.KeyTextInput(" → 🚀");
        Ui.Settle();
        Assert.Equal("windows-1252 cannot hold '→': the next save writes Prices.cs as UTF-8", window.EditorArea.ActiveGroup.BannerText);
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        byte[] saved = File.ReadAllBytes(path);
        Assert.False(Array.IndexOf(saved, (byte)'?') >= 0, "A character Windows-1252 cannot hold was saved as a question mark");
        Assert.Equal("// “Café” señor → 🚀 – 5 € — prices\nclass Prices { }\n", new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(saved));
        Assert.Equal("UTF-8", EncodingShown(window));
        Assert.Equal("Prices.cs was saved as UTF-8: windows-1252 cannot hold '→'", window.Status);
        Assert.Null(window.EditorArea.ActiveGroup.BannerText);
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    [AvaloniaFact]
    public void A_binary_file_opens_as_its_bytes_text_read_only_and_typing_never_changes_it()
    {
        // A file with no view of its own: an image's name would open it as a preview.
        string path = Path.Combine(folder, "data.bin");
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x1A, 0x0A];
        File.WriteAllBytes(path, bytes);
        MainWindow window = Ui.OpenFolder(folder);
        Assert.Null(window.OpenFile("data.bin"));
        Ui.Settle();
        Assert.True(window.EditorArea.Active is { IsBinary: true, IsReadOnly: true }, "data.bin should open as its bytes' text, read-only");

        Ui.Type(window, "x", "x");
        window.Perform(AppActions.SaveAll);
        Ui.Settle();
        Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Typing in a binary file's tab should never change the file");
        window.Close();
    }

    /// <summary>A file past what the editor opens as text or bytes (64 MB) is refused at once, nothing of it read, with
    /// why in the status bar: opened, a file holds about six and a half times its size.</summary>
    [AvaloniaFact]
    public void A_file_too_large_to_open_as_text_or_bytes_is_refused_with_why_in_the_status_bar()
    {
        string log = Path.Combine(folder, "huge.log");
        using (FileStream file = File.Create(log))
        {
            file.SetLength(65 * 1024 * 1024);
        }
        MainWindow window = Ui.OpenFolder(folder);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string? why = window.OpenFile("huge.log");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Ui.Settle();
        Assert.Equal("huge.log is 65 MB: the editor opens a file of up to 64 MB as text", why);
        Assert.Equal(why, window.Status);
        Assert.Empty(window.EditorArea.Documents);
        Assert.True(allocated < 16 << 20, $"Refusing huge.log allocated {allocated >> 20} MB on the UI thread: it was read first");

        window.Close();
        MainWindow front = new() { Width = 1200, Height = 800 };
        front.Show();
        Ui.Settle();
        Assert.Equal("huge.log is 65 MB: the editor opens a file of up to 64 MB as text", front.Open(log));
        Assert.Empty(front.EditorArea.Documents);
        front.Close();
    }

    /// <summary>A binary file's tab shows its bytes drawn as signs, none of the file's text: what reads or changes a
    /// file's text leaves it alone, or an agent's replace would write the signs over its bytes.</summary>
    [AvaloniaFact]
    public void A_binary_files_bytes_are_no_text_for_the_status_bar_an_agent_replace_in_files_or_a_bookmark()
    {
        string path = Path.Combine(folder, "data.bin");
        byte[] bytes = [(byte)'k', (byte)'e', (byte)'y', 0, (byte)'\n', (byte)'k', (byte)'e', (byte)'y', 0xFF];
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "key\n");
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("data.bin");
        Ui.Settle();
        Assert.True(window.EditorArea.Active is { IsBinary: true, HoldsText: false }, "data.bin did not open as its bytes' text");
        Assert.Equal("A binary file, shown as its bytes' text, read-only", window.EditorArea.Active!.Origin);

        Assert.False(window.FindControl<Button>("EncodingButton")!.IsVisible, "A binary file's tab shows a text encoding");
        Assert.False(window.Perform(AppActions.ToggleBookmark), "A bookmark was put on a line of a binary file");
        Assert.Empty(window.Bookmarks.All);
        Passenger.App.Mcp.PassengerTools agent = new(window);
        foreach (Task asked in new Task[] { agent.EditorText("data.bin"), agent.Edit("data.bin", "key", "lock") })
        {
            Ui.WaitFor(() => asked.IsCompleted, "the agent's answer");
            Assert.True(asked.IsFaulted, "An agent read or edited a binary file's bytes as its text");
            Assert.Contains("data.bin is a binary file, shown as its bytes' text", asked.Exception!.InnerException!.Message, StringComparison.Ordinal);
        }
        Task<string> replacing = agent.ReplaceInFiles("key", "lock");
        Ui.WaitFor(() => replacing.IsCompleted, "replace_in_files' answer");
        Assert.DoesNotContain("data.bin", replacing.Result, StringComparison.Ordinal);
        Assert.Equal("lock\n", File.ReadAllText(Path.Combine(folder, "notes.txt")));
        Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Replace in Files wrote over a binary file's bytes");
        Assert.False(window.EditorArea.Active.IsModified, "Replace in Files changed a binary file's tab");
        window.Close();
    }

    [AvaloniaFact]
    public void Saving_a_file_does_not_follow_a_planted_temporary_file_link()
    {
        string path = Path.Combine(folder, "One.cs");
        string sentinel = Path.Combine(TestFolders.Create("save-sentinel-"), "sentinel.txt");
        File.WriteAllText(path, "class One { }\n");
        File.WriteAllText(sentinel, "keep this file\n");
        string planted = path + FileText.SaveSuffix;
        TestFolders.Link(planted, sentinel);
        MainWindow window = Open();
        try
        {
            Edit(window, "One.cs", "public ");
            Ui.Press(window, Ui.Shortcut(AppActions.Save));
            Assert.Equal("public class One { }\n", File.ReadAllText(path));
            Assert.Equal("keep this file\n", File.ReadAllText(sentinel));
            Assert.NotNull(new FileInfo(planted).LinkTarget);
            Assert.Equal([planted], Directory.EnumerateFiles(folder, "*" + FileText.SaveSuffix));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Files_dropped_from_the_file_manager_open_in_the_editor_and_a_folder_dropped_on_the_Welcome_screen_opens()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "notes\n");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        Ui.Settle();

        Ui.Drop(window, window.EditorArea, folder);
        Assert.True(window.Folder == PathNames.Canonical(folder), $"The folder dropped on the Welcome screen opened {window.Folder ?? "nothing"}");

        Ui.Drop(window, window.EditorArea, Path.Combine(folder, "One.cs"), Path.Combine(folder, "notes.txt"));
        Assert.Equal(["One.cs", "notes.txt"], window.EditorArea.Documents.Select(d => d.Name));
        Assert.Equal("notes.txt", window.EditorArea.Active?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void Closing_a_tab_or_the_window_saves_every_unsaved_edit_without_asking()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        File.WriteAllText(Path.Combine(folder, "Two.cs"), "class Two { }\n");
        File.WriteAllText(Path.Combine(folder, "Three.cs"), "class Three { }\n");
        MainWindow window = Open();
        Edit(window, "One.cs", "public ");
        Edit(window, "Two.cs", "internal ");
        Edit(window, "Three.cs", "sealed ");

        window.Perform(AppActions.CloseTab);
        Ui.Settle();
        Assert.False(window.IsPopupOpen, "Closing a changed tab asked a question Rider never asks");
        Assert.Equal("sealed class Three { }\n", File.ReadAllText(Path.Combine(folder, "Three.cs")));

        window.Close();
        Assert.Equal("public class One { }\n", File.ReadAllText(Path.Combine(folder, "One.cs")));
        Assert.Equal("internal class Two { }\n", File.ReadAllText(Path.Combine(folder, "Two.cs")));
    }

    [AvaloniaFact]
    public void A_file_another_program_reads_without_letting_it_be_deleted_is_still_saved_and_quitting_goes_ahead()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open();
        Edit(window, "One.cs", "public ");

        // As a virus scanner or a build reading it holds it: reading and writing shared, deleting (so replacing) not.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            window.Close();
            Assert.False(window.IsVisible, $"Quitting stopped at a file another program was reading: {window.Status}");
        }
        Assert.Equal("public class One { }\n", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(folder, "*" + FileText.SaveSuffix));
    }

    /// <summary>A save writes its temporary file under a name of its own, made new, so a link a cloned repository
    /// carries under a fixed name (One.cs.passenger-save) never takes the save elsewhere.</summary>
    [AvaloniaFact]
    public void A_save_never_writes_through_a_link_left_where_its_temporary_file_would_go()
    {
        string path = Path.Combine(folder, "One.cs");
        string elsewhere = Path.Combine(TestFolders.Create("save-elsewhere-"), "kept.txt");
        File.WriteAllText(path, "class One { }\n");
        File.WriteAllText(elsewhere, "keep this file\n");
        string planted = path + FileText.SaveSuffix;
        TestFolders.Link(planted, elsewhere);
        MainWindow window = Open();
        Edit(window, "One.cs", "public ");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));

        Assert.Equal("public class One { }\n", File.ReadAllText(path));
        Assert.True(File.ReadAllText(elsewhere) == "keep this file\n", "The save was written through a link beside the file, to the file it points at");
        Assert.NotNull(new FileInfo(planted).LinkTarget);
        Assert.Equal([planted], Directory.EnumerateFiles(folder, "*" + FileText.SaveSuffix));
        window.Close();
    }

    [AvaloniaFact]
    public void Edits_are_saved_when_the_focus_goes_to_the_terminal_unless_settings_say_not_and_before_another_folder_opens()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open();
        Edit(window, "One.cs", "public ");
        Ui.Run(window, AppActions.ShowTerminal);
        Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the terminal to take the focus");
        Assert.Equal("public class One { }\n", File.ReadAllText(path));

        window.ApplySettings(window.Settings with { SaveOnDeactivation = false });
        Ui.Run(window, AppActions.ShowTerminal);
        Ui.Settle();
        Edit(window, "One.cs", "sealed ");
        Ui.Run(window, AppActions.ShowTerminal);
        Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the terminal to take the focus");
        Assert.True(window.EditorArea.Active!.IsModified, "The terminal's focus saved although Settings turned that off");
        Assert.Equal("public class One { }\n", File.ReadAllText(path));

        window.OpenFolder(TestFolders.Create("other-"));
        Assert.Equal("sealed public class One { }\n", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public void An_edit_left_alone_is_saved_once_the_IDE_has_been_idle_as_long_as_settings_say()
    {
        string path = Path.Combine(folder, "One.cs");
        File.WriteAllText(path, "class One { }\n");
        MainWindow window = Open();
        window.ApplySettings(window.Settings with { SaveWhenIdle = true, IdleSeconds = 1 });
        Edit(window, "One.cs", "static ");
        Assert.True(window.EditorArea.Active!.IsModified);
        Ui.WaitFor(() => !window.EditorArea.Active!.IsModified, "the idle save", TimeSpan.FromSeconds(10));
        Assert.Equal("static class One { }\n", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public void A_save_on_macOS_keeps_the_files_Finder_tags_its_access_control_list_and_its_creation_date()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Finder's tags, macOS's ACLs and a creation date a program may set are macOS's");
        string path = Path.Combine(folder, "Tagged.cs");
        File.WriteAllText(path, "class Tagged { }\n");
        Run("xattr", "-w", "com.apple.metadata:_kMDItemUserTags", "Red", path);
        Run("xattr", "-w", "com.apple.TextEncoding", "utf-8;134217984", path);
        Run("chmod", "+a", "everyone allow read", path);
        DateTime created = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        MainWindow window = Open();
        Edit(window, "Tagged.cs", "public ");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));

        Assert.Equal("public class Tagged { }\n", File.ReadAllText(path));
        Assert.True(Run("xattr", "-p", "com.apple.metadata:_kMDItemUserTags", path).Trim() == "Red", "The save took the file's Finder tag away");
        Assert.Equal("utf-8;134217984", Run("xattr", "-p", "com.apple.TextEncoding", path).Trim());
        Assert.Contains("everyone allow read", Run("ls", "-le", path), StringComparison.Ordinal);
        Assert.True(File.GetCreationTimeUtc(path) == created, $"The save made the file anew: created {File.GetCreationTimeUtc(path):u}, not {created:u}");
        window.Close();
    }

    [AvaloniaFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_save_on_Windows_keeps_the_files_attributes_its_explicit_access_control_list_and_its_alternate_data_streams()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "File attributes such as Hidden, an explicit ACL and alternate data streams are NTFS's");
        string path = Path.Combine(folder, "Hidden.cs");
        File.WriteAllText(path, "class Hidden { }\n");
        // As a download is marked, and as a person shares one file with everyone or hides it.
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        FileInfo info = new(path);
        System.Security.AccessControl.FileSecurity security = info.GetAccessControl();
        System.Security.Principal.SecurityIdentifier everyone = new(System.Security.Principal.WellKnownSidType.WorldSid, null);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(everyone, System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        MainWindow window = Open();
        Edit(window, "Hidden.cs", "public ");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));

        Assert.Equal("public class Hidden { }\n", File.ReadAllText(path));
        Assert.False(window.EditorArea.Active!.IsModified, $"The hidden file was not saved: {window.Status}");
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden), $"The save took the file's Hidden attribute away: {File.GetAttributes(path)}");
        bool shared = new FileInfo(path).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(System.Security.Principal.SecurityIdentifier))
            .OfType<System.Security.AccessControl.FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(everyone) && r.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.Read));
        Assert.True(shared, "The save gave the file its folder's inherited ACL in place of its own, which let everyone read it");
        Assert.True(File.Exists(path + ":Zone.Identifier") && File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal),
            "The save took the file's Zone.Identifier stream away");
        window.Close();
    }

    [AvaloniaFact]
    public void A_read_only_file_is_never_written_by_a_save_and_its_first_edit_and_each_save_ask_to_make_it_writable()
    {
        // As Perforce or TFVC leave a file not checked out: macOS and Linux would let a save move a new file over it.
        string path = Path.Combine(folder, "Locked.cs");
        File.WriteAllText(path, "class Locked { }\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        MainWindow window = Open();
        List<string> asked = [];
        bool agree = false;
        window.Confirm = (title, text, yes) =>
        {
            asked.Add($"{title}: {text} [{yes}]");
            return Task.FromResult(agree);
        };

        Edit(window, "Locked.cs", "public ");
        Assert.Equal(["Clear Read-Only Status: Locked.cs is read-only, so its changes cannot be saved. Make it writable? [Make Writable]"], asked);
        Assert.Equal("Locked.cs is read-only: its changes are not saved", window.Status);

        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Ui.WaitFor(() => asked.Count == 2, "Save to ask again");
        Assert.Equal("class Locked { }\n", File.ReadAllText(path));
        Assert.True(new FileInfo(path).IsReadOnly, "The save made the read-only file writable");
        Assert.True(window.EditorArea.Active!.IsModified, "The read-only file's changes were taken for saved");

        Task<string> saving = new Passenger.App.Mcp.PassengerTools(window).SaveAll();
        Ui.WaitFor(() => saving.IsCompleted, "save_all's answer");
        Assert.Equal("Saved 0 of 1 files; not saved: Locked.cs (read-only)", Assert.IsType<ModelContextProtocol.McpException>(saving.Exception?.InnerException).Message);
        Assert.Equal("class Locked { }\n", File.ReadAllText(path));

        agree = true;
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Ui.WaitFor(() => !window.EditorArea.Active!.IsModified, "the save once the file is writable");
        Assert.Equal("public class Locked { }\n", File.ReadAllText(path));
        Assert.False(new FileInfo(path).IsReadOnly);
        window.Close();
    }

    [AvaloniaFact]
    public void A_window_closed_with_a_read_only_files_changes_asks_and_closes_with_the_file_made_writable_or_left_as_it_was()
    {
        string path = Path.Combine(folder, "Locked.cs");
        File.WriteAllText(path, "class Locked { }\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        MainWindow window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(false);
        Edit(window, "Locked.cs", "public ");
        window.Close();
        ChoiceDialog asked = Asked(window);
        Assert.Contains("Locked.cs (read-only)", asked.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).OfType<string>().Single(t => t.Contains("could not be saved", StringComparison.Ordinal)), StringComparison.Ordinal);
        Ui.ClickButton(asked, "Cancel");
        Assert.True(window.IsVisible, "Cancel closed the window, and the file's changes with it");
        window.Close();
        Ui.ClickButton(Asked(window), "Close Without Saving");
        Ui.WaitFor(() => !window.IsVisible, "the window to close without the file's changes");
        Assert.Equal("class Locked { }\n", File.ReadAllText(path));
        Assert.True(new FileInfo(path).IsReadOnly);

        window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(false);
        Edit(window, "Locked.cs", "public ");
        window.Close();
        Ui.ClickButton(Asked(window), "Make Writable");
        Ui.WaitFor(() => !window.IsVisible, "the window to close once the file was saved");
        Assert.Equal("public class Locked { }\n", File.ReadAllText(path));
        Assert.False(new FileInfo(path).IsReadOnly);
    }

    /// <summary>The question a window closed with changes it cannot save asks, held to the dialogs' rules.</summary>
    private static ChoiceDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "the window to ask about the changes it cannot save");
        ChoiceDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<ChoiceDialog>().Single());
        Assert.Equal("Clear Read-Only Status", asked.Title);
        return asked;
    }

    /// <summary>What a program of the system says, failing when it fails.</summary>
    private static string Run(string program, params string[] arguments)
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{program} {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private MainWindow Open() => Ui.OpenFolder(folder);

    private static string? EncodingShown(MainWindow window) => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(window, "EncodingText")!.Text;

    /// <summary>Types text at the start of a file, leaving it unsaved.</summary>
    private static void Edit(MainWindow window, string file, string text)
    {
        window.OpenFile(file);
        Ui.Settle();
        window.EditorArea.CodeEditor.Selections = Text.SelectionSet.Single(0);
        window.KeyTextInput(text);
        Ui.Settle();
    }
}
