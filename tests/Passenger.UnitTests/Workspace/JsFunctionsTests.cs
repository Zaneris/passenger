using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Where JavaScript a JS interop call names is declared, so Go to Declaration from the call lands on the function: each
/// way a script declares one (a declaration, a class, a function or arrow function assigned or given as a member, a method),
/// never a call of it or a mention in a comment; and the files a module's path names under the projects that serve it.</summary>
public class JsFunctionsTests
{
    private const string Script = """
        // greet("Ada") says hello; function greet(x) { } in a comment declares nothing.
        export function greet(name) {
            return "Hello, " + name;
        }

        window.app = {
            shout: function (text) { return text.toUpperCase(); },
            "whisper": (text) => text.toLowerCase(),
            later: async x => x,
        };

        window.focusOn = (id) => document.getElementById(id).focus();
        const sum = async (a, b) => a + b;

        export class Map {
            draw(at) {
                greet("map");
            }
        }

        const url = "http://example.com/greet";
        /* function shout() { } */
        """;

    [Fact]
    public void A_function_declaration_is_found_by_its_name_and_its_calls_and_comments_are_not()
    {
        Assert.Equal(["export function greet(name) {"], Lines(Script, "greet"));
    }

    [Fact]
    public void A_dotted_identifier_names_its_last_part_declared_as_an_objects_member_in_each_way()
    {
        Assert.Equal(["shout: function (text) { return text.toUpperCase(); },"], Lines(Script, "app.shout"));
        Assert.Equal(["\"whisper\": (text) => text.toLowerCase(),"], Lines(Script, "app.whisper"));
        Assert.Equal(["later: async x => x,"], Lines(Script, "app.later"));
    }

    [Fact]
    public void An_arrow_function_assigned_a_class_and_a_method_are_each_found()
    {
        Assert.Equal(["window.focusOn = (id) => document.getElementById(id).focus();"], Lines(Script, "focusOn"));
        Assert.Equal(["const sum = async (a, b) => a + b;"], Lines(Script, "sum"));
        Assert.Equal(["export class Map {"], Lines(Script, "Map"));
        Assert.Equal(["draw(at) {"], Lines(Script, "draw"));
    }

    [Fact]
    public void A_name_the_script_never_declares_is_found_nowhere()
    {
        Assert.Empty(JsFunctions.Declared(Script, "missing"));
        Assert.Empty(JsFunctions.Declared(Script, "url"));
    }

    [Fact]
    public void A_modules_path_names_the_web_root_and_the_folder_of_each_project_and_content_paths_the_named_projects_web_root()
    {
        string app = Path.Combine("work", "Shop", "Shop.csproj");
        string client = Path.Combine("work", "Shop.Client", "Shop.Client.csproj");
        Assert.Equal(
            [Path.Combine("work", "Shop", "wwwroot", "Components", "Pages", "Map.razor.js"), Path.Combine("work", "Shop", "Components", "Pages", "Map.razor.js"),
                Path.Combine("work", "Shop.Client", "wwwroot", "Components", "Pages", "Map.razor.js"), Path.Combine("work", "Shop.Client", "Components", "Pages", "Map.razor.js")],
            JsFunctions.ModuleFiles("./Components/Pages/Map.razor.js?v=2", [app, client]));
        Assert.Equal([Path.Combine("work", "Shop.Client", "wwwroot", "js", "lib.js")], JsFunctions.ModuleFiles("./_content/Shop.Client/js/lib.js", [app, client]));
        Assert.Empty(JsFunctions.ModuleFiles("https://cdn.example.com/lib.js", [app]));
    }

    [Fact]
    public void A_component_and_its_code_behind_share_one_collocated_script_and_a_minified_copy_is_no_script_to_read()
    {
        Assert.Equal(Path.Combine("Pages", "Map.razor.js"), JsFunctions.CollocatedOf(Path.Combine("Pages", "Map.razor")));
        Assert.Equal(Path.Combine("Pages", "Map.razor.js"), JsFunctions.CollocatedOf(Path.Combine("Pages", "Map.razor.cs")));
        Assert.Equal(Path.Combine("Pages", "Index.cshtml.js"), JsFunctions.CollocatedOf(Path.Combine("Pages", "Index.cshtml")));
        Assert.Null(JsFunctions.CollocatedOf("Program.cs"));
        Assert.True(JsFunctions.IsScript("site.js"));
        Assert.True(JsFunctions.IsScript("module.mjs"));
        Assert.False(JsFunctions.IsScript("bootstrap.bundle.min.js"));
        Assert.False(JsFunctions.IsScript("site.css"));
    }

    /// <summary>The trimmed lines of a script where an identifier is declared.</summary>
    private static string[] Lines(string script, string identifier) =>
        [.. JsFunctions.Declared(script, identifier).Select(d =>
        {
            int start = script.LastIndexOf('\n', Math.Max(0, d.Start - 1)) + 1;
            int end = script.IndexOf('\n', d.Start);
            return script[start..(end < 0 ? script.Length : end)].Trim();
        })];
}
