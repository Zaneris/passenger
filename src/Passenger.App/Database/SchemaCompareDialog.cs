using Avalonia.Controls;

namespace Passenger.App.Database;

/// <summary>Asks which schema to make as the chosen one is: a connection of the same database kind, its database and its
/// schema (MySQL's schema is its database; SQLite's file is its one schema).</summary>
public sealed class SchemaCompareDialog : IdeWindow
{
    /// <summary>The schema chosen to be made as the source is.</summary>
    public sealed record Answer(DatabaseSource Target, string Database, string Schema);

    private readonly FormDropDown target = new() { Name = "CompareTarget" };
    private readonly TextBox database = new() { Name = "CompareDatabase" };
    private readonly TextBox schema = new() { Name = "CompareSchema" };

    public SchemaCompareDialog(DatabaseSource source, string sourceDatabase, string sourceSchema, IReadOnlyList<DatabaseSource> targets)
    {
        Title = "Compare Schemas";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        target.ItemsSource = targets.Select(t => t.ToString()).ToList();
        StackPanel fields = new() { Spacing = FormRows.Between };
        FormRows.Scope(fields);
        string from = sourceSchema.Length > 0 && sourceSchema != sourceDatabase ? $"{sourceDatabase} · {sourceSchema}" : sourceDatabase;
        fields.Children.Add(FormRows.Of("Source", new TextBlock { Text = Containers.ContainerSecrets.InSaid($"{from} of {source.Name}") }));
        fields.Children.Add(FormRows.Of("Target", target));
        Grid databaseRow = FormRows.Of("Database", database);
        databaseRow.IsVisible = source.Kind != DatabaseKind.Sqlite;
        fields.Children.Add(databaseRow);
        Grid schemaRow = FormRows.Of("Schema", schema);
        schemaRow.IsVisible = source.Kind is DatabaseKind.Postgres or DatabaseKind.SqlServer;
        fields.Children.Add(schemaRow);
        target.SelectionChanged += (_, _) =>
        {
            if (target.SelectedIndex >= 0)
            {
                database.Text = targets[target.SelectedIndex].Database;
            }
        };
        schema.Text = sourceSchema;
        target.SelectedIndex = targets.Count > 1 && targets[0].Id == source.Id ? 1 : 0;
        Button compare = DialogFooter.Default("Compare");
        compare.Name = "CompareSchemas";
        compare.Click += (_, _) =>
        {
            if (target.SelectedIndex >= 0 && database.Text?.Trim() is { Length: > 0 } named)
            {
                string chosenSchema = source.Kind == DatabaseKind.MySql ? named : source.Kind == DatabaseKind.Sqlite ? string.Empty : schema.Text?.Trim() ?? string.Empty;
                Close(new Answer(targets[target.SelectedIndex], named, chosenSchema));
            }
        };
        Content = DialogFooter.Page(new StackPanel
        {
            [DialogFooter.InsetProperty] = true,
            Spacing = FormRows.Between,
            Children =
            {
                fields,
                MutedText.Wrapped("The differences are written as a script into the target's console, which runs nothing until you run it."),
            },
        }, DialogFooter.OkCancel(compare, () => Close(null)));
    }
}
