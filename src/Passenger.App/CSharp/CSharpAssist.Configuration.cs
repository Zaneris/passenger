using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>Configuration known from what the app reads, in its C#: Go to Declaration from a read to where each file the
/// project runs with gives its key, the hover card's values of it, and the fixes of a read's warning that nothing gives its
/// key, which set it in appsettings.json, or in the user secrets for a secret's.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>The configuration of the project a C# file is of (the session's <see cref="ProjectSession.ConfigurationOf(string)"/>).</summary>
    public Func<string, ConfigurationProject?>? ConfigurationOf { get; set; }

    /// <summary>Go to Declaration from a read of a configuration key in C#: where each file the project runs with gives it (its
    /// settings, a launch profile, its user secrets, a compose file, an AppHost), or saying none does; false where the caret
    /// is on no read of the project's, for the server's own. Only a string's place asks.</summary>
    private async Task<bool> ConfigurationGoTo(LspDocument lsp, int offset)
    {
        if (context.Editors.Active is not { } document || await ReadAtAsync(lsp, document, offset) is not ({ } project, { } read))
        {
            return false;
        }
        Func<string, string?> text = context.Editors.Texts().Text;
        IReadOnlyList<(GivenKey Given, LspLocation Place)> given = await BackgroundWork.Run(() =>
        {
            Dictionary<string, ServerLines?> lines = new(StringComparer.Ordinal);
            return (IReadOnlyList<(GivenKey, LspLocation)>)[.. ConfigurationFiles.Where(ConfigurationFiles.Given(project, text), read.Key, read.Kind != ConfigurationKinds.Read)
                .Select(g =>
                {
                    if (!lines.TryGetValue(g.File!, out ServerLines? counted))
                    {
                        lines[g.File!] = counted = text(g.File!) is { } written ? ServerLines.Of(TextSnapshot.From(written)) : null;
                    }
                    LspPosition start = counted?.PositionOf(Math.Min(g.Start, counted.Text.Length)) ?? new LspPosition(0, 0);
                    LspPosition end = counted?.PositionOf(Math.Min(g.End, counted.Text.Length)) ?? start;
                    return (g, new LspLocation(DocumentUris.FromPath(g.File!), new LspRange(start, end)));
                })];
        });
        if (context.Editors.Active != document)
        {
            return true;
        }
        navigation.Go([.. given.Select(g => g.Place)], offset, "Declarations", $"Nothing {project.Name} runs with gives {read.Key}",
            [.. given.Select(g => (string?)$"{Path.GetFileName(g.Given.File)}: {g.Given.Key} = {g.Given.Shown}")]);
        return true;
    }

    /// <summary>The read of a configuration key whose string an offset of a C# file is in, with its project's configuration; null
    /// elsewhere, and asking nothing where the offset is in no string.</summary>
    private async Task<(ConfigurationProject Project, ConfigurationKey Read)?> ReadAtAsync(LspDocument lsp, OpenDocument document, int offset, CancellationToken cancel = default)
    {
        if (context.Syntax is not { } parsed || !ReferenceEquals(parsed.Text, context.Editor.Document.Snapshot)
            || !parsed.Root.FindToken(Math.Max(0, offset - 1)).IsKind(SyntaxKind.StringLiteralToken) && !parsed.Root.FindToken(offset).IsKind(SyntaxKind.StringLiteralToken)
            || ConfigurationOf?.Invoke(document.Path) is not { } project)
        {
            return null;
        }
        LspPosition at = lsp.PositionOf(offset);
        IReadOnlyList<ConfigurationKey> keys = await SettingsAssist.KeysOfAsync(context.CSharp, [project], cancel);
        return keys.FirstOrDefault(k => k.Kind is ConfigurationKinds.Read or ConfigurationKinds.Section or ConfigurationKinds.Binding && k.Key.Length > 0
            && k.Path is { } path && Passenger.Core.PathNames.Same(path, document.Path) && k.At is { } start && k.End is { } end
            && start.Line == at.Line && start.Character <= at.Character && at.Character <= end.Character) is { } read ? (project, read) : null;
    }

    /// <summary>What the hover card says of a read of a configuration key in C#: the key and where each file the project runs
    /// with gives it with its value (a user secret's always as dots, any other masked by Passenger's rule for secrets), or that
    /// nothing does. None elsewhere.</summary>
    private async Task<string?> ConfigurationHoverAsync(int offset, CancellationToken cancel)
    {
        if (context.Editors.Active is not { } document || context.WebCurrent is not null || context.CSharp?.For(document)?.Lsp is not { } lsp
            || await ReadAtAsync(lsp, document, offset, cancel) is not ({ } project, { } found))
        {
            return null;
        }
        Func<string, string?> text = context.Editors.Texts().Text;
        IReadOnlyList<GivenKey> given = await BackgroundWork.Run(() => ConfigurationFiles.Where(ConfigurationFiles.Given(project, text), found.Key, found.Kind != ConfigurationKinds.Read));
        return given.Count == 0 ? $"{found.Key}: given by nothing {project.Name} runs with"
            : $"{found.Key}:\n" + string.Join('\n', given.Select(g => $"{(g.By == GivenBy.UserSecrets ? "user secrets" : Path.GetFileName(g.File))}"
                + (g.Key.Equals(found.Key, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" ({g.Key})") + (g.Value is null ? string.Empty : $" = {g.Shown}")));
    }

    /// <summary>The fixes of a C# file's warnings, on an offset's line, that nothing gives a configuration key it reads
    /// (<see cref="CSharpDocument.ConfigurationMissingOn"/>), Passenger's own and listed first: its value asked for and set in the
    /// project's appsettings.json, or for a secret's name in its user secrets (masked as it is typed), one command of global
    /// undo each, worked out again as it is chosen and refused once something gives the key.</summary>
    public IReadOnlyList<EditorFix> ConfigurationFixes(OpenDocument file, int offset)
    {
        if (context.CSharp?.For(file) is not { Lsp: { } lsp } features)
        {
            return [];
        }
        return [.. features.ConfigurationMissingOn(lsp.PositionOf(offset).Line).DistinctBy(m => m.Key).Select(m => KeyFix(file, m.Range, m.Key, m.Secret))];
    }

    private EditorFix KeyFix(OpenDocument file, TextRange range, string key, bool secret) =>
        new(secret ? $"Add {key} to user secrets…" : $"Add {key} to appsettings.json…", "a configuration key nothing gives", range, string.Empty)
        {
            Again = _ => context.CSharp?.For(file)?.ConfigurationMissingOn(null).FirstOrDefault(m => m.Key == key) is { } now ? KeyFix(file, now.Range, key, secret) : null,
            Asks = new ValueAsked(secret ? $"The value of {key}, kept in the project's user secrets, out of its files" : $"The value of {key}, kept in the project's appsettings.json",
                secret, (value, _) => Task.FromResult(SetKey(file, key, secret, value))),
        };

    /// <summary>A configuration key set where its fix keeps it, as one command: the project's appsettings.json (made when it has
    /// none), or its user secrets as <c>dotnet user-secrets set</c> keeps them, giving the project a UserSecretsId first where it
    /// has none; the file's text as the editor has it when open, the read's warning pulled again; what it says never holds the value.</summary>
    private RefactorOutcome SetKey(OpenDocument file, string key, bool secret, string value)
    {
        if (ConfigurationOf?.Invoke(file.Path) is not { } project)
        {
            return RefactorOutcome.Refused($"{key} was not set: {file.Name} is in no project");
        }
        Func<string, string?> text = context.Editors.Texts().Text;
        if (ConfigurationFiles.Gives(ConfigurationFiles.Given(project, text), key, section: false))
        {
            return RefactorOutcome.Refused($"{key} was not set: {project.Name}'s configuration gives it already");
        }
        List<XamlFileChange> changes = [];
        string path = project.Settings;
        bool given = false;
        if (secret)
        {
            (UserSecretsFile? secrets, string? unkept) = UserSecrets.Ensure(project.Project, project.UserSecretsId, text);
            if (secrets is null)
            {
                return RefactorOutcome.Refused($"{key} was not set: {unkept}");
            }
            path = secrets.File;
            if (secrets.Given is { } id)
            {
                changes.Add(id);
                given = true;
            }
        }
        JsonNode written = JsonValue.Create(value);
        // User secrets keep a key whole, as dotnet user-secrets writes it, unless the file already nests its first section.
        IReadOnlyList<string> Where(string now) => secret && !Nests(now, key.Split(':')[0]) ? [key] : key.Split(':');
        string? was = text(path);
        bool made = string.IsNullOrWhiteSpace(was);
        if (made)
        {
            if (File.Exists(path))
            {
                return RefactorOutcome.Refused($"{key} was not set: {Path.GetFileName(path)} is empty");
            }
            (string? created, string? problem) = JsonText.SetKey("{\n}\n", secret ? [key] : key.Split(':'), written);
            if (created is null)
            {
                return RefactorOutcome.Refused($"{key} was not set: {problem}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            changes.Add(new XamlFileChange(path, null, created));
        }
        else
        {
            (string? then, string? problem) = JsonText.SetKey(was!, Where(was!), written);
            if (then is null)
            {
                return RefactorOutcome.Refused($"{key} was not set: {Path.GetFileName(path)} was not changed, {problem}");
            }
            changes.Add(new XamlFileChange(path, now => JsonText.SetKey(now, Where(now), written) is { Text: { } changed } && changed != now ? [TextEdit.Between(now, changed)] : []));
        }
        if (actions.ApplyFileChanges($"Add {key}", changes) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        context.CSharp?.For(file)?.RefreshDiagnostics();
        string where = secret ? $"{project.Name}'s user secrets" : $"{project.Name}'s appsettings.json";
        List<RefactoredFile> files = [new RefactoredFile(path, made ? "created" : "changed", [])];
        if (given)
        {
            files.Add(new RefactoredFile(project.Project, "changed", []));
        }
        return new RefactorOutcome($"Added {key} to {where}" + (given ? ", giving it a UserSecretsId" : string.Empty), null, files, []);
    }

    /// <summary>Whether a configuration file's root holds a section as an object, letter case aside.</summary>
    private static bool Nests(string text, string section)
    {
        try
        {
            using System.Text.Json.JsonDocument read = System.Text.Json.JsonDocument.Parse(text,
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return read.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && read.RootElement.EnumerateObject()
                .Any(p => p.Name.Equals(section, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.Object);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
