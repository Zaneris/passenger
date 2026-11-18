using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Migrations;

/// <summary>What Script Migrations… asks: the migration the script starts from (the empty database first) and the one it
/// ends at (the latest last), and whether it is idempotent, checking the history table before each step so it can run on
/// a database at any of them.</summary>
public sealed record MigrationScript(string? From, string? To, bool Idempotent);

/// <summary>Script Migrations…'s question over a context's migrations: From, To and Idempotent, closing with the script
/// asked for, or null for Cancel.</summary>
public sealed class MigrationScriptDialog : IdeWindow
{
    /// <summary>The From list's first choice, the database with no migration applied.</summary>
    public const string Start = "The empty database";

    /// <summary>The To list's last choice.</summary>
    public const string Latest = "The latest migration";

    public MigrationScriptDialog(IReadOnlyList<MigrationRow> migrations, string context)
    {
        FormRows.Scope(this);
        Title = "Script Migrations";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string[] names = [.. migrations.Select(m => m.Name)];
        From = new ComboBox { Name = "ScriptFrom", ItemsSource = new[] { Start }.Concat(names).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        To = new ComboBox { Name = "ScriptTo", ItemsSource = names.Append(Latest).ToList(), SelectedIndex = names.Length, HorizontalAlignment = HorizontalAlignment.Stretch };
        Idempotent = new CheckBox { Name = "ScriptIdempotent", Content = "Idempotent: each step checks the history table first" };
        StackPanel choices = new() { Spacing = FormRows.Between, Children = { FormRows.Of("From:", From), FormRows.Of("To:", To) } };

        Button script = DialogFooter.Default("Script");
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
                new TextBlock { Text = $"Script {context}'s migrations", FontWeight = FontWeight.SemiBold },
                choices,
                Idempotent,
            },
        }, DialogFooter.OkCancel(script, () => Close(null)));
    }

    public ComboBox From { get; }

    public ComboBox To { get; }

    public CheckBox Idempotent { get; }
}
