using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NuGet.Frameworks;
using Passenger.App.CSharp;
using Passenger.App.MSBuild;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

public sealed record AspireOrchestrationChoice(IReadOnlyList<ProjectModel> Services, ProjectModel? AppHost, ProjectModel? ServiceDefaults);

/// <summary>Prepares the complete change away from the workspace. Nothing in the solution is written until every
/// template, reference and source edit has succeeded. The projects made join the solution and the solution filter
/// open.</summary>
public static class AspireOrchestration
{
    public sealed record Write(string Path, FileText Text, byte[]? Before);

    public static async Task<IReadOnlyList<Write>> PrepareAsync(string solution, string? filter, string staging,
        AspireOrchestrationChoice choice, CancellationToken cancel)
    {
        string root = Path.GetDirectoryName(solution)!;
        string name = Path.GetFileNameWithoutExtension(solution);
        string host = choice.AppHost?.Path ?? Path.Combine(root, name + ".AppHost", name + ".AppHost.csproj");
        string defaults = choice.ServiceDefaults?.Path ?? Path.Combine(root, name + ".ServiceDefaults", name + ".ServiceDefaults.csproj");
        // A new AppHost, which nothing has evaluated, is read as a file in no project.
        CSharpParseOptions hostParsing = choice.AppHost is { } chosen ? CompilationSymbols.Of(chosen) : CSharpParsing.Default;
        Dictionary<string, (string Staged, byte[]? Before)> files = new(PathNames.Comparer);
        List<string> newFolders = [];
        foreach (string project in new[] { choice.AppHost is null ? host : null, choice.ServiceDefaults is null ? defaults : null }.OfType<string>())
        {
            string folder = Path.GetDirectoryName(project)!;
            if (Directory.Exists(folder) || File.Exists(folder))
            {
                throw new InvalidOperationException($"{folder} already exists: choose an existing project or move the folder in its way first");
            }
            newFolders.Add(folder);
        }
        if (choice.Services.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one service project");
        }
        if (choice.Services.Any(p => p.TargetFrameworks.Count == 0))
        {
            throw new InvalidOperationException("Wait until the selected service projects have loaded their target frameworks");
        }
        string Staged(string path)
        {
            if (!PathNames.Within(path, root))
            {
                throw new InvalidOperationException($"{path} is outside the solution folder: add orchestration for this project by hand");
            }
            return Path.Combine(staging, Path.GetRelativePath(root, path));
        }
        // Outside the solution's folder only a Directory.Packages.props above it may be changed, staged apart.
        string Copy(string path, bool outside = false)
        {
            if (files.TryGetValue(path, out (string Staged, byte[]? Before) held))
            {
                return held.Staged;
            }
            byte[] before = File.ReadAllBytes(path);
            FileText text = FileText.FromBytes(before) ?? throw new InvalidOperationException($"{path} is not a text file");
            string to = outside && !PathNames.Within(path, root)
                ? Path.Combine(staging, "outside", files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(path))
                : Staged(path);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            FileText.SaveOver(to, text, text.Text);
            files.Add(path, (to, before));
            return to;
        }
        Copy(solution);
        foreach (ProjectModel service in choice.Services)
        {
            Copy(service.Path);
            Copy(Path.Combine(service.Folder, "Program.cs"));
        }
        if (choice.AppHost is not null)
        {
            Copy(host);
            Copy(HostSourcePath(host, hostParsing));
            if (choice.Services.Any(service => !CanReference(choice.AppHost.TargetFrameworks, service.TargetFrameworks)))
            {
                throw new InvalidOperationException("The selected AppHost framework cannot reference every selected service: choose a newer AppHost");
            }
        }
        if (choice.ServiceDefaults is not null)
        {
            Copy(defaults);
            ValidateDefaults(choice.ServiceDefaults, choice.Services);
        }
        cancel.ThrowIfCancellationRequested();
        if (newFolders.Count > 0)
        {
            TemplateCatalog catalog = await ProjectTemplates.LoadAsync(root);
            cancel.ThrowIfCancellationRequested();
            if (choice.AppHost is null)
            {
                await Generate(host, "aspire-apphost", hostFramework: true);
            }
            if (choice.ServiceDefaults is null)
            {
                await Generate(defaults, "aspire-servicedefaults", hostFramework: false);
            }

            async Task Generate(string project, string shortName, bool hostFramework)
            {
                ProjectTemplate template = catalog.Templates.FirstOrDefault(t => t.Pack is null && t.ShortNames.Contains(shortName))
                    ?? throw new InvalidOperationException("Install Aspire.ProjectTemplates through Add > New Project > Aspire > Install, then try again");
                List<(TemplateVariant Variant, string Framework, Version Version)> available = [.. template.Variants
                    .Where(v => v.Language is null or "C#")
                    .SelectMany(v => v.Frameworks.Select(f => (v, f, Framework(f).Version)))];
                IEnumerable<(TemplateVariant Variant, string Framework, Version Version)> compatible = available
                    .Where(a => choice.Services.All(service => hostFramework
                        ? CanReference([a.Framework], service.TargetFrameworks)
                        : CanReference(service.TargetFrameworks, [a.Framework])));
                (TemplateVariant Variant, string Framework, Version Version) picked =
                    (hostFramework ? compatible.OrderBy(a => a.Version) : compatible.OrderByDescending(a => a.Version)).FirstOrDefault();
                if (picked.Variant is null || picked.Variant.SkipRestoreOption is null)
                {
                    throw new InvalidOperationException($"The installed {shortName} template has no compatible framework with skip-restore support for the selected services");
                }
                List<string> arguments = [template.ShortName, "--name", Path.GetFileNameWithoutExtension(project),
                    "--output", Path.GetDirectoryName(Staged(project))!, "--no-update-check", $"--{picked.Variant.SkipRestoreOption}", "true"];
                if (picked.Variant.Language is { } language)
                {
                    arguments.AddRange(["--language", language]);
                }
                if (picked.Variant.FrameworkOption is { } option)
                {
                    arguments.AddRange([$"--{option}", picked.Framework]);
                }
                Task<ChildResult> running = ChildProcesses.RunAsync(ProjectTemplates.NewCommand(arguments, root),
                    limit: TimeSpan.FromMinutes(10), cancel: cancel);
                BackgroundWork.Track(running);
                ChildResult result = await running;
                cancel.ThrowIfCancellationRequested();
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"Could not create {shortName}: {result.Said}");
                }
                string generated = Path.GetDirectoryName(Staged(project))!;
                foreach (string file in Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(Path.GetDirectoryName(project)!, Path.GetRelativePath(generated, file));
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        throw new InvalidOperationException($"{destination} already exists");
                    }
                    _ = FileText.Load(file) ?? throw new InvalidOperationException($"The template produced a binary file: {file}");
                    files.Add(destination, (file, null));
                }
                if (!files.ContainsKey(project))
                {
                    throw new InvalidOperationException($"The template did not produce {Path.GetFileName(project)}");
                }
            }
        }
        foreach (ProjectModel service in choice.Services)
        {
            if (!service.ProjectReferences.Any(p => PathNames.Same(p, defaults)))
            {
                Check(ProjectEdits.AddReference(Staged(service.Path), Staged(defaults)));
            }
            if (!(choice.AppHost?.ProjectReferences.Any(p => PathNames.Same(p, service.Path)) ?? false))
            {
                Check(ProjectEdits.AddReference(Staged(host), Staged(service.Path)));
            }
            string program = Path.Combine(service.Folder, "Program.cs");
            Save(Staged(program), ServiceSource(Read(Staged(program)).Text, program, CompilationSymbols.Of(service)));
        }
        string hostProgram = HostSourcePath(Staged(host), hostParsing);
        Dictionary<string, BackingNeeds> needs = choice.Services.ToDictionary(service => service.Path, service => BackingService.Needs(service), PathNames.Comparer);
        Save(hostProgram, HostSource(Read(hostProgram).Text, host, choice.Services, needs, hostParsing));
        string hostText = Read(Staged(host)).Text;
        List<string> hosting = [.. BackingService.Known.Where(k => needs.Values.Any(n => n.Services.Contains(k))).Select(k => k.AspirePackage)
            .Where(package => !ProjectEdits.HasPackage(hostText, package))];
        CentralPackages? hostCentral = hosting.Count > 0 ? await CentralPackages.OfAsync(host, hostText, cancel) : null;
        if (hosting.Count > 0)
        {
            // Aspire's hosting integrations at the AppHost's own Aspire version: its SDK's, else its Aspire.Hosting.AppHost's.
            string version = AspireVersion(hostText) ?? hostCentral?.Versions.GetValueOrDefault("Aspire.Hosting.AppHost")
                ?? throw new InvalidOperationException($"The AppHost's Aspire version could not be read from its project, for {hosting[0]} to be added at it; no file was changed");
            foreach (string package in hosting)
            {
                Check(ProjectEdits.AddPackage(Staged(host), package, version));
            }
        }
        // Under central package management the versions move to Directory.Packages.props: a made project's all, the AppHost's added.
        List<(string Project, IReadOnlyCollection<string>? Only)> taking = [];
        if (choice.ServiceDefaults is null)
        {
            taking.Add((defaults, null));
        }
        if (choice.AppHost is null || hosting.Count > 0)
        {
            taking.Add((host, choice.AppHost is null ? null : hosting));
        }
        foreach ((string project, IReadOnlyCollection<string>? only) in taking)
        {
            string text = Read(Staged(project)).Text;
            if (!ProjectEdits.VersionedPackages(text).Any(p => only is null || only.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
                || (PathNames.Same(project, host) && hostCentral is not null ? hostCentral : await CentralPackages.OfAsync(project, text, cancel)) is not { } central)
            {
                continue;
            }
            (string centralized, IReadOnlyList<(string Id, string Version)> pinned) = ProjectEdits.Centralized(text, central.Versions, central.Overrides, only);
            Save(Staged(project), centralized);
            if (pinned.Count > 0)
            {
                string props = Copy(central.Props, outside: true);
                Save(props, CentralPackages.Pinned(Read(props).Text, pinned));
            }
        }
        // Opened again, the filter would leave out a project it does not list.
        string? filtered = filter is null ? null : Copy(filter, outside: true);
        foreach (string project in new[] { choice.AppHost is null ? host : null, choice.ServiceDefaults is null ? defaults : null }.OfType<string>())
        {
            Check(SolutionEdits.AddProject(Staged(solution), null, Staged(project)));
            if (filter is not null)
            {
                Check(SolutionEdits.AddToFilter(filter, project, filtered));
            }
        }
        cancel.ThrowIfCancellationRequested();
        return [.. files.Select(f => new Write(f.Key, Read(f.Value.Staged), f.Value.Before))
            .Where(w => w.Before is null || FileText.FromBytes(w.Before)?.Text != w.Text.Text)];
    }

    public static void CheckUnchanged(IReadOnlyList<Write> writes)
    {
        foreach (Write write in writes)
        {
            if (write.Before is null ? File.Exists(write.Path) || Directory.Exists(write.Path)
                : !File.Exists(write.Path) || !File.ReadAllBytes(write.Path).AsSpan().SequenceEqual(write.Before))
            {
                throw new InvalidOperationException($"{write.Path} changed while orchestration was being prepared: try again");
            }
        }
    }

    private static void ValidateDefaults(ProjectModel defaults, IReadOnlyList<ProjectModel> services)
    {
        if (services.Any(service => !CanReference(service.TargetFrameworks, defaults.TargetFrameworks)))
        {
            throw new InvalidOperationException("The selected ServiceDefaults target framework is not compatible with every service");
        }
        // What the project builds, never its output, a link twice or a hidden folder.
        CSharpParseOptions parsing = CompilationSymbols.Of(defaults);
        List<MethodDeclarationSyntax> methods = [.. FolderWalk.Files(defaults.Folder, built: true)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => CSharpSyntaxTree.ParseText(Read(p).Text, parsing).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())];
        if (!methods.Any(m => m.Identifier.ValueText == "AddServiceDefaults" && m.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true))
        {
            throw new InvalidOperationException("The selected ServiceDefaults project does not expose AddServiceDefaults: choose an Aspire ServiceDefaults project");
        }
        bool web = services.Any(p => CSharpSyntaxTree.ParseText(Read(Path.Combine(p.Folder, "Program.cs")).Text, CompilationSymbols.Of(p)).GetRoot()
            .DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is MemberAccessExpressionSyntax
            { Expression: IdentifierNameSyntax { Identifier.ValueText: "WebApplication" }, Name.Identifier.ValueText: "CreateBuilder" }));
        if (web && !methods.Any(m => m.Identifier.ValueText == "MapDefaultEndpoints" && m.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true))
        {
            throw new InvalidOperationException("The selected ServiceDefaults project does not expose MapDefaultEndpoints for web services");
        }
    }

    /// <summary>The Directory.Packages.props files orchestration may pin versions in (<see cref="CentralPackages.PropsOf(ProjectModel)"/>):
    /// the chosen AppHost's, and where it makes a project, those of the solution's <paramref name="projects"/>, one of
    /// which a project made beside them reads.</summary>
    public static IReadOnlyList<string> PackagesProps(AspireOrchestrationChoice choice, IEnumerable<ProjectModel> projects) =>
        [.. (choice.AppHost is null || choice.ServiceDefaults is null ? projects : [])
            .Prepend(choice.AppHost)
            .OfType<ProjectModel>()
            .Select(CentralPackages.PropsOf)
            .OfType<string>()
            .Distinct(PathNames.Comparer)];

    public static IReadOnlyList<string> AppHostSources(string project) =>
        [.. Directory.EnumerateFiles(Path.GetDirectoryName(project)!, "*.cs", SearchOption.TopDirectoryOnly)];

    /// <summary>The AppHost's file that makes its builder, its files read as its project compiles them (<paramref name="parsing"/>).</summary>
    private static string HostSourcePath(string project, CSharpParseOptions parsing)
    {
        List<string> sources = [.. AppHostSources(project).Where(path => CSharpSyntaxTree.ParseText(Read(path).Text, parsing).GetRoot()
            .DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is MemberAccessExpressionSyntax
                { Expression: IdentifierNameSyntax { Identifier.ValueText: "DistributedApplication" }, Name.Identifier.ValueText: "CreateBuilder" }))];
        return sources.Count == 1 ? sources[0]
            : throw new InvalidOperationException($"{project}: expected one AppHost source file with DistributedApplication.CreateBuilder; no file was changed");
    }

    private static bool CanReference(IReadOnlyList<string> consumers, IReadOnlyList<string> providers)
    {
        List<NuGetFramework> targets = [.. consumers.Select(Framework)];
        List<NuGetFramework> references = [.. providers.Select(Framework)];
        return targets.Count > 0 && references.Count > 0
            && targets.All(target => references.Any(reference => DefaultCompatibilityProvider.Instance.IsCompatible(target, reference)));
    }

    private static NuGetFramework Framework(string framework)
    {
        NuGetFramework parsed = NuGetFramework.ParseFolder(framework);
        if (parsed.IsUnsupported || parsed.Framework != FrameworkConstants.FrameworkIdentifiers.NetCoreApp || parsed.Version.Major < 8)
        {
            throw new InvalidOperationException($"Aspire orchestration requires .NET 8 or later: {framework}");
        }
        return parsed;
    }

    private static FileText Read(string path) => FileText.Load(path) ?? throw new InvalidOperationException($"{path} is not text");

    private static void Save(string path, string text)
    {
        FileText file = Read(path);
        FileText.SaveOver(path, file, text);
    }

    private static void Check(string? problem)
    {
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }
    }

    private static string ServiceSource(string text, string path, CSharpParseOptions parsing)
    {
        CompilationUnitSyntax root = Parse(text, path, parsing);
        (VariableDeclaratorSyntax builder, InvocationExpressionSyntax create) = Builder(root, ["WebApplication", "Host"], path);
        string variable = builder.Identifier.Text;
        InvocationExpressionSyntax build = Build(root, builder, path);
        List<(int At, string Text)> insertions = [];
        if (!Calls(root, variable, "AddServiceDefaults").Any())
        {
            StatementSyntax statement = builder.Ancestors().OfType<StatementSyntax>().First();
            insertions.Add((statement.Span.End, NewLine(text) + Indent(text, statement.SpanStart) + variable + ".AddServiceDefaults();"));
        }
        if (create.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "WebApplication" } })
        {
            VariableDeclaratorSyntax app = build.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()
                ?? throw new InvalidOperationException($"{path}: assign the built WebApplication to a local variable before adding orchestration");
            if (!Calls(root, app.Identifier.Text, "MapDefaultEndpoints").Any())
            {
                StatementSyntax statement = app.Ancestors().OfType<StatementSyntax>().First();
                insertions.Add((statement.Span.End, NewLine(text) + Indent(text, statement.SpanStart) + app.Identifier.Text + ".MapDefaultEndpoints();"));
            }
        }
        return Insert(text, insertions);
    }

    private static string HostSource(string text, string host, IReadOnlyList<ProjectModel> services, IReadOnlyDictionary<string, BackingNeeds> needs,
        CSharpParseOptions parsing)
    {
        CompilationUnitSyntax root = Parse(text, host, parsing);
        (VariableDeclaratorSyntax builder, _) = Builder(root, ["DistributedApplication"], host);
        InvocationExpressionSyntax build = Build(root, builder, host);
        StatementSyntax statement = build.Ancestors().OfType<StatementSyntax>().First();
        List<string> additions = [];
        Backing backing = new(root, builder.Identifier.Text);
        List<(int At, string Text)> referenced = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        List<(InvocationExpressionSyntax Call, string Name, string? ProjectPath, string? ProjectType)> projects =
            [.. Calls(root, builder.Identifier.Text, "AddProject").Select(BindProjectArguments)];
        foreach (ProjectModel service in services)
        {
            string name = Path.GetFileNameWithoutExtension(service.Path).ToLowerInvariant().Replace('.', '-').Replace('_', '-');
            if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || !char.IsAsciiLetter(name[0]) || !names.Add(name))
            {
                throw new InvalidOperationException($"{service.Name} does not have a unique Aspire resource name: rename the project or add its resource by hand");
            }
            string relative = Path.GetRelativePath(Path.GetDirectoryName(host)!, service.Path).Replace('\\', '/');
            string generatedType = Path.GetFileNameWithoutExtension(service.Path).Replace('.', '_').Replace('-', '_');
            bool SameProject((InvocationExpressionSyntax Call, string Name, string? ProjectPath, string? ProjectType) project)
            {
                try
                {
                    return project.ProjectPath is { } path
                        ? PathNames.Same(PathNames.Canonical(Path.GetFullPath(path.Replace('\\', '/'), Path.GetDirectoryName(host)!)), service.Path)
                        : project.ProjectType == "Projects." + generatedType;
                }
                catch (ArgumentException error)
                {
                    throw new InvalidOperationException($"Cannot resolve AppHost projectPath in {project.Call}: {error.Message}", error);
                }
            }
            bool exists = projects.Any(SameProject);
            bool occupied = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } member
                && receiver.Identifier.Text == builder.Identifier.Text && member.Name.Identifier.ValueText.StartsWith("Add", StringComparison.Ordinal)
                && (call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "name")
                    ?? call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon is null))?.Expression is LiteralExpressionSyntax literal
                && literal.Token.ValueText == name
                && !(member.Name.Identifier.ValueText == "AddProject" && projects.Any(p => p.Call == call && SameProject(p))));
            if (occupied)
            {
                throw new InvalidOperationException($"The AppHost already uses resource name {name} for another resource: add {service.Name} by hand with a unique name");
            }
            // Each backing service it reads, by the name its connection string uses, referenced and waited for.
            IReadOnlyList<string> uses = backing.Used(needs.GetValueOrDefault(service.Path) ?? new BackingNeeds([], []));
            if (!exists)
            {
                additions.Add($"var {backing.Variable(name)} = {builder.Identifier.Text}.AddProject({SymbolDisplay.FormatLiteral(name, true)}, {SymbolDisplay.FormatLiteral(relative, true)}){References(uses, string.Empty)};");
            }
            else if (projects.FirstOrDefault(SameProject) is { Call: { } call } && uses.Count > 0)
            {
                string chain = call.Ancestors().OfType<StatementSyntax>().First().ToString();
                referenced.Add((call.Span.End, References(uses, chain)));
            }
        }
        List<(int At, string Text)> insertions = [.. referenced];
        string eol = NewLine(text);
        string indent = Indent(text, statement.SpanStart);
        if (backing.Declarations.Count > 0)
        {
            StatementSyntax declared = builder.Ancestors().OfType<StatementSyntax>().First();
            insertions.Add((declared.Span.End, eol + eol + string.Join(eol, backing.Declarations.Select(d => Indent(text, declared.SpanStart) + d))));
        }
        if (additions.Count > 0)
        {
            insertions.Add((statement.SpanStart, string.Join(eol + indent, additions) + eol + indent));
        }
        return Insert(text, insertions);

        static string References(IReadOnlyList<string> uses, string chain) => string.Concat(uses
            .Where(use => !chain.Contains($".WithReference({use})", StringComparison.Ordinal))
            .Select(use => $".WithReference({use}).WaitFor({use})"));
    }

    /// <summary>The backing services an AppHost declares for its services, as Add > Docker Compose finds them
    /// (<see cref="BackingService.Needs"/>): each server once, by its service's name, a database of one that keeps them for each
    /// connection string that names one (by the string's name, its database kept), any other by the string's name; a
    /// resource the AppHost already declares by that name is its own, used through its variable.</summary>
    private sealed class Backing(CompilationUnitSyntax root, string builder)
    {
        private readonly HashSet<string> taken = [.. root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.ValueText)];
        private readonly Dictionary<string, string?> resources = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Declarations { get; } = [];

        /// <summary>The variables a service references for what it reads, its resources declared as they are first wanted.</summary>
        public IReadOnlyList<string> Used(BackingNeeds needs)
        {
            List<string> used = [];
            foreach (BackingService service in needs.Services)
            {
                BackingConnection[] reading = [.. needs.Connections.Where(c => c.To == service)];
                IEnumerable<string?> variables = reading.Length == 0 ? [Server(service)]
                    : reading.Select(c => service.HasDatabases ? Database(service, c) : Declared(c.Name, $"{builder}.{service.AspireMethod}({Literal(c.Name)})"));
                used.AddRange(variables.OfType<string>().Where(v => !used.Contains(v)));
            }
            return used;
        }

        private string? Server(BackingService service) => Declared(service.Service, $"{builder}.{service.AspireMethod}({Literal(service.Service)})");

        private string? Database(BackingService service, BackingConnection connection)
        {
            if (Server(service) is not { } server)
            {
                return null;
            }
            string? database = service.Database(connection.Name, connection.Value);
            string named = database is null || database == connection.Name ? Literal(connection.Name) : $"{Literal(connection.Name)}, {Literal(database)}";
            return Declared(connection.Name, $"{server}.AddDatabase({named})");
        }

        /// <summary>The variable of the resource of a name: the AppHost's own when it declares one (none when not as a variable),
        /// else one declared now with what adds it.</summary>
        private string? Declared(string name, string adding)
        {
            if (resources.TryGetValue(name, out string? known))
            {
                return known;
            }
            if (root.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(call => call.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText.StartsWith("Add", StringComparison.Ordinal)
                && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.Value: string written }
                && string.Equals(written, name, StringComparison.OrdinalIgnoreCase)) is { } own)
            {
                return resources[name] = own.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText;
            }
            string variable = Variable(name);
            Declarations.Add($"var {variable} = {adding};");
            return resources[name] = variable;
        }

        /// <summary>A local's name for a resource's: its words in camel case, never a keyword or a name the file has.</summary>
        public string Variable(string name)
        {
            string[] words = [.. System.Text.RegularExpressions.Regex.Split(name, "[^A-Za-z0-9]+").Where(w => w.Length > 0)];
            string made = words.Length == 0 ? "resource" : string.Concat(words.Select((w, i) => i == 0 ? char.ToLowerInvariant(w[0]) + w[1..] : char.ToUpperInvariant(w[0]) + w[1..]));
            if (!char.IsLetter(made[0]) || SyntaxFacts.GetKeywordKind(made) != SyntaxKind.None)
            {
                made = "_" + made;
            }
            string variable = made;
            for (int i = 2; !taken.Add(variable); i++)
            {
                variable = made + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return variable;
        }

        private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, true);
    }

    /// <summary>The Aspire version a project names: its AppHost SDK's, else its Aspire.Hosting.AppHost's.</summary>
    private static string? AspireVersion(string text) =>
        System.Text.RegularExpressions.Regex.Match(text,
            "Aspire\\.AppHost\\.Sdk/(?<v>[0-9][^\"\\s;]*)|Name=\"Aspire\\.AppHost\\.Sdk\"\\s+Version=\"(?<v>[^\"]+)\"|Include=\"Aspire\\.Hosting\\.AppHost\"\\s+Version=\"(?<v>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } found ? found.Groups["v"].Value : null;

    private static (InvocationExpressionSyntax Call, string Name, string? ProjectPath, string? ProjectType) BindProjectArguments(InvocationExpressionSyntax call)
    {
        GenericNameSyntax? generic = ((MemberAccessExpressionSyntax)call.Expression).Name as GenericNameSyntax;
        int required = generic is null ? 2 : 1;
        Dictionary<int, ExpressionSyntax> arguments = [];
        bool reordered = false;
        for (int position = 0; position < call.ArgumentList.Arguments.Count; position++)
        {
            ArgumentSyntax argument = call.ArgumentList.Arguments[position];
            int parameter = argument.NameColon?.Name.Identifier.ValueText switch
            {
                null => position,
                "name" => 0,
                "projectPath" when generic is null => 1,
                "launchProfileName" or "configure" => required,
                _ => -1,
            };
            if (parameter < 0 || parameter > required || argument.RefKindKeyword.RawKind != 0
                || argument.NameColon is null && reordered || !arguments.TryAdd(parameter, argument.Expression))
            {
                throw Unresolved();
            }
            reordered |= parameter != position;
        }
        if (generic is not null && generic.TypeArgumentList.Arguments.Count != 1
            || !arguments.TryGetValue(0, out ExpressionSyntax? name) || name is not LiteralExpressionSyntax { Token.Value: string resourceName }
            || string.IsNullOrWhiteSpace(resourceName)
            || generic is null && (!arguments.TryGetValue(1, out ExpressionSyntax? path)
                || path is not LiteralExpressionSyntax { Token.Value: string projectPath } || string.IsNullOrWhiteSpace(projectPath)))
        {
            throw Unresolved();
        }
        return (call, ((LiteralExpressionSyntax)arguments[0]).Token.ValueText,
            generic is null ? ((LiteralExpressionSyntax)arguments[1]).Token.ValueText : null,
            generic?.TypeArgumentList.Arguments[0].ToString());

        InvalidOperationException Unresolved() => new($"Cannot resolve AppHost AddProject arguments: {call}; use literal name and projectPath arguments and a supported Aspire overload before adding orchestration");
    }

    /// <summary>A file to change, read as its project compiles it (<paramref name="parsing"/>).</summary>
    private static CompilationUnitSyntax Parse(string text, string path, CSharpParseOptions parsing)
    {
        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(text, parsing).GetCompilationUnitRoot();
        if (root.ContainsDiagnostics || root.ContainsDirectives)
        {
            throw new InvalidOperationException($"{path}: source contains syntax errors or directives: add orchestration by hand for this layout");
        }
        return root;
    }

    private static (VariableDeclaratorSyntax Builder, InvocationExpressionSyntax Create) Builder(CompilationUnitSyntax root, string[] types, string path)
    {
        List<(VariableDeclaratorSyntax Builder, InvocationExpressionSyntax Create)> builders = [.. root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax
                { Expression: IdentifierNameSyntax type, Name.Identifier.ValueText: "CreateBuilder" or "CreateApplicationBuilder" } } && types.Contains(type.Identifier.ValueText))
            .Select(v => (v, (InvocationExpressionSyntax)v.Initializer!.Value))];
        if (builders.Count != 1 || builders[0].Builder.Ancestors().OfType<StatementSyntax>().FirstOrDefault()
            is not LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 })
        {
            throw new InvalidOperationException($"{path}: expected one local builder created with {string.Join(" or ", types)}.CreateBuilder; no file was changed");
        }
        return builders[0];
    }

    private static InvocationExpressionSyntax Build(CompilationUnitSyntax root, VariableDeclaratorSyntax builder, string path)
    {
        List<InvocationExpressionSyntax> builds = [.. Calls(root, builder.Identifier.Text, "Build")];
        StatementSyntax declaration = builder.Ancestors().OfType<StatementSyntax>().First();
        if (builds.Count != 1 || builds[0].SpanStart <= builder.Span.End
            || Scope(builds[0].Ancestors().OfType<StatementSyntax>().First()) != Scope(declaration))
        {
            throw new InvalidOperationException($"{path}: expected one builder.Build() in the builder's scope; no file was changed");
        }
        return builds[0];
    }

    private static SyntaxNode? Scope(StatementSyntax statement) => statement.Parent is GlobalStatementSyntax global ? global.Parent : statement.Parent;

    private static IEnumerable<InvocationExpressionSyntax> Calls(CompilationUnitSyntax root, string receiver, string method) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => c.Expression is MemberAccessExpressionSyntax
        { Expression: IdentifierNameSyntax identifier } member && identifier.Identifier.Text == receiver && member.Name.Identifier.ValueText == method);

    private static string NewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string Indent(string text, int at)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        return new string(text[start..at].TakeWhile(c => c is ' ' or '\t').ToArray());
    }

    private static string Insert(string text, IEnumerable<(int At, string Text)> insertions)
    {
        foreach ((int at, string addition) in insertions.OrderByDescending(i => i.At))
        {
            text = text.Insert(at, addition);
        }
        return text;
    }
}
