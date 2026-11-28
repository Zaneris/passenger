using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Migrations;

/// <summary>What Script Migrations… asks: the migration the script starts from (the empty database first) and the one it
/// ends at (the latest last), and whether it is idempotent, checking the history table before each step so it can run on
/// a database at any of them.</summary>
public sealed record MigrationScript(string? From, string? To, bool Idempotent);

/// <summary>Script Migrations…'s question over a context's migrations: From, To and Idempotent, closing with the script
/// asked for, or null for Cancel.</summary>
public sealed class MigrationScriptDialog : IdeWindow
{
    public MigrationScriptDialog(IReadOnlyList<MigrationRow> migrations, string context)
    {
        FormRows.Scope(this);
        ShownWords.Set(this, TitleProperty, MigrationsWords.ScriptTitle(context));
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string[] names = [.. migrations.Select(m => m.Name)];
        // The first From is the database with no migration applied, the last To the latest migration.
        From = new FormDropDown { Name = "ScriptFrom", ItemsSource = new object[] { MigrationsWords.EmptyDatabase }.Concat(names).ToList(), SelectedIndex = 0 };
        To = new FormDropDown { Name = "ScriptTo", ItemsSource = names.Cast<object>().Append(MigrationsWords.LatestMigration).ToList(), SelectedIndex = names.Length };
        Idempotent = new CheckBox { Name = "ScriptIdempotent", [ShownWords.ContentProperty] = MigrationsWords.IdempotentChoice };
        StackPanel choices = new() { Spacing = FormRows.Between, Children = { FormRows.Of(MigrationsWords.FromLabel, From), FormRows.Of(MigrationsWords.ToLabel, To) } };

        Button script = DialogFooter.Default(MigrationsWords.Script);
        script.Click += (_, _) => Close(new MigrationScript(
            From.SelectedIndex <= 0 ? null : names[From.SelectedIndex - 1],
            To.SelectedIndex < 0 || To.SelectedIndex >= names.Length ? null : names[To.SelectedIndex],
            Idempotent.IsChecked == true));
        Content = DialogFooter.Page(new StackPanel
        {
            [DialogFooter.InsetProperty] = true,
            Spacing = Gaps.Group,
            Children =
            {
                choices,
                Idempotent,
            },
        }, DialogFooter.OkCancel(script, () => Close(null)));
    }

    public ComboBox From { get; }

    public ComboBox To { get; }

    public CheckBox Idempotent { get; }
}
