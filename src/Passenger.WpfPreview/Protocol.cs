using Passenger.Core;

// What Passenger asks its WPF previewer, one definition for both ends (the app compiles this file too); the rest of the
// wire, the answers and how a line is read, is every previewer's (PreviewWire).
namespace Passenger.WpfPreview;

/// <summary>A XAML file to draw (<see cref="PreviewRequest"/>), with the pack URI it has in its project's assembly: what
/// relative URIs in it resolve against, as they would in the build; App.xaml comes with its own.</summary>
public sealed record WpfPreviewRequest : PreviewRequest
{
    public required string BaseUri { get; init; }
}
