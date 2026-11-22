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

    /// <summary>A script's functions are found by their names, and a module's path by the projects serving
    /// it.</summary>
    [Fact]
    public void A_scripts_functions_are_found_by_their_names() => Cases.All(
        (nameof(A_function_declaration_is_found_by_its_name_and_its_calls_and_comments_are_not),
            A_function_declaration_is_found_by_its_name_and_its_calls_and_comments_are_not),
        (nameof(An_arrow_function_assigned_a_class_and_a_method_are_each_found), An_arrow_function_assigned_a_class_and_a_method_are_each_found),
        (nameof(A_component_and_its_code_behind_share_one_collocated_script_and_a_minified_copy_is_no_script_to_read),
            A_component_and_its_code_behind_share_one_collocated_script_and_a_minified_copy_is_no_script_to_read));

    private void A_function_declaration_is_found_by_its_name_and_its_calls_and_comments_are_not()
    {
        Assert.Equal(["export function greet(name) {"], Lines(Script, "greet"));
    }

    private void An_arrow_function_assigned_a_class_and_a_method_are_each_found()
    {
        Assert.Equal(["window.focusOn = (id) => document.getElementById(id).focus();"], Lines(Script, "focusOn"));
        Assert.Equal(["const sum = async (a, b) => a + b;"], Lines(Script, "sum"));
        Assert.Equal(["export class Map {"], Lines(Script, "Map"));
        Assert.Equal(["draw(at) {"], Lines(Script, "draw"));
    }

    private void A_component_and_its_code_behind_share_one_collocated_script_and_a_minified_copy_is_no_script_to_read()
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
