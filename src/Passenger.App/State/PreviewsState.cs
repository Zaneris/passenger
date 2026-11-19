using System.Text.Json.Nodes;

namespace Passenger.App.State;

public enum MarkdownView { Split, Editor, Preview }

public readonly record struct MarkdownLayout(MarkdownView View, bool? Stacked = null);

/// <summary>Which kinds of file show their preview beside their editor, in the store's previews.json, as the person left
/// them, every folder alike: a Markdown file's and an SVG's on until turned off, a XAML file's and an HTML file's (whose
/// page runs its scripts) off until turned on.</summary>
public sealed record PreviewsState : IStateDocument<PreviewsState>
{
    public MarkdownLayout MarkdownLayout { get; init; }

    public bool Svg { get; init; } = true;

    public bool Html { get; init; }

    public bool Xaml { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [
        StateStore.Unversioned,
        document =>
        {
            if (document.Remove("markdown", out JsonNode? previous) && previous is JsonValue value &&
                value.TryGetValue(out bool shown) && !shown)
            {
                document["markdownLayout"] = new JsonObject { ["view"] = nameof(MarkdownView.Editor) };
            }
        },
    ];

    public PreviewsState Clamped() => Enum.IsDefined(MarkdownLayout.View) ? this : this with { MarkdownLayout = default };
}
