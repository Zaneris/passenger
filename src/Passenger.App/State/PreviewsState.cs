using System.Text.Json.Nodes;

namespace Passenger.App.State;

/// <summary>What a group shows of a file with a pane of its own (a Markdown file's preview, a form's design): its editor and
/// the pane, its editor alone, the pane alone or (Markdown) its inline editor.</summary>
public enum PaneView { Split, Editor, Preview, Inline }

/// <summary>A kind of file's layout in every group: what it shows and, showing both, whether the pane stands under the
/// editor (true) or beside it (false) whatever the group's shape, null as the shape stands them.</summary>
public readonly record struct PaneLayout(PaneView View, bool? Stacked = null);

/// <summary>Which kinds of file show their preview beside their editor, in the store's previews.json, as the person left
/// them, every folder alike: a Markdown file's editor and preview together until another layout is chosen, a Windows Forms
/// form's design alone on Windows (its code alone elsewhere, where nothing draws it), an SVG's picture on until turned off,
/// a XAML file's and an HTML file's (whose page runs its scripts) off until turned on.</summary>
public sealed record PreviewsState : IStateDocument<PreviewsState>
{
    public PaneLayout MarkdownLayout { get; init; }

    /// <summary>A form's layout, null for none chosen yet (<see cref="FormLayoutOrDefault"/>).</summary>
    public PaneLayout? FormLayout { get; init; }

    /// <summary>The form's layout chosen, else its design alone where Windows Forms draws and its code alone elsewhere.</summary>
    public PaneLayout FormLayoutOrDefault => FormLayout ?? new PaneLayout(OperatingSystem.IsWindows() ? PaneView.Preview : PaneView.Editor);

    public bool Svg { get; init; } = true;

    public bool Html { get; init; }

    public bool Xaml { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, MarkdownLayoutFromItsToggle];

    /// <summary>A layout this build does not have is the editor and preview together, a form's its default.</summary>
    public PreviewsState Clamped() => this with
    {
        MarkdownLayout = Enum.IsDefined(MarkdownLayout.View) ? MarkdownLayout : default,
        FormLayout = FormLayout is { } form && (!Enum.IsDefined(form.View) || form.View == PaneView.Inline) ? null : FormLayout,
    };

    /// <summary>Version 1 to 2: the Markdown preview kept off, before it had layouts, is the editor alone; kept on, the
    /// editor and preview together, the default.</summary>
    private static void MarkdownLayoutFromItsToggle(JsonObject previews)
    {
        if (previews.Remove("markdown", out JsonNode? kept) && kept is JsonValue value && value.TryGetValue(out bool shown) && !shown)
        {
            previews[StateFile.Options.PropertyNamingPolicy!.ConvertName(nameof(MarkdownLayout))] =
                new JsonObject { [StateFile.Options.PropertyNamingPolicy.ConvertName(nameof(MarkdownLayout.View))] = nameof(PaneView.Editor) };
        }
    }
}
