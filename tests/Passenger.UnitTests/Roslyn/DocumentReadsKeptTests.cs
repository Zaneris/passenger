using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Roslyn;

/// <summary>What the extension keeps of each document a project's whole read walks (the configuration keys and connection
/// strings appsettings.json completion and a compose file's check ask for), held to Roslyn's own versions: an edit inside a
/// body reads again only its document, and an edit of declarations also the documents whose code was bound, so what they
/// found follows an options class, a constant or an initializer declared in another file, the parse options, and documents
/// come and gone.</summary>
public sealed class DocumentReadsKeptTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private const string Options = "public sealed class Options\n{\n    public string Name { get; set; }\n}\n";
    private const string Keys = "public static class Keys\n{\n    public const string Section = \"Shop\";\n}\n";
    private const string Reader = "public sealed class Reader\n{\n    public string Read()\n    {\n        return Keys.Section;\n    }\n}\n";
    private const string Plain = "public sealed class Plain\n{\n    public int Count()\n    {\n        return 1;\n    }\n#if TRADE\n    public string Traded() => Keys.Section;\n#endif\n}\n";

    private static AdhocWorkspace Made()
    {
        AdhocWorkspace workspace = new();
        ProjectId id = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), "Shop", "Shop", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), parseOptions: new CSharpParseOptions(),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        foreach ((string name, string text) in new[] { ("Options.cs", Options), ("Keys.cs", Keys), ("Reader.cs", Reader), ("Plain.cs", Plain) })
        {
            workspace.AddDocument(id, name, SourceText.From(text));
        }
        return workspace;
    }

    /// <summary>A reader as the configuration's: a document that names Keys has its code bound, finding the constants it reads
    /// and the Options class's properties as declared ('=' after one with an initializer); any other, nothing.</summary>
    private static async Task<DocumentReadsKept<string>.Read> ReadAsync(Document document, ConcurrentQueue<string> read, CancellationToken cancel)
    {
        read.Enqueue(document.Name);
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel))!;
        MemberAccessExpressionSyntax[] uses = [.. root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression is IdentifierNameSyntax { Identifier.ValueText: "Keys" })];
        if (uses.Length == 0)
        {
            return new(string.Empty, false);
        }
        SemanticModel model = (await document.GetSemanticModelAsync(cancel))!;
        INamedTypeSymbol? options = model.Compilation.GetTypeByMetadataName("Options");
        IEnumerable<string> properties = options is null ? [] : options.GetMembers().OfType<IPropertySymbol>().Select(p => p.Name
            + (p.DeclaringSyntaxReferences.Any(r => r.GetSyntax(cancel) is PropertyDeclarationSyntax { Initializer: not null }) ? "=" : string.Empty));
        return new(string.Join(",", uses.Select(u => model.GetConstantValue(u, cancel).Value as string ?? "?")) + "|" + string.Join(",", properties), true);
    }

    /// <summary>The project's documents read through <paramref name="kept"/>: the names of those read again, in order, and
    /// what each holds by name.</summary>
    private static async Task<(string Read, IReadOnlyDictionary<string, string> Found)> ReadAllAsync(AdhocWorkspace workspace, DocumentReadsKept<string> kept)
    {
        ConcurrentQueue<string> read = [];
        Project project = workspace.CurrentSolution.Projects.Single();
        IReadOnlyList<string> found = await kept.OfAsync(project, project.Documents, (document, cancel) => ReadAsync(document, read, cancel), Cancel);
        return (string.Join(" ", read.Order(StringComparer.Ordinal)), project.Documents.Zip(found).ToDictionary(each => each.First.Name, each => each.Second));
    }

    /// <summary>A document's text changed as the language server changes it, one span replaced.</summary>
    private static async Task ChangeAsync(AdhocWorkspace workspace, string name, string old, string now)
    {
        Document document = workspace.CurrentSolution.Projects.Single().Documents.Single(d => d.Name == name);
        SourceText text = await document.GetTextAsync(Cancel);
        int at = text.ToString().IndexOf(old, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{name} has no '{old}' to change");
        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(document.Id, text.WithChanges(new TextChange(new TextSpan(at, old.Length), now)))),
            $"The workspace took no change of {name}");
    }

    [Fact]
    public async Task An_edit_inside_a_body_reads_again_only_its_own_document()
    {
        using AdhocWorkspace workspace = Made();
        DocumentReadsKept<string> kept = new();
        (string read, IReadOnlyDictionary<string, string> found) = await ReadAllAsync(workspace, kept);
        Assert.Equal("Keys.cs Options.cs Plain.cs Reader.cs", read);
        Assert.Equal("Shop|Name", found["Reader.cs"]);

        (read, _) = await ReadAllAsync(workspace, kept);
        Assert.True(read.Length == 0, $"Asked again with nothing changed, {read} was read again: every completion would walk the project");

        await ChangeAsync(workspace, "Plain.cs", "return 1;", "return 2;");
        (read, _) = await ReadAllAsync(workspace, kept);
        Assert.True(read == "Plain.cs", $"A key typed in Plain.cs's body read again {read}: a document whose code was bound is read again at every key");

        await ChangeAsync(workspace, "Reader.cs", "return Keys.Section;", "int shown = 1;\n        return Keys.Section;");
        (read, found) = await ReadAllAsync(workspace, kept);
        Assert.True(read == "Reader.cs", $"A key typed in Reader.cs's body read again {read}");
        Assert.Equal("Shop|Name", found["Reader.cs"]);
    }

    [Fact]
    public async Task A_declaration_edited_in_another_file_reads_again_the_documents_whose_code_was_bound()
    {
        using AdhocWorkspace workspace = Made();
        DocumentReadsKept<string> kept = new();
        await ReadAllAsync(workspace, kept);

        await ChangeAsync(workspace, "Options.cs", "    public string Name { get; set; }\n", "    public string Name { get; set; }\n    public int Port { get; set; }\n");
        (string read, IReadOnlyDictionary<string, string> found) = await ReadAllAsync(workspace, kept);
        Assert.Equal("Options.cs Reader.cs", read);
        Assert.True(found["Reader.cs"] == "Shop|Name,Port", $"A property added to the options class left Reader.cs's read as {found["Reader.cs"]}: completion would never offer it");

        await ChangeAsync(workspace, "Keys.cs", "\"Shop\"", "\"Store\"");
        (read, found) = await ReadAllAsync(workspace, kept);
        Assert.Equal("Keys.cs Reader.cs", read);
        Assert.True(found["Reader.cs"] == "Store|Name,Port", $"A constant's new value left Reader.cs's read as {found["Reader.cs"]}: the key read would be the old one");

        await ChangeAsync(workspace, "Options.cs", "public string Name { get; set; }", "public string Name { get; set; } = \"\";");
        (read, found) = await ReadAllAsync(workspace, kept);
        Assert.Equal("Options.cs Reader.cs", read);
        Assert.True(found["Reader.cs"] == "Store|Name=,Port", $"An initializer given to Name left Reader.cs's read as {found["Reader.cs"]}: the key would still be said to need a value");
    }

    [Fact]
    public async Task Parse_options_and_documents_come_and_gone_are_followed()
    {
        using AdhocWorkspace workspace = Made();
        DocumentReadsKept<string> kept = new();
        (_, IReadOnlyDictionary<string, string> found) = await ReadAllAsync(workspace, kept);
        Assert.Equal(string.Empty, found["Plain.cs"]);

        Project project = workspace.CurrentSolution.Projects.Single();
        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.WithProjectParseOptions(project.Id, new CSharpParseOptions(preprocessorSymbols: ["TRADE"]))),
            "The workspace took no parse options");
        (string read, IReadOnlyDictionary<string, string> traded) = await ReadAllAsync(workspace, kept);
        Assert.True(traded["Plain.cs"] == "Shop|Name", $"With TRADE defined, Plain.cs's read is '{traded["Plain.cs"]}' after reading {read}: the code its #if holds goes unread");

        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.AddDocument(DocumentId.CreateNewId(project.Id), "Later.cs",
            SourceText.From("public sealed class Later\n{\n    public string Key => Keys.Section;\n}\n"))), "The workspace took no new document");
        (read, found) = await ReadAllAsync(workspace, kept);
        Assert.Contains("Later.cs", read, StringComparison.Ordinal);
        Assert.Equal("Shop|Name", found["Later.cs"]);

        Document later = workspace.CurrentSolution.Projects.Single().Documents.Single(d => d.Name == "Later.cs");
        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(later.Id)), "The workspace took no removal of Later.cs");
        (_, found) = await ReadAllAsync(workspace, kept);
        Assert.Equal(["Keys.cs", "Options.cs", "Plain.cs", "Reader.cs"], found.Keys.Order(StringComparer.Ordinal));
    }
}
