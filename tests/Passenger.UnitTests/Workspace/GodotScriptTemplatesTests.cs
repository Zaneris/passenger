using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Passenger's C# script templates for Godot's Attach Script dialog: shaped by the project's .editorconfig, root
/// namespace and C# version (file-scoped or braced namespaces, braces on their own line or not, spaces Godot is told to
/// keep or tabs it indents with), a node's with Godot's lifecycle methods and a resource's empty, each marked as Passenger's.</summary>
public class GodotScriptTemplatesTests
{
    private static GodotScriptStyle Style(Dictionary<string, string> settings, string? space = "Spin.Game", bool fileScoped = true) =>
        GodotScriptTemplates.StyleOf(name => settings.GetValueOrDefault(name), space, fileScoped);

    [Fact]
    public void A_project_of_file_scoped_namespaces_and_four_spaces_gets_a_partial_class_godot_keeps_the_spaces_of()
    {
        string text = GodotScriptTemplates.Text("Node2D", Style(new() { ["csharp_style_namespace_declarations"] = "file_scoped:warning", ["indent_size"] = "4" }));
        Assert.Equal("""
            // meta-name: Passenger (this project's style)
            // meta-description: A Node2D script in the project's namespace and style, written by Passenger
            // meta-space-indent: 0
            using _BINDINGS_NAMESPACE_;

            namespace Spin.Game;

            public partial class _CLASS_ : _BASE_
            {
                public override void _Ready()
                {
                }

                public override void _Process(double delta)
                {
                }
            }

            """.ReplaceLineEndings("\n"), text);
        Assert.True(GodotScriptTemplates.IsPassengers(text), "The template does not say it is Passenger's");
    }

    [Fact]
    public void Braces_at_the_lines_end_a_namespace_in_braces_and_tabs_are_written_as_the_project_says()
    {
        GodotScriptStyle style = Style(new() { ["csharp_style_namespace_declarations"] = "block_scoped", ["csharp_new_line_before_open_brace"] = "none", ["indent_style"] = "tab" });
        Assert.Equal("// meta-name: Passenger (this project's style)\n// meta-description: A Resource script in the project's namespace and style, written by Passenger\n"
            + "using _BINDINGS_NAMESPACE_;\n\nnamespace Spin.Game {\n\tpublic partial class _CLASS_ : _BASE_ {\n\t}\n}\n", GodotScriptTemplates.Text("Resource", style));
    }

    [Fact]
    public void A_csharp_before_10_or_no_root_namespace_takes_a_braced_namespace_or_none()
    {
        Assert.False(Style([], fileScoped: false).FileScoped);
        Assert.DoesNotContain(GodotScriptTemplates.Text("Node", Style([], space: null)).Split('\n'), l => l.StartsWith("namespace", StringComparison.Ordinal));
        Assert.Equal(Path.Combine("game", "script_templates", "Control", "Passenger.cs"), GodotScriptTemplates.PathOf("game", "Control"));
    }

}
