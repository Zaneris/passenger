using System.Text.Json.Nodes;

namespace Passenger.App.State;

/// <summary>What a Markdown file's group shows: its editor and its preview, its editor alone, or its preview alone.</summary>
public enum MarkdownView { Split, Editor, Preview }

/// <summary>A Markdown file's layout in every group: what it shows and, showing both, whether the preview stands under the
/// editor (true) or beside it (false) whatever the group's shape, null as the shape stands them.</summary>
public readonly record struct MarkdownLayout(MarkdownView View, bool? Stacked = null);

/// <summary>Which kinds of file show their preview beside their editor, in the store's previews.json, as the person left
/// them, every folder alike: a Markdown file's editor and preview together until another layout is chosen, an SVG's
/// picture on until turned off, a XAML file's and an HTML file's (whose page runs its scripts) off until turned on.</summary>
public sealed record PreviewsState : IStateDocument<PreviewsState>
{
    public MarkdownLayout MarkdownLayout { get; init; }

    public bool Svg { get; init; } = true;

    public bool Html { get; init; }

    public bool Xaml { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, MarkdownLayoutFromItsToggle];

    /// <summary>A layout this build does not have is the editor and preview together.</summary>
    public PreviewsState Clamped() => Enum.IsDefined(MarkdownLayout.View) ? this : this with { MarkdownLayout = default };

    /// <summary>Version 1 to 2: the Markdown preview kept off, before it had layouts, is the editor alone; kept on, the
    /// editor and preview together, the default.</summary>
    private static void MarkdownLayoutFromItsToggle(JsonObject previews)
    {
        if (previews.Remove("markdown", out JsonNode? kept) && kept is JsonValue value && value.TryGetValue(out bool shown) && !shown)
        {
            previews[StateFile.Options.PropertyNamingPolicy!.ConvertName(nameof(MarkdownLayout))] =
                new JsonObject { [StateFile.Options.PropertyNamingPolicy.ConvertName(nameof(MarkdownLayout.View))] = nameof(MarkdownView.Editor) };
        }
    }
}
