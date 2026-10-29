using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Agents using the code tools on every language Passenger serves, each answered by that language's own service:
/// Avalonia's and WPF's XAML through Passenger's own XAML (the galleries of XamlTests and WpfXamlTests, each in a folder
/// of the test's own, restored), the web languages through their servers with the project's own ESLint and Prettier
/// (skipped without a Node.js), a compose file and a Dockerfile through theirs, MSBuild files through Passenger's own
/// evaluation, and saying plainly what a language lacks. Its Passengers are processes of their own, so it runs beside the
/// window tests (<see cref="Beside"/>).</summary>
[Collection(Beside.Name)]
public sealed class McpLanguagesTests(McpLanguagesTests.Restored restored) : McpRig, IClassFixture<McpLanguagesTests.Restored>
{
    /// <summary>The user secrets of the Aspire test's AppHost, the run's own.</summary>
    private const string AspireSecretsId = "passenger-mcp-aspire-host";

    /// <summary>Avalonia's XAML, and a library type it names decompiled once across sessions, another version's decompiled
    /// files unused for months pruned.</summary>
    [Fact]
    public async Task An_agent_reads_navigates_renames_and_refactors_avalonia_xaml_through_passengers_own_xaml_and_a_library_type_decompiled_once()
    {
        string gallery = await Taken("mcpaxaml-");
        // Another version's decompiled files, which no session has used for two months.
        string stale = Path.Combine(OwnState, "MetadataAsSource", "Decompiled", "Avalonia.Controls", "0123456789abcdef");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "Avalonia.Controls.StackPanel.cs"), "// Decompiled from a version long gone\n");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(60));
        McpClient client = await Start(["--mcp", gallery]);
        string window = File.ReadAllText(Path.Combine(gallery, "MainWindow.axaml"));

        string decompiled = Assert.Single((await Call(client, McpTools.Definition, At(window, "MainWindow.axaml", "<StackPan"))).EnumerateArray()).GetProperty("path").GetString()!;
        Assert.EndsWith("Avalonia.Controls.StackPanel.cs", decompiled, StringComparison.Ordinal);
        Assert.False(Directory.Exists(stale), $"The decompiled files of a version unused for two months were kept: {stale}");
        DateTime written = File.GetLastWriteTimeUtc(decompiled);

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = "MainWindow.axaml" })).GetString()!;
        Assert.True(outline.StartsWith("Window x:Class=\"Gallery.MainWindow\" x:DataType=\"vm:MainViewModel\" (line 1)\n  StackPanel (line 10)\n", StringComparison.Ordinal)
            && outline.Contains("\n    Button Name=\"Go\" (line 12)", StringComparison.Ordinal), $"The window's outline is not its element tree:\n{outline}");

        Assert.Equal("class Avalonia.Controls.Button : ContentControl", (await Call(client, McpTools.SymbolInfo, At(window, "MainWindow.axaml", "<Butt"))).GetString());
        JsonElement greeting = Assert.Single((await Call(client, McpTools.Definition, At(window, "MainWindow.axaml", "{Binding Gree"))).EnumerateArray());
        Assert.Equal((Path.Combine("ViewModels", "MainViewModel.cs"), 5), (greeting.GetProperty("path").GetString(), greeting.GetProperty("line").GetInt32()));
        Assert.Contains("public string Greeting", greeting.GetProperty("text").GetString(), StringComparison.Ordinal);
        JsonElement[] accents = [.. (await Call(client, McpTools.Definition, At(window, "MainWindow.axaml", "{StaticResource Acc"))).EnumerateArray()];
        Assert.Equal([(Path.Combine("Styles", "Palette.axaml"), 5), (Path.Combine("Styles", "Palette.axaml"), 9)],
            accents.Select(a => (a.GetProperty("path").GetString(), a.GetProperty("line").GetInt32())));

        string[] uses = [.. Used(await Call(client, McpTools.Usages, At(window, "MainWindow.axaml", "Name=\"G"))).Select(u => $"{u.Path}:{u.Line}")];
        Assert.True(uses.Contains("MainWindow.axaml:12") && uses.Contains("MainWindow.axaml:13") && uses.Contains("MainWindow.axaml.cs:10"),
            $"The button's name was not found where its file and its code-behind use it: {string.Join(", ", uses)}");
        // A bound property and a handler have their XAML among their usages, which Safe Delete names before deleting either.
        string viewModel = Path.Combine("ViewModels", "MainViewModel.cs");
        Dictionary<string, object?> property = At(XamlTests.Gallery.MainViewModel, viewModel, "string Gree");
        Dictionary<string, object?>[] boundAt = [property, At(window, "MainWindow.axaml", "{Binding Gree")];
        foreach (Dictionary<string, object?> asked in boundAt)
        {
            string[] bound = [.. Used(await Call(client, McpTools.Usages, asked)).Select(u => $"{u.Path}:{u.Line}")];
            Assert.True(bound.Contains("MainWindow.axaml:11") && bound.Contains($"{viewModel}:5"),
                $"The bound property's usages from {asked["path"]} are not its declaration and the binding: {string.Join(", ", bound)}");
        }
        // A named element's field in the code-behind goes to its name in the XAML, as Go to Declaration goes, not to the generated field.
        JsonElement field = Assert.Single((await Call(client, McpTools.Definition, At(XamlTests.Gallery.HandlingCodeBehind, "MainWindow.axaml.cs", "        G"))).EnumerateArray());
        Assert.Equal(("MainWindow.axaml", 12), (field.GetProperty("path").GetString(), field.GetProperty("line").GetInt32()));
        Dictionary<string, object?> handler = At(XamlTests.Gallery.HandlingCodeBehind, "MainWindow.axaml.cs", "void On");
        Assert.Contains("MainWindow.axaml:12", Used(await Call(client, McpTools.Usages, handler)).Select(u => $"{u.Path}:{u.Line}"));
        JsonElement described = await Call(client, McpTools.Refactor, new(property) { ["kind"] = "safe_delete", ["describe"] = true });
        Assert.Equal($"Safe Delete would take the property 'Greeting' out of {viewModel}, leaving 1 usage that would no longer compile",
            described.GetProperty("said").GetString());
        CallToolResult refused = await Result(client, McpTools.Refactor, new(handler) { ["kind"] = "safe_delete" });
        Assert.True(refused.IsError == true && Text(refused).Contains("MainWindow.axaml:12: <Button Name=\"Go\" Content=\"Go\" Click=\"OnGo\"", StringComparison.Ordinal),
            $"Safe Delete of the handler did not refuse naming the XAML that uses it: {Text(refused)}");
        Assert.Contains("void OnGo", File.ReadAllText(Path.Combine(gallery, "MainWindow.axaml.cs")), StringComparison.Ordinal);

        JsonElement missing = Assert.Single((await Call(client, McpTools.Diagnostics, new() { ["path"] = "MainWindow.axaml" })).EnumerateArray());
        Assert.Contains("'Subtitle'", missing.GetProperty("message").GetString(), StringComparison.Ordinal);
        Dictionary<string, object?> subtitle = At(window, "MainWindow.axaml", "{Binding Subt");
        Assert.Contains("Create property 'Subtitle' [quickfix]", Strings(await Call(client, McpTools.CodeActions, subtitle)));
        await Call(client, McpTools.ApplyCodeAction, new(subtitle) { ["title"] = "Create property 'Subtitle'" });
        Assert.Contains("public string? Subtitle { get; set; }", File.ReadAllText(Path.Combine(gallery, "ViewModels", "MainViewModel.cs")), StringComparison.Ordinal);
        // The view model written or edited, the window's XAML is read against it before its diagnostics answer.
        Assert.True(Problems(await Call(client, McpTools.Diagnostics, new() { ["path"] = "MainWindow.axaml" })) is [],
            "diagnostics at once after Create property still said the binding's property is missing");
        Dictionary<string, object?> renaming = new() { ["path"] = Path.Combine("ViewModels", "MainViewModel.cs"), ["oldText"] = "public string Greeting", ["newText"] = "public string Salutation" };
        await Call(client, McpTools.Edit, renaming);
        string[] unbound = Problems(await Call(client, McpTools.Diagnostics, new() { ["path"] = "MainWindow.axaml" }));
        Assert.True(unbound is [{ } lacking] && lacking.Contains("'Greeting'", StringComparison.Ordinal),
            $"diagnostics at once after the view model's Greeting was renamed did not say the window binds a property it lacks: {string.Join(" | ", unbound)}");
        await Call(client, McpTools.Edit, new(renaming) { ["oldText"] = renaming["newText"], ["newText"] = renaming["oldText"] });

        Dictionary<string, object?> margin = At(window, "MainWindow.axaml", "<StackPanel Margin=\"1");
        Assert.Equal(["Extract Resource… [refactor.extract], choices: MainWindow.axaml <Window.Resources>; App.axaml <Application.Resources>; Palette.axaml <ResourceDictionary>",
            "Extract Style… [refactor.extract], choices: MainWindow.axaml <Window.Styles>; App.axaml <Application.Styles>"], Strings(await Call(client, McpTools.CodeActions, margin)));
        Assert.Equal("Extracted resource 'PanelMargin' to MainWindow.axaml <Window.Resources>", (await Call(client, McpTools.ApplyCodeAction,
            new(margin) { ["title"] = "Extract Resource…", ["choice"] = "MainWindow.axaml <Window.Resources>", ["name"] = "PanelMargin" })).GetString());
        window = File.ReadAllText(Path.Combine(gallery, "MainWindow.axaml"));
        Assert.True(window.Contains("<Thickness x:Key=\"PanelMargin\">12</Thickness>", StringComparison.Ordinal)
            && window.Contains("<StackPanel Margin=\"{StaticResource PanelMargin}\"", StringComparison.Ordinal), $"Extract Resource did not reach the file on disk:\n{window}");

        string renamed = (await Call(client, McpTools.Rename, new(At(window, "MainWindow.axaml", "Name=\"G")) { ["newName"] = "Start" })).GetString()!;
        Assert.Equal("Renamed Go to Start in 2 files: MainWindow.axaml, MainWindow.axaml.cs", renamed);
        Assert.Contains("Start.Content = \"Start\";", File.ReadAllText(Path.Combine(gallery, "MainWindow.axaml.cs")), StringComparison.Ordinal);
        Assert.Contains("{Binding #Start.Content}", File.ReadAllText(Path.Combine(gallery, "MainWindow.axaml")), StringComparison.Ordinal);
        await client.DisposeAsync();

        // A second session, as after a restart: the same version's file, not decompiled again.
        window = File.ReadAllText(Path.Combine(gallery, "MainWindow.axaml"));
        await using (McpClient again = await Start(["--mcp", gallery], languageServer: false))
        {
            string second = Assert.Single((await Call(again, McpTools.Definition, At(window, "MainWindow.axaml", "<StackPan"))).EnumerateArray()).GetProperty("path").GetString()!;
            Assert.True(decompiled == second, $"The second session decompiled the type again into another folder: {second}, the first's {decompiled}");
        }
        Assert.True(File.GetLastWriteTimeUtc(decompiled) == written, "The second session wrote the decompiled file again");
        Assert.Single(Directory.GetDirectories(Path.Combine(OwnState, "MetadataAsSource", "Decompiled", "Avalonia.Controls")));
    }

    /// <summary>Aspire's code help for an agent: a service's connection string to the AppHost line declaring the resource, an
    /// AppHost's parameter with no value marked and set through its fix with the value given as name (in the run's own user
    /// secrets, never said back), and a resource renamed in the AppHost and the service that reads it.</summary>
    [Fact]
    public async Task An_agent_goes_to_sets_and_renames_aspire_resources_as_the_window_does()
    {
        string secrets = Path.Combine(TestRun.UserSecrets, AspireSecretsId, "secrets.json");
        string shop = await Taken("mcpaspire-");
        Directory.CreateDirectory(Path.GetDirectoryName(secrets)!);
        File.Delete(secrets);
        try
        {
            McpClient client = await Start(["--mcp", shop]);
            string api = File.ReadAllText(Path.Combine(shop, "Api", "Program.cs"));
            string host = File.ReadAllText(Path.Combine(shop, "Host", "Program.cs"));
            JsonElement declared = Assert.Single((await Call(client, McpTools.Definition, At(api, "Api/Program.cs", "GetConnectionString(\"conn"))).EnumerateArray());
            Assert.Equal((Path.Combine("Host", "Program.cs"), 4), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));

            Assert.Contains((await Call(client, McpTools.Diagnostics, new() { ["path"] = "Host/Program.cs" })).EnumerateArray(),
                d => d.GetProperty("message").GetString()!.StartsWith("Parameter 'password' has no value", StringComparison.Ordinal));
            Dictionary<string, object?> password = At(host, "Host/Program.cs", "AddParameter(\"pass");
            const string fix = "Set password in the AppHost's user secrets…";
            Assert.Contains($"{fix} [quickfix]", Strings(await Call(client, McpTools.CodeActions, password)));
            CallToolResult unvalued = await Result(client, McpTools.ApplyCodeAction, new(password) { ["title"] = fix });
            Assert.True(unvalued.IsError == true && Text(unvalued).Contains("give it as name", StringComparison.Ordinal), $"The fix was made with no value: {Text(unvalued)}");
            string set = (await Call(client, McpTools.ApplyCodeAction, new(password) { ["title"] = fix, ["name"] = "agent-secret" })).GetString()!;
            Assert.Equal("Set password in the AppHost's user secrets", set);
            Assert.Contains("\"Parameters:password\": \"agent-secret\"", File.ReadAllText(secrets), StringComparison.Ordinal);

            string renamed = (await Call(client, McpTools.Rename, new(At(api, "Api/Program.cs", "GetConnectionString(\"conn")) { ["newName"] = "store" })).GetString()!;
            Assert.StartsWith("Renamed connected to store in 2 files", renamed, StringComparison.Ordinal);
            Assert.Contains("builder.AddConnectionString(\"store\")", File.ReadAllText(Path.Combine(shop, "Host", "Program.cs")), StringComparison.Ordinal);
            Assert.Contains("GetConnectionString(\"store\")", File.ReadAllText(Path.Combine(shop, "Api", "Program.cs")), StringComparison.Ordinal);
            await client.DisposeAsync();
        }
        finally
        {
            File.Delete(secrets);
        }
    }

    [Fact]
    public async Task An_agent_reads_navigates_renames_and_refactors_wpf_xaml_through_passengers_own_xaml()
    {
        string gallery = await Taken("mcpwxaml-");
        await using McpClient client = await Start(["--mcp", gallery]);
        string window = File.ReadAllText(Path.Combine(gallery, "MainWindow.xaml"));

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = "MainWindow.xaml" })).GetString()!;
        Assert.True(outline.StartsWith("Window x:Class=\"Gallery.MainWindow\" (line 1)\n  Window.Resources (line 10)\n    Style TargetType=\"Button\" (line 11)\n", StringComparison.Ordinal)
            && outline.Contains("\n    Button x:Name=\"Go\" (line 30)", StringComparison.Ordinal), $"The window's outline is not its element tree:\n{outline}");
        Assert.Equal("class System.Windows.Controls.TextBlock : FrameworkElement",
            (await Call(client, McpTools.SymbolInfo, At(window, "MainWindow.xaml", "<StackPanel Margin=\"12\">\n        <TextBl"))).GetString());
        JsonElement accent = Assert.Single((await Call(client, McpTools.Definition, At(window, "MainWindow.xaml", "{StaticResource Acc"))).EnumerateArray());
        Assert.Equal((Path.Combine("Themes", "Brushes.xaml"), 4), (accent.GetProperty("path").GetString(), accent.GetProperty("line").GetInt32()));
        JsonElement library = Assert.Single((await Call(client, McpTools.Definition, At(window, "MainWindow.xaml", "<StackPanel Margin=\"12\">\n        <TextBl"))).EnumerateArray());
        Assert.EndsWith("System.Windows.Controls.TextBlock.cs", library.GetProperty("path").GetString(), StringComparison.Ordinal);

        string[] keyed = [.. Used(await Call(client, McpTools.Usages, At(window, "MainWindow.xaml", "{StaticResource Acc"))).Select(u => $"{u.Path}:{u.Line}")];
        Assert.True(keyed.Contains("App.xaml:12") && keyed.Contains("MainWindow.xaml:30") && keyed.Contains($"{Path.Combine("Themes", "Brushes.xaml")}:4"),
            $"The brush's key was not found where the project's XAML uses and defines it: {string.Join(", ", keyed)}");
        string[] named = [.. Used(await Call(client, McpTools.Usages, At(window, "MainWindow.xaml", "x:Name=\"G"))).Select(u => u.Text)];
        Assert.True(named.Any(u => u.Contains("Go.Content = \"Start\";", StringComparison.Ordinal)) && named.Any(u => u.Contains("ElementName=Go", StringComparison.Ordinal)),
            $"The button's name was not found where its file and its code-behind use it: {string.Join(" | ", named)}");
        // A binding WPF resolves at run time is found where the XAML says its data context's type: here its d:DataContext.
        string[] bound = [.. Used(await Call(client, McpTools.Usages, At(WpfXamlTests.Gallery.MainViewModel, Path.Combine("ViewModels", "MainViewModel.cs"), "string Gree")))
            .Select(u => $"{u.Path}:{u.Line}")];
        Assert.True(bound.Contains("MainWindow.xaml:29"), $"The property bound under the window's design-time data context was not found where it is bound: {string.Join(", ", bound)}");
        Assert.Contains("MainWindow.xaml:30", Used(await Call(client, McpTools.Usages, At(WpfXamlTests.Gallery.CodeBehind, "MainWindow.xaml.cs", "void On"))).Select(u => $"{u.Path}:{u.Line}"));

        Dictionary<string, object?> margin = At(window, "MainWindow.xaml", "<StackPanel Margin=\"1");
        Assert.Contains("Extract Resource… [refactor.extract], choices: MainWindow.xaml <Window.Resources>; App.xaml <Application.Resources>; Brushes.xaml <ResourceDictionary>",
            Strings(await Call(client, McpTools.CodeActions, margin)));
        CallToolResult nowhere = await Result(client, McpTools.ApplyCodeAction, new(margin) { ["title"] = "Extract Resource…", ["choice"] = "Elsewhere" });
        Assert.True(nowhere.IsError == true && Text(nowhere).Contains("it can go in: MainWindow.xaml <Window.Resources>", StringComparison.Ordinal),
            $"Extract Resource to a place it does not offer answered: {Text(nowhere)}");
        Dictionary<string, object?> button = At(window, "MainWindow.xaml", "<Butt");
        Assert.Equal("Extracted 2 properties of Button (Content, Background) to style 'ButtonStyle' in MainWindow.xaml <Window.Resources>",
            (await Call(client, McpTools.ApplyCodeAction, new(button) { ["title"] = "Extract Style…", ["choice"] = "MainWindow.xaml <Window.Resources>" })).GetString());
        Assert.Contains("<Style x:Key=\"ButtonStyle\" TargetType=\"Button\">", File.ReadAllText(Path.Combine(gallery, "MainWindow.xaml")), StringComparison.Ordinal);

        string renamed = (await Call(client, McpTools.Rename, new(At(File.ReadAllText(Path.Combine(gallery, "MainWindow.xaml")), "MainWindow.xaml", "x:Name=\"G")) { ["newName"] = "Start" })).GetString()!;
        Assert.Equal("Renamed Go to Start in 2 files: MainWindow.xaml, MainWindow.xaml.cs", renamed);
        Assert.Contains("Start.Content = \"Start\";", File.ReadAllText(Path.Combine(gallery, "MainWindow.xaml.cs")), StringComparison.Ordinal);
        Assert.Contains("ElementName=Start", File.ReadAllText(Path.Combine(gallery, "MainWindow.xaml")), StringComparison.Ordinal);
    }

    /// <summary>A script, a stylesheet and a package.json, with the project's own ESLint and Prettier (the tests' packages
    /// copied into its node_modules): each tool through the file's language server, ESLint's fix first among the actions,
    /// Prettier formatting what it covers, and JSON's Go to Declaration and Rename, which its server lacks, Passenger's own;
    /// its lack of usages said plainly.</summary>
    [Fact]
    public async Task An_agent_reads_navigates_renames_fixes_and_formats_the_web_languages_through_their_servers_eslint_and_prettier()
    {
        await WebLanguagesTests.SkipWithoutNodeAsync(typescript: true);
        await EslintPrettierTests.Project.SkipWithoutNodeAsync();
        string web = Made("mcpweb-", made =>
        {
            CopyPackages(made);
            File.WriteAllText(Path.Combine(made, "package.json"), "{\n  \"name\": \"shop-client\",\n  \"version\": \"1.0.0\",\n  \"private\": true,\n  \"type\": \"module\",\n"
                + "  \"scripts\": {\n    \"build\": \"node src/app.js\",\n    \"all\": \"npm run build\"\n  },\n"
                + $"  \"devDependencies\": {{\n    \"eslint\": \"{EslintPrettierTests.Project.VersionOf("eslint")}\",\n    \"prettier\": \"{EslintPrettierTests.Project.VersionOf("prettier")}\"\n  }}\n}}\n");
            File.WriteAllText(Path.Combine(made, "eslint.config.js"), EslintPrettierTests.Project.Config);
            File.WriteAllText(Path.Combine(made, ".prettierrc"), "{ \"semi\": false, \"singleQuote\": true }\n");
            Directory.CreateDirectory(Path.Combine(made, "src"));
            File.WriteAllText(Path.Combine(made, "src", "app.js"), "function greet(name) {\n  return `Hello, ${name}`\n}\nlet total = greet('Ada')\nconsole.log(total)\n");
            File.WriteAllText(Path.Combine(made, "src", "format.js"), EslintPrettierTests.Project.Format);
            Directory.CreateDirectory(Path.Combine(made, "styles"));
            File.WriteAllText(Path.Combine(made, "styles", "site.css"), ":root {\n  --accent: #3574f0;\n}\n\n.cart {\n  color: var(--accent);\n}\n");
        });
        await using McpClient client = await Start(["--mcp", web]);
        string app = Path.Combine("src", "app.js");
        string script = File.ReadAllText(Path.Combine(web, app));

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = app })).GetString()!;
        Assert.True(outline.Contains("greet (function, line 1)", StringComparison.Ordinal) && outline.Contains("total (variable, line 4)", StringComparison.Ordinal),
            $"The script's outline is not its server's:\n{outline}");
        Dictionary<string, object?> call = At(script, app, "let total = gre");
        Assert.Contains("function greet(name: any): string", (await Call(client, McpTools.SymbolInfo, call)).GetString(), StringComparison.Ordinal);
        JsonElement declared = Assert.Single((await Call(client, McpTools.Definition, call)).EnumerateArray());
        Assert.Equal((app, 1), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        Assert.Equal([1, 4], Used(await Call(client, McpTools.Usages, call)).Select(u => u.Line).Order());

        JsonElement problem = Assert.Single((await Call(client, McpTools.Diagnostics, new() { ["path"] = app })).EnumerateArray(),
            d => d.GetProperty("message").GetString()!.Contains("(prefer-const)", StringComparison.Ordinal));
        Assert.Equal(("error", 4), (problem.GetProperty("severity").GetString(), problem.GetProperty("line").GetInt32()));
        Dictionary<string, object?> let = At(script, app, "let t");
        string[] actions = Strings(await Call(client, McpTools.CodeActions, let));
        Assert.True(actions.Length > 0 && actions[0].StartsWith("ESLint: Fix 'prefer-const'", StringComparison.Ordinal), $"ESLint's fix is not first among the line's actions: {string.Join(" | ", actions)}");
        Assert.Equal($"Applied 'ESLint: Fix 'prefer-const'' to {app}", (await Call(client, McpTools.ApplyCodeAction, new(let) { ["title"] = "ESLint: Fix 'prefer-const'" })).GetString());
        Assert.Contains("const total = greet('Ada')", File.ReadAllText(Path.Combine(web, app)), StringComparison.Ordinal);

        string renamed = (await Call(client, McpTools.Rename, new(At(script, app, "function gre")) { ["newName"] = "welcome" })).GetString()!;
        Assert.Equal($"Renamed in 1 file: {app}", renamed);
        string written = File.ReadAllText(Path.Combine(web, app));
        Assert.True(written.Contains("function welcome(name)", StringComparison.Ordinal) && written.Contains("welcome('Ada')", StringComparison.Ordinal),
            $"The rename did not reach the script on disk:\n{written}");

        Assert.StartsWith("Prettier: formatted ", (await Call(client, McpTools.Format, new() { ["path"] = Path.Combine("src", "format.js") })).GetString(), StringComparison.Ordinal);
        Assert.Equal("const label = { name: 'tea', price: 3 }\nconsole.log(label)\n", File.ReadAllText(Path.Combine(web, "src", "format.js")));

        string css = Path.Combine("styles", "site.css");
        string sheet = File.ReadAllText(Path.Combine(web, css));
        Assert.Equal(2, Assert.Single((await Call(client, McpTools.Definition, At(sheet, css, "var(--acc"))).EnumerateArray()).GetProperty("line").GetInt32());
        await Call(client, McpTools.Rename, new(At(sheet, css, "var(--acc")) { ["newName"] = "--brand" });
        Assert.Equal(":root {\n  --brand: #3574f0;\n}\n\n.cart {\n  color: var(--brand);\n}\n", File.ReadAllText(Path.Combine(web, css)));

        string manifest = File.ReadAllText(Path.Combine(web, "package.json"));
        CallToolResult none = await Result(client, McpTools.Usages, At(manifest, "package.json", "\"bui"));
        Assert.True(none.IsError == true && Text(none).Contains("JSON has no usages", StringComparison.Ordinal), $"usages in package.json answered: {Text(none)}");
        JsonElement package = Assert.Single((await Call(client, McpTools.Definition, At(manifest, "package.json", "\"esl"))).EnumerateArray());
        Assert.Equal(Path.Combine("node_modules", "eslint", "package.json"), package.GetProperty("path").GetString());
        Assert.Equal("Renamed build to compile in package.json, and in 1 script that runs it",
            (await Call(client, McpTools.Rename, new(At(manifest, "package.json", "\"bui")) { ["newName"] = "compile" })).GetString());
        manifest = File.ReadAllText(Path.Combine(web, "package.json"));
        Assert.True(manifest.Contains("\"compile\": \"node src/app.js\"", StringComparison.Ordinal) && manifest.Contains("\"all\": \"npm run compile\"", StringComparison.Ordinal),
            $"The script's rename did not reach package.json on disk:\n{manifest}");
    }

    /// <summary>TypeScript's refactorings, which its server makes by commands of its own that hand Passenger their edits: an
    /// agent extracts a call into a function, told the name it was given and where, and moves a class to a new file, which
    /// the file that used it then imports it from, every file written. Skipped without a Node.js.</summary>
    [Fact]
    public async Task An_agent_applies_typescripts_refactorings_which_its_server_makes_by_commands_of_its_own()
    {
        await WebLanguagesTests.SkipWithoutNodeAsync(typescript: true);
        string web = Made("mcptsref-", made =>
        {
            Directory.CreateDirectory(Path.Combine(made, "src"));
            File.WriteAllText(Path.Combine(made, "tsconfig.json"), "{\n  \"compilerOptions\": { \"target\": \"ES2022\", \"module\": \"ES2022\", \"strict\": true },\n  \"include\": [\"src\"]\n}\n");
            File.WriteAllText(Path.Combine(made, "src", "shapes.ts"), Shapes);
            File.WriteAllText(Path.Combine(made, "src", "main.ts"), "import { Circle, total } from \"./shapes\";\n\nconst shapes = [new Circle(1), new Circle(2)];\nconsole.log(total(shapes));\n");
        });
        await using McpClient client = await Start(["--mcp", web], languageServer: false);
        string main = Path.Combine("src", "main.ts");
        string shapes = Path.Combine("src", "shapes.ts");
        string script = File.ReadAllText(Path.Combine(web, main));

        Dictionary<string, object?> call = At(script, main, "console.log(");
        Dictionary<string, object?> callEnd = At(script, main, "console.log(total(shapes)");
        string extracted = (await Call(client, McpTools.ApplyCodeAction, new(call)
            { ["endLine"] = callEnd["line"], ["endColumn"] = callEnd["column"], ["title"] = "Extract to function in module scope" })).GetString()!;
        Assert.StartsWith($"Applied 'Extract to function in module scope' to {main}, naming what it made newFunction at {main}:4:", extracted, StringComparison.Ordinal);
        string written = File.ReadAllText(Path.Combine(web, main));
        Assert.True(written.Contains("console.log(newFunction());", StringComparison.Ordinal) && written.Contains("return total(shapes);", StringComparison.Ordinal),
            $"The call was not extracted into a function on disk:\n{written}");

        string moved = (await Call(client, McpTools.ApplyCodeAction, new(At(Shapes, shapes, "export class Cir")) { ["title"] = "Move to a new file" })).GetString()!;
        string circle = Path.Combine("src", "Circle.ts");
        Assert.True(moved.StartsWith("Applied 'Move to a new file' to ", StringComparison.Ordinal) && moved.EndsWith($", making {circle}", StringComparison.Ordinal),
            $"Move to a new file answered: {moved}");
        Assert.Contains("export class Circle implements Shape", File.ReadAllText(Path.Combine(web, circle)), StringComparison.Ordinal);
        Assert.DoesNotContain("class Circle", File.ReadAllText(Path.Combine(web, shapes)), StringComparison.Ordinal);
        Assert.Contains("from \"./Circle\";", File.ReadAllText(Path.Combine(web, main)), StringComparison.Ordinal);
    }

    /// <summary>An agent's TypeScript rename at an imported name, and at a use of another, renames each where it is declared
    /// and everywhere it is used, every file written, never an alias in the importing file alone. Skipped without a Node.js.</summary>
    [Fact]
    public async Task An_agents_typescript_rename_at_an_imported_name_renames_it_where_it_is_declared_and_everywhere_it_is_used()
    {
        await WebLanguagesTests.SkipWithoutNodeAsync(typescript: true);
        string web = Made("mcptsrename-", made =>
        {
            Directory.CreateDirectory(Path.Combine(made, "src"));
            File.WriteAllText(Path.Combine(made, "tsconfig.json"), "{\n  \"compilerOptions\": { \"target\": \"ES2022\", \"module\": \"ES2022\", \"strict\": true },\n  \"include\": [\"src\"]\n}\n");
            File.WriteAllText(Path.Combine(made, "src", "shapes.ts"), Shapes);
            File.WriteAllText(Path.Combine(made, "src", "main.ts"), "import { Circle, total } from \"./shapes\";\n\nconst shapes = [new Circle(1), new Circle(2)];\nconsole.log(total(shapes));\n");
        });
        await using McpClient client = await Start(["--mcp", web], languageServer: false);
        string main = Path.Combine("src", "main.ts");
        string shapes = Path.Combine("src", "shapes.ts");

        string imported = (await Call(client, McpTools.Rename, new(At(File.ReadAllText(Path.Combine(web, main)), main, "import { Circle, tot")) { ["newName"] = "sumOf" })).GetString()!;
        Assert.True(imported.StartsWith("Renamed in 2 files: ", StringComparison.Ordinal) && imported.Contains(shapes, StringComparison.Ordinal), $"The rename at the import answered: {imported}");
        Assert.Contains("export function sumOf(shapes: Shape[]): number", File.ReadAllText(Path.Combine(web, shapes)), StringComparison.Ordinal);
        string script = File.ReadAllText(Path.Combine(web, main));
        Assert.True(script.Contains("import { Circle, sumOf } from \"./shapes\";", StringComparison.Ordinal) && script.Contains("console.log(sumOf(shapes));", StringComparison.Ordinal),
            $"The importing file was not renamed with the function:\n{script}");

        await Call(client, McpTools.Rename, new(At(script, main, "new Cir")) { ["newName"] = "Round" });
        Assert.Contains("export class Round implements Shape", File.ReadAllText(Path.Combine(web, shapes)), StringComparison.Ordinal);
        script = File.ReadAllText(Path.Combine(web, main));
        Assert.True(script.Contains("import { Round, sumOf } from \"./shapes\";", StringComparison.Ordinal) && script.Contains("[new Round(1), new Round(2)]", StringComparison.Ordinal),
            $"The importing file was not renamed with the class:\n{script}");
    }

    /// <summary>The module of shapes McpLanguagesTests' TypeScript refactors and renames.</summary>
    private const string Shapes = "export interface Shape {\n  area(): number;\n}\n\nexport class Circle implements Shape {\n  constructor(public radius: number) {}\n"
        + "  area(): number {\n    return Math.PI * this.radius * this.radius;\n  }\n}\n\nexport function total(shapes: Shape[]): number {\n"
        + "  return shapes.reduce((sum, s) => sum + s.area(), 0);\n}\n";

    /// <summary>An agent's diagnostics and ESLint's fixes answer from the checks of the text as it stands: TypeScript's problem
    /// in a file just opened, then the edited text's own, ESLint's in its first call as its server starts, and ESLint's fix
    /// listed, then applied, each in a file let go since the call before (as a pause lets it go). Skipped without a Node.js.</summary>
    [Fact]
    public async Task An_agents_diagnostics_and_eslint_fixes_wait_for_the_checks_of_the_text_as_it_stands()
    {
        await WebLanguagesTests.SkipWithoutNodeAsync(typescript: true);
        await EslintPrettierTests.Project.SkipWithoutNodeAsync();
        string web = Made("mcpchecks-", made =>
        {
            CopyPackages(made);
            File.WriteAllText(Path.Combine(made, "package.json"), EslintPrettierTests.Project.Package);
            File.WriteAllText(Path.Combine(made, "eslint.config.js"), EslintPrettierTests.Project.Config);
            Directory.CreateDirectory(Path.Combine(made, "src"));
            File.WriteAllText(Path.Combine(made, "src", "app.js"), "let total = 1\nconsole.log(total)\n");
            File.WriteAllText(Path.Combine(made, "src", "main.ts"), "const count: number = \"two\";\nconsole.log(count);\n");
        });
        await using McpClient client = await Start(["--mcp", web], languageServer: false);
        string app = Path.Combine("src", "app.js");
        string main = Path.Combine("src", "main.ts");

        string[] linted = Problems(await Call(client, McpTools.Diagnostics, new() { ["path"] = app }));
        Assert.True(linted.Any(p => p.Contains("(prefer-const)", StringComparison.Ordinal)), $"ESLint's first call, its server starting, missed its problem: {string.Join(" | ", linted)}");

        string[] opened = Problems(await Call(client, McpTools.Diagnostics, new() { ["path"] = main }));
        Assert.True(opened is ["1: TS2322: Type 'string' is not assignable to type 'number'."], $"diagnostics of a TypeScript file just opened: {string.Join(" | ", opened)}");
        await Call(client, McpTools.Edit, new() { ["path"] = main, ["oldText"] = "const count: number = \"two\";", ["newText"] = "const count: number = 2;\nconst label: string = count;" });
        string[] edited = Problems(await Call(client, McpTools.Diagnostics, new() { ["path"] = main }));
        Assert.True(edited is ["2: TS2322: Type 'number' is not assignable to type 'string'."], $"diagnostics right after an edit did not answer the edited text: {string.Join(" | ", edited)}");

        Dictionary<string, object?> let = At(File.ReadAllText(Path.Combine(web, app)), app, "let t");
        string[] actions = Strings(await Call(client, McpTools.CodeActions, let));
        Assert.True(actions.Contains("ESLint: Fix 'prefer-const' [quickfix]"), $"ESLint's fix was not listed in a file let go: {string.Join(" | ", actions)}");
        await Call(client, McpTools.Diagnostics, new() { ["path"] = main });
        Assert.Equal($"Applied 'ESLint: Fix 'prefer-const'' to {app}", (await Call(client, McpTools.ApplyCodeAction, new(let) { ["title"] = "ESLint: Fix 'prefer-const'" })).GetString());
        Assert.StartsWith("const total = 1", File.ReadAllText(Path.Combine(web, app)), StringComparison.Ordinal);
    }

    /// <summary>A project whose property comes from its Directory.Build.props and whose target another names: what MSBuild's
    /// evaluation makes of each name, where each is defined (the SDK's own files among them), the file's elements, and the
    /// commands MSBuild files lack said plainly.</summary>
    [Fact]
    public async Task An_agent_reads_a_project_files_evaluated_values_and_goes_where_each_name_is_defined()
    {
        string shop = Made("mcpmsbuild-", made =>
        {
            File.WriteAllText(Path.Combine(made, "Directory.Build.props"), "<Project>\n  <PropertyGroup>\n    <Company>Passenger</Company>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(made, "Shop.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                + "    <Product>$(Company) Shop</Product>\n  </PropertyGroup>\n  <Target Name=\"Greet\" AfterTargets=\"Build\">\n    <Message Text=\"$(Product)\" />\n  </Target>\n"
                + "  <Target Name=\"Wave\" DependsOnTargets=\"Greet\" />\n</Project>\n");
        });
        // MSBuild files' help asks nothing of C#'s server.
        await using McpClient client = await Start(["--mcp", shop], languageServer: false);
        string project = File.ReadAllText(Path.Combine(shop, "Shop.csproj"));

        Dictionary<string, object?> company = At(project, "Shop.csproj", "$(Comp");
        // The hover card's lines end as the system's do (Windows' "\r\n").
        string said = (await Call(client, McpTools.SymbolInfo, company)).GetString()!.ReplaceLineEndings("\n");
        Assert.True(said.StartsWith("Company = Passenger\nMSBuild property, set in Directory.Build.props, line 3", StringComparison.Ordinal),
            $"symbol_info on a property did not say its evaluated value and where it was set: {said}");
        JsonElement set = Assert.Single((await Call(client, McpTools.Definition, company)).EnumerateArray());
        Assert.Equal(("Directory.Build.props", 3), (set.GetProperty("path").GetString(), set.GetProperty("line").GetInt32()));
        Assert.Equal("<Company>Passenger</Company>", set.GetProperty("text").GetString());
        JsonElement target = Assert.Single((await Call(client, McpTools.Definition, At(project, "Shop.csproj", "DependsOnTargets=\"Gre"))).EnumerateArray());
        Assert.Equal(("Shop.csproj", 6), (target.GetProperty("path").GetString(), target.GetProperty("line").GetInt32()));
        string[] sdk = [.. (await Call(client, McpTools.Definition, At(project, "Shop.csproj", "Sdk=\"Microsoft.NE"))).EnumerateArray().Select(p => p.GetProperty("path").GetString()!)];
        Assert.True(sdk.Length > 0 && sdk.All(Path.IsPathRooted) && sdk.Any(p => p.EndsWith("Sdk.props", StringComparison.Ordinal)),
            $"The SDK's definition is not its own files: {string.Join(", ", sdk)}");

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = "Shop.csproj" })).GetString()!;
        Assert.Equal($"Project Sdk=\"Microsoft.NET.Sdk\" (line 1)\n  PropertyGroup (line 2)\n    TargetFramework = {TestProjects.Framework} (property, line 3)\n"
            + "    Product = $(Company) Shop (property, line 4)\n  Target Name=\"Greet\" (target, line 6)\n    Message (task, line 7)\n  Target Name=\"Wave\" (target, line 9)", outline);

        foreach ((string tool, Dictionary<string, object?> arguments) in new (string, Dictionary<string, object?>)[]
        {
            (McpTools.Usages, company),
            (McpTools.Rename, new(company) { ["newName"] = "Maker" }),
            (McpTools.CodeActions, company),
            (McpTools.Format, new() { ["path"] = "Shop.csproj" }),
        })
        {
            CallToolResult lacking = await Result(client, tool, arguments);
            Assert.True(lacking.IsError == true && Text(lacking).Contains("MSBuild files have no ", StringComparison.Ordinal)
                && Text(lacking).Contains("definition goes to where a property", StringComparison.Ordinal), $"{tool} on a project file answered: {Text(lacking)}");
        }
    }

    /// <summary>A web app's routes, read from its code: the workspace with routes lists each with its handler, parameters and
    /// place, and without them says nothing of routes.</summary>
    [Fact]
    public async Task An_agent_lists_a_web_apps_routes_read_from_its_code()
    {
        string shop = await Taken("mcproutes-");
        await using McpClient client = await Start(["--mcp", shop]);

        Assert.False((await Call(client, McpTools.Workspace)).TryGetProperty("routes", out JsonElement unasked) && unasked.ValueKind != JsonValueKind.Null,
            "The workspace listed routes unasked");
        JsonElement routes = (await Call(client, McpTools.Workspace, new() { ["routes"] = true })).GetProperty("routes");
        string[] listed = [.. routes.EnumerateArray().Select(r => r.GetProperty("route").GetString()!)];
        foreach (string route in new[] { "GET /api/todos/{id:int}", "PUT /orders/{id:guid}", "GET /api/Products/{id}", "POST /Orders/Edit/{id:int}?handler=Cancel", "GET /counter", "HUB /chat" })
        {
            Assert.True(listed.Contains(route), $"The agent's routes lack {route}: {string.Join(", ", listed)}");
        }
        JsonElement todo = routes.EnumerateArray().First(r => r.GetProperty("route").GetString() == "GET /api/todos/{id:int}");
        Assert.Equal(("Minimal API", "lambda", "Program.cs", 11), (todo.GetProperty("kind").GetString(), todo.GetProperty("handler").GetString(),
            todo.GetProperty("path").GetString(), todo.GetProperty("line").GetInt32()));
        Assert.Equal(["id:int (route)"], Strings(todo.GetProperty("parameters")));
        JsonElement order = routes.EnumerateArray().First(r => r.GetProperty("route").GetString() == "PUT /orders/{id:guid}");
        Assert.Equal(("Orders.Update", "Order"), (order.GetProperty("handler").GetString(), order.GetProperty("body").GetString()));
    }

    /// <summary>The tests' own npm packages (eslint and prettier) copied into a folder's node_modules.</summary>
    private static void CopyPackages(string made)
    {
        string packages = Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules");
        foreach (string file in Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(made, "node_modules", Path.GetRelativePath(packages, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>A file's errors and warnings as diagnostics lists them, each as "line: message".</summary>
    private static string[] Problems(JsonElement found) =>
        [.. found.EnumerateArray().Select(d => $"{d.GetProperty("line").GetInt32()}: {d.GetProperty("message").GetString()}")];

    /// <summary>A folder of the test's own, written by <paramref name="write"/>, removed with the test.</summary>
    private string Made(string name, Action<string> write)
    {
        string made = PathNames.Canonical(TestFolders.Create(name));
        DeleteAtEnd(made);
        TestFolders.Prepare(() => write(made), made);
        return made;
    }

    /// <summary>A Blazor app's C#, as the window's code help reads it: a configuration key's read goes to where appsettings.json
    /// gives it and its card says the value, and a Razor component and a member of its <c>@code</c> are named by symbol as a
    /// C# type and its members are.</summary>
    [Fact]
    public async Task An_agent_goes_to_a_configuration_keys_value_and_names_a_razor_component_and_its_members_by_symbol()
    {
        const string Salutation = "<button @onclick=\"Clicked\">Click</button>\n\n@code {\n    private int count;\n\n    private void Clicked() => count++;\n}\n";
        string shop = PathNames.Canonical(TestFolders.Create("mcp-blazor-"));
        DeleteAtEnd(shop);
        await TestFolders.PrepareAsync(async () =>
        {
            await BlazorApp.CreateAsync(shop);
            string start = File.ReadAllText(Path.Combine(shop, "Program.cs"));
            File.WriteAllText(Path.Combine(shop, "Program.cs"), start.Replace("var builder = WebApplication.CreateBuilder(args);",
                "var builder = WebApplication.CreateBuilder(args);\nstring? greeting = builder.Configuration[\"Greeting:Text\"];", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(shop, "appsettings.json"), "{\n  \"AllowedHosts\": \"*\",\n  \"Greeting\": {\n    \"Text\": \"hi\"\n  }\n}\n");
            File.WriteAllText(Path.Combine(shop, "Components", "Salutation.razor"), Salutation);
            await Restore.RunAsync(shop);
        }, shop);
        await using McpClient client = await Start(["--mcp", shop]);
        string program = File.ReadAllText(Path.Combine(shop, "Program.cs"));
        Assert.Contains("[\"Greeting:Text\"]", program, StringComparison.Ordinal);

        Dictionary<string, object?> key = At(program, "Program.cs", "[\"Greeting:Te");
        JsonElement given = Assert.Single((await Call(client, McpTools.Definition, key)).EnumerateArray());
        Assert.Equal(("appsettings.json", 4, "\"Text\": \"hi\""), (given.GetProperty("path").GetString(), given.GetProperty("line").GetInt32(), given.GetProperty("text").GetString()));
        string card = (await Call(client, McpTools.SymbolInfo, key)).GetString()!.ReplaceLineEndings("\n");
        Assert.True(card.Contains("System.String", StringComparison.Ordinal) && card.Contains("Greeting:Text:\nappsettings.json = hi", StringComparison.Ordinal),
            $"symbol_info on a configuration key's read did not say where its value is given, as the hover card does:\n{card}");

        string razor = Path.Combine("Components", "Salutation.razor");
        JsonElement member = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Clicked", ["exact"] = true })).EnumerateArray());
        Assert.Equal((razor, 6, 18, "M:Shop.Components.Salutation.Clicked"),
            (member.GetProperty("path").GetString(), member.GetProperty("line").GetInt32(), member.GetProperty("column").GetInt32(), member.GetProperty("id").GetString()));
        JsonElement declared = Assert.Single((await Call(client, McpTools.Definition, new() { ["symbol"] = "Salutation.Clicked" })).EnumerateArray());
        Assert.Equal((razor, 6), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        Assert.Equal([1, 6], Used(await Call(client, McpTools.Usages, new() { ["symbol"] = "Salutation.Clicked" })).Select(u => u.Line).Order());
        string component = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Salutation" })).GetString()!;
        Assert.StartsWith($"Salutation (class) at {razor}:1:1, T:Shop.Components.Salutation", component, StringComparison.Ordinal);
    }

    /// <summary>The test's own restored folder (<see cref="Restored"/>), removed with the test.</summary>
    private async Task<string> Taken(string name)
    {
        string taken = await restored.Take(name);
        DeleteAtEnd(taken);
        return taken;
    }

    /// <summary>The class's restored folders, Avalonia's and WPF's galleries, the Aspire AppHost and its service, and the web
    /// app of routes, each written as its test needs it: all four are restored at once, begun as the class starts, so the
    /// tests that take none run meanwhile, rather than each test's in its turn. Each test takes its own; those no test took
    /// go with the class.</summary>
    public sealed class Restored : IAsyncLifetime
    {
        private readonly Dictionary<string, string> folders = new(StringComparer.Ordinal);
        private readonly HashSet<string> taken = new(StringComparer.Ordinal);
        private Task restoring = Task.CompletedTask;

        public ValueTask InitializeAsync()
        {
            restoring = Waiting.Apart(RestoreAsync);
            return ValueTask.CompletedTask;
        }

        private async Task RestoreAsync()
        {
            (string Name, Func<string, Task> Write)[] kinds = [("mcpaxaml-", Avalonia), ("mcpaspire-", Aspire), ("mcpwxaml-", Wpf), ("mcproutes-", Routes)];
            try
            {
                foreach ((string kind, Func<string, Task> write) in kinds)
                {
                    string folder = PathNames.Canonical(TestFolders.Unowned(kind));
                    folders[kind] = folder;
                    await write(folder);
                }
                await Task.WhenAll(folders.Values.Select(Restore.RunAsync));
            }
            catch
            {
                foreach (string folder in folders.Values)
                {
                    TestFolders.Delete(folder);
                }
                folders.Clear();
                throw;
            }
        }

        public async Task<string> Take(string name)
        {
            await restoring;
            taken.Add(name);
            return folders[name];
        }

        /// <summary>Once the restore has ended, the folders no test took go; one that failed took its folders with it, and
        /// each test that took one failed saying why.</summary>
        public async ValueTask DisposeAsync()
        {
            await Task.WhenAny(restoring);
            foreach ((string kind, string folder) in folders)
            {
                if (!taken.Contains(kind))
                {
                    TestFolders.Delete(folder);
                }
            }
        }

        private static Task Avalonia(string made)
        {
            File.WriteAllText(Path.Combine(made, "Gallery.csproj"), AvaloniaApp.HeadlessProject());
            Directory.CreateDirectory(Path.Combine(made, "ViewModels"));
            Directory.CreateDirectory(Path.Combine(made, "Styles"));
            File.WriteAllText(Path.Combine(made, "ViewModels", "MainViewModel.cs"), XamlTests.Gallery.MainViewModel);
            File.WriteAllText(Path.Combine(made, "Styles", "Palette.axaml"), XamlTests.Gallery.Palette);
            File.WriteAllText(Path.Combine(made, "App.axaml"), XamlTests.Gallery.AppXaml);
            File.WriteAllText(Path.Combine(made, "App.axaml.cs"),
                "using Avalonia;\nusing Avalonia.Markup.Xaml;\n\nnamespace Gallery;\n\npublic partial class App : Application\n{\n    public override void Initialize() => AvaloniaXamlLoader.Load(this);\n}\n");
            File.WriteAllText(Path.Combine(made, "Program.cs"), "namespace Gallery;\n\npublic static class Program\n{\n    public static void Main()\n    {\n    }\n}\n");
            // The gallery's window, its button's Click handled, with a caption bound to a property its view model lacks.
            File.WriteAllText(Path.Combine(made, "MainWindow.axaml"), XamlTests.Gallery.Handled(XamlTests.Gallery.Window).Replace(
                "<TextBlock Text=\"{Binding #Go.Content}\" />", "<TextBlock Text=\"{Binding #Go.Content}\" />\n        <TextBlock Text=\"{Binding Subtitle}\" />", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(made, "MainWindow.axaml.cs"), XamlTests.Gallery.HandlingCodeBehind);
            return Task.CompletedTask;
        }

        private static Task Aspire(string made)
        {
            AspireCodeHelpTests.Project.WriteTo(made, AspireSecretsId);
            File.WriteAllText(Path.Combine(made, "Host", "Program.cs"), AspireCodeHelpTests.Project.Host.Replace("builder.Build().Run();",
                "var password = builder.AddParameter(\"password\", secret: true);\nbuilder.Build().Run();", StringComparison.Ordinal));
            AspireApp.UseFeed(made);
            return Task.CompletedTask;
        }

        private static Task Wpf(string made) => WpfXamlTests.Gallery.MakeAsync(made);

        private static Task Routes(string made)
        {
            WebRoutesTests.WriteShop(made, 5557);
            return Task.CompletedTask;
        }
    }

    /// <summary>A tool's path, line and column (from 1) just after the first <paramref name="after"/> in a file's text.</summary>
    private static Dictionary<string, object?> At(string text, string path, string after)
    {
        int at = text.IndexOf(after, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{path} has no {after}");
        at += after.Length;
        int line = text[..at].Count(c => c == '\n');
        int column = at - (text.LastIndexOf('\n', at - 1) + 1);
        return new() { ["path"] = path, ["line"] = line + 1, ["column"] = column + 1 };
    }

    private static string[] Strings(JsonElement list) => [.. list.EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>An agent on a compose file and a Dockerfile: their servers' outline and problems, Passenger's own declarations
    /// in the compose file (a service's, a build's Dockerfile), an image's registry page said, Passenger's own Rename of a
    /// service (a name taken refused) and the YAML server's layout, saved. Skipped without a Node.js that runs the YAML
    /// server.</summary>
    [Fact]
    public async Task An_agent_reads_a_compose_files_declarations_and_problems_renames_a_service_and_formats_it_through_the_yaml_server()
    {
        await DockerFilesTests.Shop.SkipWithoutNodeAsync(yaml: true);
        const string Compose = "services:\n  web:\n    image:   nginx:1.27\n    ports: [ \"8080:80\" ]\n    depends_on:   [api]\n  api:\n    build: .\n";
        string compose = Made("mcpcompose-", made =>
        {
            File.WriteAllText(Path.Combine(made, "compose.yaml"), Compose);
            File.WriteAllText(Path.Combine(made, "Dockerfile"), "FROM alpine:3.20\nFROMM nothing\nENTRYPOINT [\"sh\"]\n");
        });
        await using McpClient client = await Start(["--mcp", compose]);

        JsonElement service = Assert.Single((await Call(client, McpTools.Definition, At(Compose, "compose.yaml", "[ap"))).EnumerateArray());
        Assert.Equal(("compose.yaml", 6, 3), (service.GetProperty("path").GetString(), service.GetProperty("line").GetInt32(), service.GetProperty("column").GetInt32()));
        JsonElement dockerfile = Assert.Single((await Call(client, McpTools.Definition, At(Compose, "compose.yaml", "build: ."))).EnumerateArray());
        Assert.Equal(("Dockerfile", 1), (dockerfile.GetProperty("path").GetString(), dockerfile.GetProperty("line").GetInt32()));
        CallToolResult image = await Result(client, McpTools.Definition, At(Compose, "compose.yaml", "image:   ngi"));
        Assert.True(image.IsError == true && Text(image).Contains("https://hub.docker.com/_/nginx", StringComparison.Ordinal), $"definition on an image answered: {Text(image)}");

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = "compose.yaml" })).GetString()!;
        Assert.True(outline.Contains("web (", StringComparison.Ordinal) && outline.Contains("depends_on (", StringComparison.Ordinal), $"The compose file's outline is not its server's:\n{outline}");
        JsonElement problem = Assert.Single((await Call(client, McpTools.Diagnostics, new() { ["path"] = "Dockerfile" })).EnumerateArray());
        Assert.Contains("Unknown instruction: FROMM", problem.GetProperty("message").GetString(), StringComparison.Ordinal);

        string renamed = (await Call(client, McpTools.Rename, new(At(Compose, "compose.yaml", "[ap")) { ["newName"] = "backend" })).GetString()!;
        Assert.Equal("Renamed the service api to backend in 1 file: compose.yaml", renamed);
        string written = Compose.Replace("api", "backend", StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllText(Path.Combine(compose, "compose.yaml")));
        CallToolResult taken = await Result(client, McpTools.Rename, new(At(written, "compose.yaml", "  back")) { ["newName"] = "web" });
        Assert.True(taken.IsError == true && Text(taken).EndsWith(": compose.yaml has a service named web already", StringComparison.Ordinal), $"A rename to a name taken answered: {Text(taken)}");

        Assert.StartsWith("Reformatted", (await Call(client, McpTools.Format, new() { ["path"] = "compose.yaml" })).GetString(), StringComparison.Ordinal);
        Assert.Equal("services:\n  web:\n    image: nginx:1.27\n    ports: [\"8080:80\"]\n    depends_on: [backend]\n  backend:\n    build: .\n", File.ReadAllText(Path.Combine(compose, "compose.yaml")));
    }
}
