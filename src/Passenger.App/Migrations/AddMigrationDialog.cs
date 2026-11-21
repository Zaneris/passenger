using Avalonia.Controls;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>What Add Migration… asks: the migration's name, and the folder of the project its files go in (null for the
/// one dotnet ef chooses).</summary>
public sealed record MigrationAdd(string Name, string? Folder);

/// <summary>Add Migration…'s question for a context: its name, and its output folder, from the project's, offering the
/// folders holding the context's migrations (the last one's first), the one last given filled in, a folder picked inside
/// the project taken too; empty, dotnet ef's own, which the note under it names. Closes with the migration asked for, or
/// null for Cancel; a name that is no class's or a folder outside the project said beside the buttons.</summary>
public sealed class AddMigrationDialog : IdeWindow
{
    private readonly ProjectModel project;

    /// <summary>The folder of the context's last migration, beside which dotnet ef puts it given none; null for none.</summary>
    private readonly string? last;
    private readonly Func<Task<string?>> pick;
    private readonly TextBlock into = FormRows.Note();
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, Classes = { StyleClasses.Error } };

    /// <param name="holding">The folders holding the context's migrations, from the project's, the last one's first.</param>
    /// <param name="last">The folder of the context's last migration, beside which dotnet ef puts it given none; null for none.</param>
    /// <param name="kept">The folder last given for the context, filled in; null for none.</param>
    /// <param name="pick">Asks the person for a folder, starting in the project's; null for none.</param>
    public AddMigrationDialog(ProjectModel project, string context, IReadOnlyList<string> holding, string? last, string? kept, Func<Task<string?>> pick)
    {
        FormRows.Scope(this);
        this.project = project;
        this.last = last;
        this.pick = pick;
        Title = $"Add Migration to {context}";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        NameBox = new TextBox { Name = "MigrationName", PlaceholderText = "AddCustomerEmail" };
        BrowseRow folderRow = BrowseRow.Offering(holding, BrowseAsync);
        FolderBox = folderRow.Offered!;
        FolderBox.Name = "MigrationFolder";
        FolderBox.PlaceholderText = last ?? EfOutputFolders.Default;
        FolderBox.Text = kept ?? string.Empty;
        FolderBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty)
            {
                ShowInto();
            }
        };
        Button add = DialogFooter.Default("Add");
        add.Click += (_, _) => Add();
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children =
            {
                FormRows.Of("Name", NameBox, [FormRows.Note("A C# class's name, as the migration is one")]),
                FormRows.Of("Output folder", folderRow, [into]),
            },
        };
        Content = DialogFooter.Page(new StackPanel { [DialogFooter.InsetProperty] = true, Children = { fields } },
            DialogFooter.OkCancel(add, () => Close(null), problem));
        ShowInto();
        Opened += (_, _) => NameBox.Focus();
    }

    public TextBox NameBox { get; }

    /// <summary>The output folder's field: typed, or one of the folders holding the context's migrations chosen.</summary>
    public ComboBox FolderBox { get; }

    /// <summary>Where the migration goes, as the note under the folder says it.</summary>
    public string Into => into.Text ?? string.Empty;

    /// <summary>Why it cannot be added as it stands, as said beside the buttons; empty when it can.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>Add: closes with the migration asked for. Null once closed, else why not, said beside the buttons.</summary>
    public string? Add()
    {
        string name = NameBox.Text?.Trim() ?? string.Empty;
        (string? folder, string? unfit) = EfOutputFolders.Read(FolderBox.Text, project);
        string? why = MigrationsController.WhyNotNamed(name) ?? unfit;
        problem.Text = why;
        if (why is null)
        {
            Close(new MigrationAdd(name, folder));
        }
        return why;
    }

    /// <summary>The note under the folder: where the migration's files go, the folder dotnet ef chooses where none is given.</summary>
    private void ShowInto()
    {
        (string? folder, string? unfit) = EfOutputFolders.Read(FolderBox.Text, project);
        into.Text = unfit ?? (folder is not null ? $"Into {Shown(folder)}"
            : last is not null ? $"Empty: beside its last migration, in {Shown(last)}" : $"Empty: dotnet ef's default, {Shown(EfOutputFolders.Default)}");
        problem.Text = string.Empty;
    }

    /// <summary>A folder of the project as the note says it, the project's own by its name.</summary>
    private string Shown(string folder) => folder == "." ? project.Name : $"{project.Name}/{folder}";

    private async Task BrowseAsync()
    {
        if (await pick() is not { } picked)
        {
            return;
        }
        (string? folder, string? unfit) = EfOutputFolders.Read(picked, project);
        if (unfit is not null)
        {
            problem.Text = unfit;
            return;
        }
        FolderBox.Text = folder;
    }
}
