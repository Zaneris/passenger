using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's answers that grow with the code or the folder held within what its client takes from one result
/// (<see cref="AgentAnswers.MostCharacters"/>), each saying how many it left out and how to ask for them, and that way
/// reaching the rest: a symbol used thousands of times, its callers, a text found thousands of times, a name thousands of
/// methods match, a file of thousands of members and warnings, a build of them, a file's text before a change, a long
/// history read on past each answer, a commit of a thousand files, a thousand tests, the IDE's actions, and a solution of
/// hundreds of projects and thousands of routes.</summary>
public sealed class McpLongAnswersTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpLongAnswersTests(RestoredFolders folders)
        : base(folders)
    {
    }

    private const int CallFiles = 60;

    private const int CallsEach = 50;

    private const int BigMembers = 2500;

    [Fact]
    public async Task Every_answer_that_grows_with_the_code_holds_to_the_limit_saying_how_many_it_left_out_and_how_to_read_them()
    {
        await TakeFolderAsync();
        Write("Much.cs", "static class Much\n{\n    public static int Used(int value) => value;\n}\n");
        for (int file = 0; file < CallFiles; file++)
        {
            Write($"Calls/C{file}.cs", $"static class C{file}\n{{\n{string.Concat(Enumerable.Range(0, CallsEach).Select(m => $"    public static int M{m}() => Much.Used({m});\n"))}}}\n");
        }
        Write("Big.cs", $"static class Big\n{{\n{string.Concat(Enumerable.Range(0, BigMembers).Select(i => $"    static void B{i}()\n    {{\n        int unused{i} = {i};\n    }}\n"))}}}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        const int Uses = CallFiles * CallsEach;

        // A symbol used thousands of times: every file counted, the first files' places listed, and within a file all of its own.
        JsonElement usages = Json(await Held(client, McpTools.Usages, new() { ["symbol"] = "Much.Used" }));
        JsonElement[] files = [.. usages.GetProperty("files").EnumerateArray()];
        int listed = files.Sum(f => f.TryGetProperty("places", out JsonElement places) ? places.GetArrayLength() : 0);
        int counted = files.Sum(f => f.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 0);
        Assert.True(usages.GetProperty("count").GetInt32() == Uses + 1 && files.Length == CallFiles + 1 && listed > 0 && listed + counted == Uses + 1,
            $"usages of a symbol used {Uses + 1:N0} times in {CallFiles + 1} files answered {usages.GetProperty("count")} places, {files.Length} files, {listed} listed and {counted} counted");
        string more = usages.GetProperty("more").GetString()!;
        Assert.True(Regex.IsMatch(more, @"^[\d,]+ places in [\d,]+ files counted, not listed: within, a folder or file, lists their places$")
            && Number(more) == counted, $"usages said what it left out as: {more}");
        string last = files.Last(f => f.TryGetProperty("count", out _) && f.GetProperty("path").GetString()!.StartsWith("Calls", StringComparison.Ordinal)).GetProperty("path").GetString()!;
        JsonElement within = await Call(client, McpTools.Usages, new() { ["symbol"] = "Much.Used", ["within"] = last });
        Assert.True(Used(within).Count == CallsEach && Used(within).All(u => u.Path == last) && !within.TryGetProperty("more", out _),
            $"usages within {last} answered: {Text(within)[..Math.Min(400, Text(within).Length)]}");

        // Its callers, the same way, and those within a file alone.
        CallToolResult callers = await Held(client, McpTools.Hierarchy, new() { ["symbol"] = "Much.Used" });
        LeftOut(callers, "caller", Uses, "within, a folder or file, lists those there");
        Assert.Equal(CallsEach, Json(await Held(client, McpTools.Hierarchy, new() { ["symbol"] = "Much.Used", ["within"] = last }, whole: true)).GetArrayLength());

        // A text found thousands of times, a thousand of them asked for.
        JsonElement found = Json(await Held(client, McpTools.FindInFiles, new() { ["text"] = "Much.Used(", ["limit"] = 1000 }));
        string foundMore = found.GetProperty("more").GetString()!;
        Assert.True(found.GetProperty("capped").GetBoolean() && found.GetProperty("matches").GetArrayLength() + Number(foundMore) == found.GetProperty("count").GetInt32()
            && foundMore.EndsWith("more matches left out: directory, mask or a narrower text lists fewer at once", StringComparison.Ordinal),
            $"find_in_files of {Uses:N0} matches listed {found.GetProperty("matches").GetArrayLength()} of {found.GetProperty("count")}, saying: {foundMore}");

        // Thousands of methods a name matches, more asked for than an answer holds.
        LeftOut(await Held(client, McpTools.FindSymbol, new() { ["query"] = "M", ["kind"] = "method", ["limit"] = 10_000 }), "symbol", null, "kind or a longer query narrows them");

        // A file of thousands of members and warnings: its findings, its outline and its text, each read on from where it stopped.
        CallToolResult findings = await Held(client, McpTools.Diagnostics, new() { ["path"] = "Big.cs" });
        Match findingsMore = Regex.Match(((TextContentBlock)findings.Content[^1]).Text, @"^[\d,]+ more findings left out: .+; severity (\w+) with from (\d+) lists those from that line on$");
        Assert.True(findings.Content.Count == 2 && findingsMore.Success, $"diagnostics of {BigMembers:N0} warnings ended: {((TextContentBlock)findings.Content[^1]).Text}");
        JsonElement nextFindings = Json(await Held(client, McpTools.Diagnostics, new() { ["path"] = "Big.cs", ["severity"] = findingsMore.Groups[1].Value, ["from"] = int.Parse(findingsMore.Groups[2].Value, CultureInfo.InvariantCulture) }));
        Assert.Equal(int.Parse(findingsMore.Groups[2].Value, CultureInfo.InvariantCulture), nextFindings[0].GetProperty("line").GetInt32());
        string outline = Text(await Held(client, McpTools.Outline, new() { ["path"] = "Big.cs" }));
        Match outlineMore = Regex.Match(outline, @"\n([\d,]+) more parts left out: from (\d+) lists those from that line on$");
        Assert.True(outlineMore.Success, $"outline of {BigMembers:N0} members ended: {outline[^Math.Min(300, outline.Length)..]}");
        string next = Text(await Held(client, McpTools.Outline, new() { ["path"] = "Big.cs", ["from"] = int.Parse(outlineMore.Groups[2].Value, CultureInfo.InvariantCulture) }));
        Assert.True(next.Split('\n')[0].Contains($", line {outlineMore.Groups[2].Value})", StringComparison.Ordinal), $"outline from {outlineMore.Groups[2].Value} began: {next.Split('\n')[0]}");
        string text = Text(await Held(client, McpTools.EditorText, new() { ["path"] = "Big.cs" }));
        Match textMore = Regex.Match(text, @"\n([\d,]+) more lines left out: from (\d+) reads on$");
        Assert.True(textMore.Success, $"editor_text of {BigMembers * 4:N0} lines ended: {text[^Math.Min(300, text.Length)..]}");
        string read = Text(await Held(client, McpTools.EditorText, new() { ["path"] = "Big.cs", ["from"] = int.Parse(textMore.Groups[2].Value, CultureInfo.InvariantCulture) }));
        Assert.StartsWith($"{textMore.Groups[2].Value,6}\t", read, StringComparison.Ordinal);

        // A build of thousands of warnings, and the Problems window's lists of them.
        JsonElement built = Json(await Held(client, McpTools.Build, new()));
        Assert.True(built.GetProperty("warnings").GetInt32() >= BigMembers && built.GetProperty("problems").GetArrayLength() + Number(built.GetProperty("more").GetString()!) == built.GetProperty("warnings").GetInt32()
            && built.GetProperty("more").GetString()!.EndsWith("diagnostics with no path, a folder's, project or severity lists the build's", StringComparison.Ordinal),
            $"A build of {built.GetProperty("warnings")} warnings listed {built.GetProperty("problems").GetArrayLength()}, saying: {built.GetProperty("more")}");
        Assert.True(Json(await Held(client, McpTools.Diagnostics, new())).TryGetProperty("more", out _), "Problems' lists of thousands of warnings said nothing left out");

        // A file's text before a change, read on from where it stopped.
        await Call(client, McpTools.Edit, new() { ["path"] = "Big.cs", ["oldText"] = "static void B0()", ["newText"] = "static void First()" });
        long set = (await Call(client, McpTools.LocalHistory, new() { ["path"] = "Big.cs" })).GetProperty("rows")[0].GetProperty("set").GetInt64();
        JsonElement before = Json(await Held(client, McpTools.LocalHistory, new() { ["path"] = "Big.cs", ["set"] = set }));
        Match beforeMore = Regex.Match(before.GetProperty("more").GetString()!, @"^([\d,]+) more lines left out: from (\d+) reads on$");
        Assert.True(beforeMore.Success && before.GetProperty("text").GetString()!.Contains("static void B0()", StringComparison.Ordinal),
            $"local_history of Big.cs before set {set} said: {before.GetProperty("more")}");
        int from = int.Parse(beforeMore.Groups[2].Value, CultureInfo.InvariantCulture);
        JsonElement rest = Json(await Held(client, McpTools.LocalHistory, new() { ["path"] = "Big.cs", ["set"] = set, ["from"] = from }));
        Assert.StartsWith(File.ReadAllLines(Path.Combine(Folder, "Big.cs"))[from - 1], rest.GetProperty("text").GetString()!, StringComparison.Ordinal);

        // A long history, a commit of a thousand files, and a thousand tests, in a folder of their own.
        string tests = await ManyTestsAsync();
        await using McpClient other = await Start(["--mcp", tests], languageServer: false);
        CallToolResult log = await Held(other, McpTools.VcsLog, new() { ["count"] = 500 });
        LeftOut(log, "commit", 500, "a path narrows them to a file's or folder's");
        string[] first = Subjects(log);
        Match readOn = Regex.Match(((TextContentBlock)log.Content[1]).Text, @"left out: before (\w+) reads on; ");
        Assert.True(readOn.Success && readOn.Groups[1].Value == Json(log)[first.Length - 1].GetProperty("hash").GetString(),
            $"vcs_log cut short named no before of its last commit to read on from: {((TextContentBlock)log.Content[1]).Text}");
        string[] after = Subjects(await Held(other, McpTools.VcsLog, new() { ["count"] = 500, ["before"] = readOn.Groups[1].Value }));
        Assert.True(after.Length > 0 && Number(after[0]) == Number(first[^1]) - 1,
            $"vcs_log before the last of {first.Length} commits ({first[^1]}) went on from {(after.Length > 0 ? after[0] : "none")}");
        Assert.Contains("is no commit of the history vcs_log lists of Many", Text(await Result(other, McpTools.VcsLog, new() { ["path"] = "Many", ["before"] = readOn.Groups[1].Value })), StringComparison.Ordinal);
        JsonElement commit = Assert.Single(Json(await Held(other, McpTools.VcsLog, new() { ["path"] = "Many" }, whole: true)).EnumerateArray());
        Assert.True(commit.GetProperty("changes").GetArrayLength() + Number(commit.GetProperty("more").GetString()!) == ManyFiles,
            $"vcs_log of a folder whose commit added {ManyFiles} files answered: {Text(commit)[..Math.Min(600, Text(commit).Length)]}");
        LeftOut(await Held(other, McpTools.TestsList, new() { ["limit"] = 5000 }), "test", ManyTests, "filter or state narrows them");

        // The IDE's actions, every one, within the limit, or saying how query narrows them.
        CallToolResult actions = await Held(other, McpTools.IdeActions, new());
        if (actions.Content.Count > 1)
        {
            LeftOut(actions, "action", null, "query, a group's name or a word of the action's, narrows them");
        }

        // Each commit's subject, as the history writes them.
        static string[] Subjects(CallToolResult listed) => [.. Json(listed).EnumerateArray().Select(c => c.GetProperty("subject").GetString()!)];
    }

    private const int WideLibraries = 400;

    private const int RouteFiles = 20;

    private const int RoutesEach = 100;

    [Fact]
    public async Task A_workspace_of_hundreds_of_projects_and_thousands_of_routes_holds_to_the_limit_and_within_lists_those_of_a_folder_or_file_whole()
    {
        string wide = await WideSolutionAsync();
        await using McpClient client = await Start(["--mcp", Path.Combine(wide, "Web.slnf")]);
        const int Projects = WideLibraries + 1;
        const int Routes = RouteFiles * RoutesEach;

        // Every project it can named, the first whole, and the first routes, each list given its share of the room.
        JsonElement workspace = Json(await Held(client, McpTools.Workspace, new() { ["routes"] = true }));
        JsonElement[] projects = [.. workspace.GetProperty("projects").EnumerateArray()];
        int whole = projects.Count(p => p.TryGetProperty("targetFrameworks", out _));
        string more = workspace.GetProperty("more").GetString()!;
        Match said = Regex.Match(more, @"^(?:([\d,]+) projects? named alone(?:, | and ))?(?:([\d,]+) more projects? left out(?:, | and ))?([\d,]+) more routes? left out: within, a folder or file, lists the projects and routes there whole$");
        int named = said.Groups[1].Success ? Number(said.Groups[1].Value) : 0;
        int projectsLeft = said.Groups[2].Success ? Number(said.Groups[2].Value) : 0;
        int routes = workspace.GetProperty("routes").GetArrayLength();
        Assert.True(said.Success && projects.Length == whole + named && projects.Length + projectsLeft == Projects && routes > 0 && routes + Number(said.Groups[3].Value) == Routes,
            $"workspace of {Projects} projects and {Routes:N0} routes listed {projects.Length} projects ({whole} whole) and {routes} routes, saying: {more}");

        // Within a project's folder that project whole, and within a file of routes all of its routes and no project.
        string lastNamed = projects[^1].GetProperty("path").GetString()!;
        JsonElement library = Json(await Held(client, McpTools.Workspace, new() { ["within"] = Path.GetDirectoryName(lastNamed) }, whole: true));
        JsonElement only = Assert.Single(library.GetProperty("projects").EnumerateArray());
        Assert.True(only.GetProperty("path").GetString() == lastNamed && only.GetProperty("targetFrameworks").GetArrayLength() == 1 && !library.TryGetProperty("more", out _),
            $"workspace within {Path.GetDirectoryName(lastNamed)} answered: {library}");
        string file = Path.Combine("Web", "Routes", $"R{RouteFiles - 1}.cs");
        JsonElement routesOf = Json(await Held(client, McpTools.Workspace, new() { ["routes"] = true, ["within"] = file }, whole: true));
        Assert.True(routesOf.GetProperty("projects").GetArrayLength() == 0 && routesOf.GetProperty("routes").GetArrayLength() == RoutesEach
            && routesOf.GetProperty("routes").EnumerateArray().All(r => r.GetProperty("path").GetString() == file),
            $"workspace's routes within {file} answered {routesOf.GetProperty("projects").GetArrayLength()} projects and {routesOf.GetProperty("routes").GetArrayLength()} routes, not its {RoutesEach}");
    }

    /// <summary>A solution of hundreds of libraries, never restored, and a web app of thousands of routes in files of their
    /// own, restored, which a solution filter opens alone.</summary>
    private async Task<string> WideSolutionAsync()
    {
        string wide = PathNames.Canonical(TestFolders.Create("mcp-wide-"));
        DeleteAtEnd(wide);
        StringBuilder solution = new("<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n");
        for (int library = 0; library < WideLibraries; library++)
        {
            TestFolders.Write(wide, $"Libraries/Library{library}/Library{library}.csproj", TestProjects.Library());
            solution.Append(CultureInfo.InvariantCulture, $"  <Project Path=\"Libraries/Library{library}/Library{library}.csproj\" />\n");
        }
        TestFolders.Write(wide, "Wide.slnx", solution.Append("</Solution>\n").ToString());
        TestFolders.Write(wide, "Web.slnf", "{\n  \"solution\": {\n    \"path\": \"Wide.slnx\",\n    \"projects\": [ \"Web\\\\Web.csproj\" ]\n  }\n}\n");
        TestFolders.Write(wide, "Web/Web.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(wide, "Web/Program.cs", "WebApplication app = WebApplication.CreateBuilder(args).Build();\n"
            + string.Concat(Enumerable.Range(0, RouteFiles).Select(f => $"Routes{f}.Map(app);\n")) + "app.Run();\n");
        for (int file = 0; file < RouteFiles; file++)
        {
            TestFolders.Write(wide, $"Web/Routes/R{file}.cs", $"static class Routes{file}\n{{\n    public static void Map(WebApplication app)\n    {{\n"
                + string.Concat(Enumerable.Range(0, RoutesEach).Select(r => $"        app.MapGet(\"/files/{file}/routes/{r}/{{id:int}}\", (int id) => id + {r});\n")) + "    }\n}\n");
        }
        await Restore.RunAsync(Path.Combine(wide, "Web", "Web.csproj"));
        return wide;
    }

    private const int ManyFiles = 1000;

    private const int ManyTests = 1000;

    /// <summary>A test project of a thousand tests as written, never restored, in a repository of a commit adding a thousand
    /// files and hundreds after it.</summary>
    private async Task<string> ManyTestsAsync()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-many-"));
        DeleteAtEnd(tests);
        File.WriteAllText(Path.Combine(tests, "Many.Tests.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(tests, "ManyTests.cs"), "using Xunit;\n\nnamespace Many;\n\npublic class ManyTests\n{\n"
            + string.Concat(Enumerable.Range(0, ManyTests).Select(i => $"    [Fact]\n    public void Test_number_{i}_holds_what_it_says() => Assert.True(true);\n\n")) + "}\n");
        await TestGit.RunAsync(tests, "init", "-q", "-b", "main");
        StringBuilder history = new();
        history.Append("commit refs/heads/main\ncommitter Passenger Tests <tests@passenger.invalid> 1700000000 +0000\ndata 10\nMany files\n");
        for (int file = 0; file < ManyFiles; file++)
        {
            history.Append(CultureInfo.InvariantCulture, $"M 644 inline Many/File{file}.txt\ndata 2\n{file % 10}\n");
        }
        for (int commit = 1; commit < 600; commit++)
        {
            string subject = $"Commit {commit} of a long history, its subject saying at some length what it changed";
            history.Append(CultureInfo.InvariantCulture, $"\ncommit refs/heads/main\ncommitter Passenger Tests <tests@passenger.invalid> {1700000000 + commit} +0000\ndata {subject.Length}\n{subject}\n");
        }
        await TestGit.FeedAsync(tests, history.ToString(), "fast-import", "--quiet");
        await TestGit.RunAsync(tests, "reset", "-q", "--hard");
        return tests;
    }

    /// <summary>A tool's answer, held within the limit, a list's words after it where it was cut (none where
    /// <paramref name="whole"/>).</summary>
    private async Task<CallToolResult> Held(McpClient client, string tool, Dictionary<string, object?> arguments, bool whole = false)
    {
        CallToolResult result = await Result(client, tool, arguments);
        string text = Text(result);
        Assert.True(result.IsError != true, $"{tool} failed: {text}{Said()}");
        Assert.True(text.Length <= AgentAnswers.MostCharacters,
            $"{tool} answered {text.Length:N0} characters, past the {AgentAnswers.MostCharacters:N0} an agent's client is to take: {text[..400]}");
        Assert.True(!whole || result.Content.Count == 1, $"{tool} cut an answer that fits: {text[^Math.Min(400, text.Length)..]}");
        return result;
    }

    /// <summary>A list's answer's words after it say how many <paramref name="noun"/>s it left out, which with those listed
    /// make <paramref name="all"/> (where known), and how to read them.</summary>
    private static void LeftOut(CallToolResult held, string noun, int? all, string how)
    {
        Assert.True(held.Content is [TextContentBlock, TextContentBlock], $"A cut list's answer came as {held.Content.Count} parts");
        string words = ((TextContentBlock)held.Content[1]).Text;
        int listed = Json(held).GetArrayLength();
        Assert.True(Regex.IsMatch(words, $@"^[\d,]+ more {noun}s left out: (.+; )?{Regex.Escape(how)}$") && (all is not int whole || listed + Number(words) == whole),
            $"A list of {(all is int known ? known.ToString("N0", CultureInfo.InvariantCulture) : "many")} {noun}s listed {listed}, saying: {words}");
    }

    /// <summary>An answer's JSON, a list's before the words saying what it left out.</summary>
    private static JsonElement Json(CallToolResult result)
    {
        using JsonDocument json = JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text);
        return json.RootElement.Clone();
    }

    /// <summary>The first number words say, its thousands set apart by commas.</summary>
    private static int Number(string words) =>
        int.Parse(Regex.Match(words, @"[\d,]+").Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
}
