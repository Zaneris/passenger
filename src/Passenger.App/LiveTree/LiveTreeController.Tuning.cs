using Avalonia.Controls;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App.LiveTree;

/// <summary>A running Godot game's values tuned from the properties pane, and kept: Keep writes a value where it comes from
/// (<see cref="GodotKeep"/>), shown before it writes and one command of Undo; Keep All a node's; and the values a game that
/// ended changed and did not keep listed over the tree until each is kept or the lot discarded.</summary>
public sealed partial class LiveTreeController : IGodotTuner
{
    /// <summary>What lists the values a game that ended left unkept, while it does.</summary>
    private GodotUnkeptPanel? unkept;

    /// <summary>The game whose unkept values are listed.</summary>
    private GodotGame? unkeptOf;

    /// <summary>The list of values a game that ended left unkept, while it shows.</summary>
    public GodotUnkeptPanel? Unkept => unkept;

    public void Settled(long element)
    {
        if (app is GodotGame { State: LiveAppState.Connected } && details.Details?.Id == element)
        {
            IdeErrors.Observe(ShowDetailsAsync(element));
        }
    }

    public void Keep(GodotTuned tuned, Control from)
    {
        if (TuningOf(tuned) is { } tuning)
        {
            IdeErrors.Observe(KeepAsync($"Keep {tuned.NodeName}.{tuned.Name}", tuning, [tuned], from));
        }
    }

    /// <summary>A popup beside what opened it, in the theme's frame of a popup, as wide as the popup asks (its own width, else
    /// what it measures, and its frame's edges), so it is kept inside the window.</summary>
    public void ShowPopup(Control popup, Control from)
    {
        double width = popup.Width;
        if (double.IsNaN(width))
        {
            popup.Measure(Avalonia.Size.Infinity);
            width = popup.DesiredSize.Width + 4;
        }
        UserControl frame = new() { Width = width, Content = new Border { Classes = { StyleClasses.Popup }, Child = popup } };
        popup.Width = double.NaN;
        parts.ShowPopup(frame, from);
    }

    public void ClosePopup() => parts.ClosePopup();

    public void Say(string words) => parts.Say(words);

    /// <summary>The tuning a value set belongs to: the game followed's, or the one whose unkept values are listed.</summary>
    private GodotTuning? TuningOf(GodotTuned tuned) =>
        app is GodotGame game && game.Tuning.Of(tuned.Node, tuned.Name) == tuned ? game.Tuning
        : unkeptOf is { } ended && ended.Tuning.Of(tuned.Node, tuned.Name) == tuned ? ended.Tuning
        : null;

    /// <summary>The followed game's values set or read back: the rows show them where they stand, the chosen node's Keep
    /// All its count, and the list of a game that ended what is left to keep.</summary>
    private void OnTuning(object? sender, EventArgs e)
    {
        view?.RefreshCells();
        ShowKeepAll();
    }

    /// <summary>Keep All after the chosen node's heading while it has values changed and not kept.</summary>
    private void ShowKeepAll()
    {
        if (view is null)
        {
            return;
        }
        if (app is GodotGame game && details.Details is { } shown && game.Tuning.UnkeptOf((ulong)shown.Id) is { Count: > 0 } values)
        {
            view.ShowHeadingLink($"Keep All ({values.Count})…", () =>
            {
                if (view.HeadingLink is { } link)
                {
                    IdeErrors.Observe(KeepAsync($"Keep All of {values[0].NodeName}", game.Tuning, game.Tuning.UnkeptOf((ulong)shown.Id), link));
                }
            });
        }
        else
        {
            view.ShowHeadingLink(null, null);
        }
    }

    /// <summary>A game's values left unkept as it ends, listed over the tree with Keep, Keep All and Discard, the window
    /// shown and the status bar saying so; nothing for a game that left none.</summary>
    private void OfferUnkept(GodotGame game)
    {
        if (game.State != LiveAppState.Ended || game.Tuning.Unkept is not { Count: > 0 } values || unkeptOf == game)
        {
            return;
        }
        if (unkeptOf is { } earlier)
        {
            earlier.Tuning.Changed -= OnUnkeptChanged;
        }
        unkeptOf = game;
        game.Tuning.Changed += OnUnkeptChanged;
        unkept = new GodotUnkeptPanel(game.Name, Keep,
            from => IdeErrors.Observe(KeepAsync($"Keep All of {game.Name}", game.Tuning, game.Tuning.Unkept, from)),
            () => game.Tuning.Discard());
        unkept.Show(values);
        view?.ShowAside(unkept);
        parts.Dock.Show(window.Id);
        parts.Say($"{game.Name} ended with {Plural.Of(values.Count, "value")} changed and not kept: the Live Visual Tree lists them to keep");
    }

    private void OnUnkeptChanged(object? sender, EventArgs e)
    {
        if (unkeptOf is not { } game || unkept is null)
        {
            return;
        }
        if (game.Tuning.Unkept is { Count: > 0 } left)
        {
            unkept.Show(left);
            return;
        }
        game.Tuning.Changed -= OnUnkeptChanged;
        unkeptOf = null;
        unkept = null;
        view?.ShowAside(null);
    }

    /// <summary>Values kept where each comes from: what each write will be is shown first, beside what asked, with why any
    /// cannot be kept; Keep writes them as one command of Undo, and once written each is the value its changes are marked
    /// against. The status bar says what came of it.</summary>
    private async Task KeepAsync(string title, GodotTuning tuning, IReadOnlyList<GodotTuned> values, Control from)
    {
        UiThread.Check();
        List<GodotKept> plans = [];
        List<(GodotTuned Tuned, GodotKept Kept)> made = [];
        List<string> refused = [];
        foreach (GodotTuned tuned in values)
        {
            (GodotKept? kept, string? whyNot) = GodotKeep.Plan(tuned.Place, tuned.Name, tuned.Value, tuned.Property, res => Read(tuning, res), parts.Editors.ParseOptions);
            if (kept is not null)
            {
                plans.Add(kept);
                made.Add((tuned, kept));
            }
            else
            {
                refused.Add($"{tuned.NodeName}.{tuned.Name} is not kept: {whyNot}");
            }
        }
        if (plans.Count == 0 && refused.Count == 1)
        {
            parts.Say(refused[0]);
            return;
        }
        GodotKeepPreview preview = new(title, plans, refused);
        TaskCompletionSource<bool> answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        preview.Done += (_, keep) =>
        {
            parts.ClosePopup();
            answered.TrySetResult(keep);
        };
        ShowPopup(preview, from);
        Previewing = preview;
        bool chosen;
        try
        {
            chosen = await answered.Task;
        }
        finally
        {
            Previewing = null;
        }
        if (!chosen)
        {
            return;
        }
        List<XamlFileChange> changes = [.. plans.GroupBy(p => p.File, PathNames.Comparer).Select(file =>
        {
            string was = file.First().Was;
            IReadOnlyList<TextEdit> edits = [.. file.SelectMany(p => p.Edits).OrderBy(e => e.Start)];
            return new XamlFileChange(file.Key, now => now == was ? edits : []);
        })];
        if (await parts.Change(title, changes) is { } why)
        {
            parts.Say($"{title}: {why}");
            return;
        }
        foreach ((GodotTuned tuned, _) in made)
        {
            tuning.Kept(tuned);
        }
        parts.Say(made.Count == 1
            ? $"Kept {made[0].Tuned.Name} in {Path.GetFileName(made[0].Kept.File)}:{made[0].Kept.Line + 1}"
            : $"Kept {Plural.Of(made.Count, "value")} in {Plural.Listed([.. made.Select(m => Path.GetFileName(m.Kept.File)).Distinct()])}");
    }

    /// <summary>What Keep is showing before it writes, while it is.</summary>
    public GodotKeepPreview? Previewing { get; private set; }

    /// <summary>A <c>res://</c> path's file in the game's project, and its text as the window has it.</summary>
    private (string File, string Text)? Read(GodotTuning tuning, string res)
    {
        if (!res.StartsWith("res://", StringComparison.Ordinal))
        {
            return null;
        }
        string path = PathNames.Canonical(Path.Combine(tuning.Folder, res["res://".Length..]));
        return parts.Editors.TextOf(path) is { } text ? (path, text.ToString()) : null;
    }
}
