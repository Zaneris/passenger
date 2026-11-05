using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a Godot project's files say written as Godot's editor writes it: a setting put last in its section of
/// project.godot, or in a section made in its place among the others; a group given to a node in its scene's header.</summary>
public class GodotWritesTests
{
    private const string Project = "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\n\n[dotnet]\n\nproject/assembly_name=\"Spinner\"\n";

    [Fact]
    public void A_setting_goes_last_in_its_section_or_in_a_section_made_where_godot_sorts_it()
    {
        string action = GodotWrites.WithSetting(Project, "input", "jump", GodotWrites.ActionValue("\n"))!;
        Assert.Equal(Project + "\n[input]\n\njump={\n\"deadzone\": 0.2,\n\"events\": []\n}\n", action);
        string autoload = GodotWrites.WithSetting(action, "autoload", "Globals", GodotWrites.AutoloadValue("res://Globals.cs"))!;
        Assert.Contains("config/name=\"Spinner\"\n\n[autoload]\n\nGlobals=\"*res://Globals.cs\"\n\n[dotnet]", autoload, StringComparison.Ordinal);
        string second = GodotWrites.WithSetting(autoload, "input", "fire", GodotWrites.ActionValue("\n"))!;
        Assert.EndsWith("\"events\": []\n}\nfire={\n\"deadzone\": 0.2,\n\"events\": []\n}\n", second, StringComparison.Ordinal);
        Assert.Null(GodotWrites.WithSetting(second, "input", "jump", "{}"));
    }

    [Fact]
    public void A_setting_keeps_the_files_line_breaks()
    {
        string crlf = Project.Replace("\n", "\r\n", StringComparison.Ordinal);
        string made = GodotWrites.WithSetting(crlf, "input", "jump", GodotWrites.ActionValue(GodotWrites.NewlineOf(crlf)))!;
        Assert.DoesNotContain("\n", made.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_joins_a_nodes_groups_or_goes_before_the_scene_it_instances()
    {
        string scene = "[gd_scene format=3]\n\n[node name=\"Main\" type=\"Node2D\"]\n\n[node name=\"Enemy\" type=\"Area2D\" parent=\".\" groups=[\"hittable\"]]\n\n"
            + "[node name=\"Player\" parent=\".\" instance=ExtResource(\"1\")]\n\n[node name=\"Empty\" parent=\".\" groups=[]]\n";
        GodotFile file = GodotFile.Parse(scene);
        int Start(string name) => file.Nodes.First(n => n.Name == name).Section.Start;
        Assert.Contains("[node name=\"Main\" type=\"Node2D\" groups=[\"enemies\"]]\n", GodotWrites.WithGroup(scene, Start("Main"), "enemies"), StringComparison.Ordinal);
        Assert.Contains("groups=[\"hittable\", \"enemies\"]]", GodotWrites.WithGroup(scene, Start("Enemy"), "enemies"), StringComparison.Ordinal);
        Assert.Contains("[node name=\"Player\" parent=\".\" groups=[\"enemies\"] instance=ExtResource(\"1\")]", GodotWrites.WithGroup(scene, Start("Player"), "enemies"), StringComparison.Ordinal);
        Assert.Contains("[node name=\"Empty\" parent=\".\" groups=[\"enemies\"]]", GodotWrites.WithGroup(scene, Start("Empty"), "enemies"), StringComparison.Ordinal);
        Assert.Null(GodotWrites.WithGroup(scene, Start("Enemy"), "hittable"));
        Assert.Equal("[node name=\"Main\" type=\"Node2D\" groups=[\"say \\\"hi\\\"\"]]", GodotWrites.GroupLine(scene, Start("Main"), "say \"hi\""));
    }

    [Fact]
    public void A_function_is_made_last_in_a_gdscript_two_blank_lines_after_the_last_and_indented_as_the_script_is()
    {
        Assert.Equal("extends Node\n\nfunc _ready() -> void:\n    pass\n\n\nfunc _on_hit(body: Node2D) -> void:\n    pass\n",
            GodotWrites.WithFunction("extends Node\n\nfunc _ready() -> void:\n    pass\n\n", "_on_hit", ["body: Node2D"]));
        Assert.Equal("extends Node\r\n\r\n\r\nfunc go() -> void:\r\n\tpass\r\n", GodotWrites.WithFunction("extends Node\r\n", "go", []));
    }
}

