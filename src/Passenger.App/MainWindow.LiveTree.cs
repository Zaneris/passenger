using Passenger.App.LiveTree;

namespace Passenger.App;

/// <summary>The window's Live Visual Tree (<see cref="LiveTreeController"/>), over its session.</summary>
public partial class MainWindow
{
    private LiveTreeController? liveTree;

    /// <summary>The Live Visual Tree tool window.</summary>
    public LiveTreeView LiveTree => liveTree!.View;

    /// <summary>The Avalonia or WPF app the Live Visual Tree follows, or null.</summary>
    public LiveApp? LiveApp => liveTree!.App as LiveApp;

    /// <summary>What the Live Visual Tree follows: an Avalonia or WPF app, or a Godot game; null for nothing.</summary>
    public ILiveTreeSource? LiveSource => liveTree!.App;

    /// <summary>The inspector in the debugged app told of the property breakpoints as they are now: what an agent's
    /// breakpoint_set waits for.</summary>
    internal Task PropertyWatches => liveTree!.Watching;

    /// <summary>A debugged app's binding values in its .axaml files.</summary>
    private BindingValuesController? bindingValues;

    /// <summary>The binding values after the .axaml lines shown, which an agent's debug_state reads as the editor shows them.</summary>
    internal BindingValuesController BindingValues => bindingValues!;

    /// <summary>Avalonia's renderer debug overlays in the apps Run, Debug and Profile launch.</summary>
    private OverlaysCommands? overlays;

    private void AddLiveTree()
    {
        liveTree = new LiveTreeController(new LiveTreeParts(Dock, actions!, text => status.Text = text, NavigateTo, Editors));
        bindingValues = new BindingValuesController(Editors, liveTree);
        overlays = new OverlaysCommands(new OverlaysParts(Bottom, actions!, text => status.Text = text));
        overlays.Bind(session);
        gameAudio = new Godot.GameAudioCommands(new Godot.GameAudioParts(Bottom.Debug.GameAudio, actions!, text => status.Text = text, () => AgentsOwn));
        gameAudio.Bind(session);
    }

    /// <summary>The audio of the Godot game a Debug tab debugs, muted from the Debug window.</summary>
    private Godot.GameAudioCommands? gameAudio;
}
