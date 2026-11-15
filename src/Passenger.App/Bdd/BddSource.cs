using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>What the editor's commands read of the folder's BDD: an open feature's reading, a project's bindings, the
/// binding a C# file declares at a place, and the features a binding's project sees, open ones as the editor holds them and
/// the rest from disk. Of the window's thread; reading is off it.</summary>
internal sealed class BddSource(Func<ProjectSession> session, EditorArea editors, Func<CancellationToken, Task<StepCatalog>> readSteps)
{
    public ProjectSession Session => session();

    public EditorArea Editors => editors;

    public FeatureChecks Checks => session().Features;

    /// <summary>The bindings as they stand, read first where a read is due.</summary>
    public Task<StepCatalog> StepsAsync(CancellationToken cancel = default) => readSteps(cancel);

    /// <summary>The bindings a file's project sees; null for a file of no Reqnroll project.</summary>
    public ProjectSteps? StepsOf(string path) => session().Steps.For(session().ProjectOf(path)?.Path);

    /// <summary>An open feature's reading of its text and the bindings as they stand, a read of the bindings that is due made first.</summary>
    public async Task<FeatureAnalysis?> AnalysisAsync(OpenDocument document)
    {
        if (session().Steps.Wants)
        {
            await readSteps(CancellationToken.None);
        }
        return await Checks.CurrentAsync(document);
    }

    /// <summary>The step definition a C# file declares at an offset: on its method's name or its expression's string.</summary>
    public StepBinding? BindingAt(OpenDocument document, int offset)
    {
        TextSnapshot text = document.Text.Snapshot;
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        LspPosition at = ServerPlaces.ServerPositionOf(text, new TextPosition(line, offset - text.GetLineStart(line)));
        foreach (ProjectSteps steps in session().Steps.Projects)
        {
            foreach (StepBinding binding in steps.Bindings)
            {
                if (On(binding.MethodPlace, binding.Method.Length) || On(binding.ExpressionPlace, binding.ExpressionLength))
                {
                    return binding;
                }
            }
        }
        return null;

        bool On(BindingPlace? place, int length) => place is not null && PathNames.Same(place.Path, document.Path) && place.Line == at.Line
            && at.Character >= place.Column && at.Character <= place.Column + length;
    }

    /// <summary>The Reqnroll projects that see a binding (its own, and those that reference the project declaring it).</summary>
    public IReadOnlyList<ProjectSteps> ProjectsSeeing(StepBinding binding) =>
        [.. session().Steps.Projects.Where(p => p.Bindings.Any(b => b.SameAs(binding)))];

    /// <summary>Every feature file of the projects given, each read against its own project's bindings.</summary>
    public async Task<IReadOnlyList<(string Path, FeatureAnalysis Analysis)>> FeaturesAsync(IEnumerable<ProjectSteps> projects)
    {
        List<(string, FeatureAnalysis)> read = [];
        foreach (ProjectSteps project in projects)
        {
            if (session().Projects.FirstOrDefault(p => PathNames.Same(p.Path, project.Project)) is not { } model)
            {
                continue;
            }
            string[] files = await BackgroundWork.Run(() => FolderWalk.ProjectFiles(model, ".feature").ToArray());
            foreach (string file in files)
            {
                read.Add((file, await AnalysisOfAsync(file)));
            }
        }
        return read;
    }

    /// <summary>A feature file's reading: an open one's as the editor holds it, any other's from disk.</summary>
    public async Task<FeatureAnalysis> AnalysisOfAsync(string path)
    {
        if (editors.Documents.FirstOrDefault(d => PathNames.Same(d.Path, path)) is { } open && await Checks.CurrentAsync(open) is { } analysis)
        {
            return analysis;
        }
        TextSnapshot text = editors.TextOf(path) ?? TextSnapshot.From(await BackgroundWork.Run(() => File.Exists(path) ? File.ReadAllText(path) : string.Empty));
        return await Checks.ReadAsync(path, text);
    }

    /// <summary>A step's place in its feature file as the server names places, for the usages and declarations lists.</summary>
    public LspLocation PlaceOf(string path, FeatureAnalysis analysis, FeatureStep step) =>
        Declared.Served([(path, new TextPosition(step.Line, step.TextColumn))], p => PathNames.Same(p, path) ? analysis.Text : ServerPlaces.Texts(editors)(p))[0];

    /// <summary>A binding's method as a place to go to.</summary>
    public static LspLocation? PlaceOf(BindingPlace? place) => place is null ? null
        : new LspLocation(DocumentUris.FromPath(place.Path), new LspRange(new LspPosition(place.Line, place.Column), new LspPosition(place.Line, place.Column)));

    /// <summary>The namespace a folder's code takes: its project's RootNamespace (else its name), then the folders below,
    /// each name made a C# identifier.</summary>
    public string NamespaceFor(string folder)
    {
        string full = PathNames.Canonical(folder);
        ProjectModel? project = session().ProjectOf(Path.Combine(full, "_.cs"));
        string root = project?.Property(Passenger.Host.CarriedProperties.RootNamespace) is { Length: > 0 } named ? named
            : project is null ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(project.Path);
        string below = project is null ? "." : Path.GetRelativePath(project.Folder, full);
        IEnumerable<string> parts = [root, .. below == "." ? [] : below.Split(Path.DirectorySeparatorChar)];
        return string.Join('.', parts.SelectMany(p => p.Split('.', StringSplitOptions.RemoveEmptyEntries)).Select(Identifier));

        static string Identifier(string part)
        {
            string name = new([.. part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
            return char.IsDigit(name[0]) ? "_" + name : name;
        }
    }
}
