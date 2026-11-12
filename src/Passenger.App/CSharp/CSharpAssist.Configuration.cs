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
/// programs running its code run with gives its key, the hover card's values of it, and the fixes of a read's warning that
/// nothing gives its key, which set it in a program's appsettings.json, or in its user secrets for a secret's.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>A C# file's code with the configurations of the programs that run it (the session's <see cref="ProjectSession.ConfiguredCodeOf"/>).</summary>
    public Func<string, ConfiguredCode?>? ConfiguredCodeOf { get; set; }

    /// <summary>Where Go to Declaration goes from a read of a configuration key in C#: where each file the programs running it
    /// run with gives it (their settings, a launch profile, their user secrets, a compose file, an AppHost), or saying none
    /// does; null where the offset is on no read of the project's, for the server's own. Only a string's place asks.</summary>
    private async Task<Declared?> ConfigurationDeclaredAsync(LspDocument lsp, OpenDocument document, int offset)
    {
        if (await ReadAtAsync(lsp, document, offset) is not ({ } code, { } read))
        {
            return null;
        }
        Func<string, string?> text = context.Editors.Texts().Text;
        IReadOnlyList<(string Named, LspLocation Place)> given = await BackgroundWork.Run(() =>
        {
            Dictionary<string, ServerLines?> lines = new(StringComparer.Ordinal);
            return (IReadOnlyList<(string, LspLocation)>)[.. code.Where(text, read.Key, read.Kind != ConfigurationKinds.Read)
                .Select(g =>
                {
                    if (!lines.TryGetValue(g.Given.File!, out ServerLines? counted))
                    {
                        lines[g.Given.File!] = counted = text(g.Given.File!) is { } written ? ServerLines.Of(TextSnapshot.From(written)) : null;
                    }
                    LspPosition start = counted?.PositionOf(Math.Min(g.Given.Start, counted.Text.Length)) ?? new LspPosition(0, 0);
                    LspPosition end = counted?.PositionOf(Math.Min(g.Given.End, counted.Text.Length)) ?? start;
                    return ($"{code.Of(g.Program, Path.GetFileName(g.Given.File)!)}: {g.Given.Key} = {g.Given.Shown}",
                        new LspLocation(DocumentUris.FromPath(g.Given.File!), new LspRange(start, end)));
                })];
        });
        return new Declared([.. given.Select(g => g.Place)], $"Nothing {code.Names} runs with gives {read.Key}")
        {
            Names = [.. given.Select(g => (string?)g.Named)],
        };
    }

    /// <summary>The read of a configuration key whose string an offset of a C# file is in, with the configurations its code runs
    /// with; null elsewhere, and asking nothing where the offset is in no string.</summary>
    private async Task<(ConfiguredCode Code, ConfigurationKey Read)?> ReadAtAsync(LspDocument lsp, OpenDocument document, int offset, CancellationToken cancel = default)
    {
        if ((document.Syntax is { } parsing ? await parsing.CurrentAsync() : null) is not { } parsed || !ReferenceEquals(parsed.Text, document.Text.Snapshot)
            || !parsed.Root.FindToken(Math.Max(0, offset - 1)).IsKind(SyntaxKind.StringLiteralToken) && !parsed.Root.FindToken(offset).IsKind(SyntaxKind.StringLiteralToken)
            || ConfiguredCodeOf?.Invoke(document.Path) is not { } code)
        {
            return null;
        }
        LspPosition at = lsp.PositionOf(offset);
        IReadOnlyList<ConfigurationKey> keys = await SettingsAssist.KeysOfAsync(context.CSharp, [code.Project], cancel);
        return keys.FirstOrDefault(k => k.Kind is ConfigurationKinds.Read or ConfigurationKinds.Section or ConfigurationKinds.Binding && k.Key.Length > 0
            && k.Path is { } path && Passenger.Core.PathNames.Same(path, document.Path) && k.At is { } start && k.End is { } end
            && start.Line == at.Line && start.Character <= at.Character && at.Character <= end.Character) is { } read ? (code, read) : null;
    }

    /// <summary>What the hover card says of a read of a configuration key in C#: the key and where each file the programs
    /// running it run with gives it with its value (a user secret's always as dots, any other masked by Passenger's rule for
    /// secrets), or that nothing does. None elsewhere.</summary>
    private async Task<string?> ConfigurationHoverAsync(OpenDocument document, int offset, CancellationToken cancel)
    {
        if (context.WebOf(document) is not null || context.CSharp?.For(document)?.Lsp is not { } lsp
            || await ReadAtAsync(lsp, document, offset, cancel) is not ({ } code, { } found))
        {
            return null;
        }
        Func<string, string?> text = context.Editors.Texts().Text;
        IReadOnlyList<(ConfigurationProject Program, GivenKey Given)> given = await BackgroundWork.Run(() => code.Where(text, found.Key, found.Kind != ConfigurationKinds.Read));
        return given.Count == 0 ? $"{found.Key}: given by nothing {code.Names} runs with"
            : $"{found.Key}:\n" + string.Join('\n', given.Select(g => code.Of(g.Program, g.Given.By == GivenBy.UserSecrets ? "user secrets" : Path.GetFileName(g.Given.File)!)
                + (g.Given.Key.Equals(found.Key, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" ({g.Given.Key})") + (g.Given.Value is null ? string.Empty : $" = {g.Given.Shown}")));
    }

    /// <summary>The fixes of a C# file's warnings, on an offset's line, that nothing gives a configuration key it reads
    /// (<see cref="CSharpDocument.ConfigurationMissingOn"/>), Passenger's own and listed first: for each program running the
    /// code, its value asked for and set in that program's appsettings.json, or for a secret's name in its user secrets (masked
    /// as it is typed), one command of global undo each, worked out again as it is chosen and refused once something gives the key.</summary>
    public IReadOnlyList<EditorFix> ConfigurationFixes(OpenDocument file, int offset)
    {
        if (context.CSharp?.For(file) is not { Lsp: { } lsp } features
            || features.ConfigurationMissingOn(lsp.PositionOf(offset).Line) is not { Count: > 0 } missing || ConfiguredCodeOf?.Invoke(file.Path) is not { } code)
        {
            return [];
        }
        return [.. missing.DistinctBy(m => m.Key).SelectMany(m => code.Programs.Select(p => KeyFix(file, code, p, m.Range, m.Key, m.Secret)))];
    }

    private EditorFix KeyFix(OpenDocument file, ConfiguredCode code, ConfigurationProject program, TextRange range, string key, bool secret) =>
        new($"Add {key} to {code.Of(program, secret ? "user secrets" : "appsettings.json")}…", "a configuration key nothing gives", range, string.Empty)
        {
            Again = _ => context.CSharp?.For(file)?.ConfigurationMissingOn(null).FirstOrDefault(m => m.Key == key) is { } now && ConfiguredCodeOf?.Invoke(file.Path) is { } still
                && still.Programs.FirstOrDefault(p => PathNames.Same(p.Project, program.Project)) is { } again ? KeyFix(file, still, again, now.Range, key, secret) : null,
            Asks = new ValueAsked($"The value of {key}, kept in {(code.Apart(program) ? $"{program.Name}'s" : "the project's")} {(secret ? "user secrets, out of its files" : "appsettings.json")}",
                secret, (value, _) => SetKeyAsync(file, program, key, secret, value)),
        };

    /// <summary>A configuration key set where its fix keeps it, as one command: the program's appsettings.json (made when it has
    /// none), or its user secrets as <c>dotnet user-secrets set</c> keeps them, giving the program a UserSecretsId first where it
    /// has none; the file's text as the editor has it when open, said once a file not open has landed, the read's warning
    /// pulled again; what it says never holds the value.</summary>
    private async Task<RefactorOutcome> SetKeyAsync(OpenDocument file, ConfigurationProject project, string key, bool secret, string value)
    {
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
        if (await actions.ApplyFileChangesAsync($"Add {key}", changes) is { } refused)
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
            using System.Text.Json.JsonDocument read = System.Text.Json.JsonDocument.Parse(text, LenientJson.Document);
            return read.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && read.RootElement.EnumerateObject()
                .Any(p => p.Name.Equals(section, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.Object);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
