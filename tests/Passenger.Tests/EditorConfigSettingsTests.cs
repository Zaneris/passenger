using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Xaml.Features;

namespace Passenger.Tests;

/// <summary>Settings says, under each row a file's .editorconfig can say over (a final line break, XAML's and GDScript's
/// layouts), what the open folder's .editorconfig files set over it, naming the file, the section's glob and the value, for
/// the files the row is for, and follows the files as they are written and deleted; a row's note names its keys from its
/// declaration alone. Every key the XAML and GDScript
/// layouts read is declared on the rows whose settings it changes, and on no other.</summary>
public sealed class EditorConfigSettingsTests
{
    private const string LineLength = "Line length (columns)";
    private const string FinalLineBreak = "Ensure a final line break on save";

    internal void Each_row_names_what_the_folders_editorconfig_files_set_over_it_and_follows_them_as_they_change()
    {
        string folder = TestFolders.Create("editorconfig-settings-");
        string top = TestFolders.Write(folder, ".editorconfig", "root = true\n\n[*]\ninsert_final_newline = true\n\n[*.gd]\nmax_line_length = 120\n");
        TestFolders.Write(folder, "scripts/.editorconfig", "[{player,enemy}.gd]\ngdscript_trailing_comma = false\n");
        TestFolders.Write(folder, "scripts/player.gd", "extends Node\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        SettingsDialog settings = window.ShowSettings();
        try
        {
            settings.Page = SettingsDialog.EditorPage;
            Ui.Settle();
            Assert.Equal("./.editorconfig sets insert_final_newline = true for *", settings.SaidOver(FinalLineBreak));
            Assert.True(settings.SaidOver("Reformat on save") is null, "A row no .editorconfig key says over said something was set over it");
            settings.Page = SettingsDialog.GDScriptCodeStylePage;
            Ui.Settle();
            const string Wide = "./.editorconfig sets max_line_length = 120 for *.gd";
            Assert.Equal(Wide, settings.SaidOver(LineLength));
            Assert.Equal(Wide, settings.SaidOver("Wrap long lines inside brackets"));
            Assert.Equal("./scripts/.editorconfig sets gdscript_trailing_comma = false for {player,enemy}.gd",
                settings.SaidOver("Trailing comma in multi-line arrays, dictionaries and enums"));
            Assert.True(settings.SaidOver("Blank lines around functions and classes") is null,
                "A row whose key no .editorconfig in the folder sets said something was set over it");
            Assert.True(NoteOf(settings, LineLength) == "The style guide's is 100. A file's .editorconfig says over this by max_line_length.",
                $"Line length's note does not end in the words its declaration gives: {NoteOf(settings, LineLength)}");
            // At the narrowest the line stands under the note, wrapping within the page.
            settings.Width = settings.MinWidth;
            Ui.Settle();
            OptionRow row = RowOf(settings, LineLength);
            Assert.True(row.IsStacked && row.SaidOver!.Bounds.Top >= row.Note!.Bounds.Bottom && row.SaidOver.Bounds.Right <= row.Bounds.Width + 0.5,
                $"At {settings.Bounds.Width:0} wide, what is set over Line length stands at {row.SaidOver!.Bounds}, its note at {row.Note!.Bounds}, in a row {row.Bounds.Width:0} wide");
            settings.Page = SettingsDialog.XamlCodeStylePage;
            Ui.Settle();
            Assert.True(settings.SaidOver("Right margin (columns)") is null, "XAML's right margin was said to be set over by a max_line_length set for scripts alone");
            settings.Query = "max_line_length";
            Ui.Settle();
            Assert.True(settings.Pages.SequenceEqual([SettingsDialog.XamlCodeStylePage, SettingsDialog.GDScriptCodeStylePage]),
                $"A search for the key an .editorconfig names found the pages {string.Join(", ", settings.Pages)}");
            settings.Query = string.Empty;

            File.WriteAllText(top, "root = true\n\n[*.{gd,axaml}]\nmax_line_length = 80\n");
            const string Narrow = "./.editorconfig sets max_line_length = 80 for *.{gd,axaml}";
            Ui.WaitFor(() => settings.SaidOver("Right margin (columns)") == Narrow,
                () => $"XAML's right margin says \"{settings.SaidOver("Right margin (columns)")}\" once the .editorconfig set max_line_length for .axaml files too");
            Assert.Equal(Narrow, settings.SaidOver(LineLength));

            Assert.True(settings.SaidOver(FinalLineBreak) is null, "The final line break was still said to be set once the .editorconfig no longer set it");

            File.Delete(top);
            Ui.WaitFor(() => settings.SaidOver(LineLength) is null,
                () => $"Line length still says \"{settings.SaidOver(LineLength)}\" once the .editorconfig setting it was deleted");
            Assert.NotNull(settings.SaidOver("Trailing comma in multi-line arrays, dictionaries and enums"));
        }
        finally
        {
            settings.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Every_key_the_xaml_and_gdscript_layouts_read_is_declared_on_the_rows_whose_settings_it_changes_and_no_note_names_one_by_hand()
    {
        AppSettings plain = new();
        HashSet<string> keys = new(StringComparer.Ordinal);
        plain.XamlFormat.Over(key => Asked(key, keys));
        plain.GDScriptFormat.Over(key => Asked(key, keys));
        string[] values =
        [
            "true", "false", "0", "1", "2", "3", "80", "120", "off", "x:Name; *",
            .. typeof(XamlFormatSettings).GetProperties().Select(p => p.PropertyType).Where(t => t.IsEnum).SelectMany(Enum.GetNames),
        ];
        Setting[] declaring = [.. Declared.Every.Where(s => s.Row?.Over is not null)];
        Dictionary<Setting, string> kept = Declared.Every.ToDictionary(s => s, s => Assert.Single(SettingMembers.Changed(plain, s.Other(plain))));
        List<string> wrong = [];
        foreach (string key in keys)
        {
            HashSet<string> changed = new(values.SelectMany(value => SettingMembers.Changed(plain, plain with
            {
                XamlFormat = plain.XamlFormat.Over(k => k == key ? value : null),
                GDScriptFormat = plain.GDScriptFormat.Over(k => k == key ? value : null),
            })), StringComparer.Ordinal);
            string[] changes = [.. Declared.Every.Where(s => changed.Contains(kept[s])).Select(s => s.Row?.Label.English ?? kept[s])];
            string[] declared = [.. declaring.Where(s => s.Row!.Over!.Names(key)).Select(s => s.Row!.Label.English)];
            if (!changes.SequenceEqual(declared))
            {
                wrong.Add($"{key} changes {Listed(changes)} but is declared on {Listed(declared)}");
            }
        }
        foreach (Setting setting in declaring.Where(s => kept[s].Split('.')[0] is "xamlFormat" or "gdScriptFormat"))
        {
            EditorConfigOver over = setting.Row!.Over!;
            wrong.AddRange(over.Keys.Concat(over.Aliases).Where(k => !keys.Contains(k)).Select(k => $"{setting.Row.Label.English} declares {k}, which no layout reads"));
        }
        wrong.AddRange(Declared.Every.Select(s => s.Row).OfType<SettingRow>().Where(r => r.Note?.English.Contains("says over this", StringComparison.Ordinal) == true)
            .Select(r => $"{r.Label.English}'s note names its .editorconfig keys by hand"));
        Assert.True(wrong.Count == 0, $"Settings says a key of a file's .editorconfig over rows it does not change, or misses rows it does:\n{string.Join("\n", wrong)}");

        static string Listed(string[] labels) => labels.Length == 0 ? "no row" : string.Join(", ", labels);
    }

    private static string? Asked(string key, HashSet<string> keys)
    {
        keys.Add(key);
        return null;
    }

    /// <summary>The note under a row of the page shown, by its label.</summary>
    private static string? NoteOf(SettingsDialog settings, string label) => RowOf(settings, label).Note?.Text;

    private static OptionRow RowOf(SettingsDialog settings, string label) => settings.GetVisualDescendants().OfType<OptionRow>().Single(r => r.Label.Text == label);
}
