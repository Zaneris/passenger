using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>The changes of project.godot a Godot project's page asks for, each a <see cref="GodotPageEdit"/>: its words and
/// its change of the file's text, written through Workspace's <see cref="GodotWrites"/> where Godot's own save puts it.</summary>
public static class GodotPageEdits
{
    /// <summary>An autoload made a global the project's scripts name, or a node alone.</summary>
    public static GodotPageEdit Autoload(GodotPageAutoload autoload, bool global)
    {
        string made = global ? "a global" : "a node";
        return new GodotPageEdit(global ? "Make Autoload Global?" : "Make Autoload a Node?",
            $"Make {autoload.Name} ({autoload.Res}) {made} in {GodotProject.FileName}? {(global ? "Its scripts then name it as a global, as Godot's Autoload settings' Global Variable box does" : "Its scripts then reach it by its node's path alone")}.",
            global ? "Make Global" : "Make Node", $"Make Autoload {autoload.Name} {(global ? "Global" : "a Node")}",
            $"{autoload.Name} was left {(autoload.Global ? "a global" : "a node")}", $"{autoload.Name} is {made} already, or {GodotProject.FileName} has no such autoload",
            $"{autoload.Name} is {made} in {GodotProject.FileName}", text => GodotWrites.WithAutoloadGlobal(text, autoload.Name, global));
    }

    /// <summary>A scene made the main scene, written by its uid where project.godot names the main scene so already.</summary>
    public static GodotPageEdit MainScene(GodotPageScene scene, bool byUid) => new("Change Main Scene?",
        $"Make {scene.Res} the main scene in {GodotProject.FileName}? The game starts there when it runs, as Godot's Run Project does.",
        "Make Main Scene", $"Make {scene.Res} the Main Scene", "The main scene was left as it was", $"{scene.Res} is the main scene already",
        $"{scene.Res} is the main scene in {GodotProject.FileName}", text => GodotWrites.WithMainScene(text, byUid && scene.Uid is { } uid ? uid : scene.Res));

    /// <summary>An input action added, with no events, last in the Input Map.</summary>
    public static GodotPageEdit AddAction(string name) => new("Add Input Action?",
        $"Add the input action {name} to {GodotProject.FileName}? It takes no events until some are added.", "Add Action", $"Add Input Action {name}",
        $"{name} was not added", $"{GodotProject.FileName} has an input action named {name} already", $"{name} is an input action in {GodotProject.FileName}",
        text => GodotWrites.WithAction(text, name))
    {
        Refused = GodotWrites.WhyNotActionName(name),
    };

    /// <summary>An input action taken out of the Input Map with its events.</summary>
    public static GodotPageEdit RemoveAction(GodotPageAction action) => new("Remove Input Action?",
        $"Remove the input action {action.Name} and its {Plural.Of(action.Events.Count, "event")} from {GodotProject.FileName}? Code asking for it then finds no such action.",
        "Remove Action", $"Remove Input Action {action.Name}", $"{action.Name} was kept", $"{GodotProject.FileName} has no input action named {action.Name}",
        $"{action.Name} is no longer an input action in {GodotProject.FileName}", text => GodotWrites.WithoutSetting(text, "input", action.Name));

    /// <summary>A key or mouse button (<see cref="GodotInputEvents.Givable"/>) added last to an action's events.</summary>
    public static GodotPageEdit AddEvent(GodotPageAction action, string given) => new("Add Input Event?",
        $"Add {given} to the input action {action.Name} in {GodotProject.FileName}?", "Add Event", $"Add {given} to {action.Name}",
        $"{given} was not added to {action.Name}", $"{GodotProject.FileName} has no input action named {action.Name}", $"{action.Name} takes {given}",
        text => GodotInputEvents.Written(given) is { } written ? GodotWrites.WithEvent(text, action.Name, written) : null)
    {
        Refused = GodotInputEvents.Written(given) is null ? $"{given} is no key or mouse button the page gives" : null,
    };

    /// <summary>An event of an action, by its place among them, taken out.</summary>
    public static GodotPageEdit RemoveEvent(GodotPageAction action, int index) => new("Remove Input Event?",
        $"Remove {action.Events[index]} from the input action {action.Name} in {GodotProject.FileName}?", "Remove Event",
        $"Remove {action.Events[index]} from {action.Name}", $"{action.Name} keeps {action.Events[index]}",
        $"{GodotProject.FileName} has changed: {action.Name} has no such event now", $"{action.Name} no longer takes {action.Events[index]}",
        text => GodotWrites.WithoutEvent(text, action.Name, index));

    /// <summary>A layer of a kind (<see cref="GodotDefinitions.LayerKinds"/>) named, or its name taken out where none is given.</summary>
    public static GodotPageEdit NameLayer(string kind, int number, string name)
    {
        string layer = $"{GodotDefinitions.LayersShown(kind)} layer {number}";
        string trimmed = name.Trim();
        return new GodotPageEdit(trimmed.Length == 0 ? "Clear Layer Name?" : "Name Layer?",
            trimmed.Length == 0 ? $"Take the name of {layer} out of {GodotProject.FileName}?" : $"Name {layer} {trimmed} in {GodotProject.FileName}?",
            trimmed.Length == 0 ? "Clear Name" : "Name Layer", trimmed.Length == 0 ? $"Clear the Name of {layer}" : $"Name {layer} {trimmed}",
            $"{layer} was left as it was", trimmed.Length == 0 ? $"{layer} has no name" : $"{layer} is named {trimmed} already",
            trimmed.Length == 0 ? $"{layer} has no name in {GodotProject.FileName}" : $"{layer} is named {trimmed} in {GodotProject.FileName}",
            text => GodotWrites.WithLayerName(text, kind, number, trimmed))
        {
            Refused = number < 1 || number > GodotDefinitions.LayerCount(kind) ? $"{GodotDefinitions.LayersShown(kind)} layers count from 1 to {GodotDefinitions.LayerCount(kind)}" : null,
        };
    }
}
