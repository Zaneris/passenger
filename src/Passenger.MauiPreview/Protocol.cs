using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

// What Passenger and its MAUI previewer say to each other, one definition for both ends (the app compiles this file
// too): one JSON object a line each way over the previewer's standard input and output. Passenger sends the file's text
// as it stands (MauiPreviewRequest) whenever it, the window's scale or what the preview is drawn as changes; the
// previewer answers the newest it has read (MauiPreviewReply, by its sequence): a frame, the error that kept the XAML
// from loading, or why there is nothing to draw.
namespace Passenger.MauiPreview;

/// <summary>A XAML file to draw: its text as it stands, the application's definition (App.xaml) whose resources it sees,
/// the scale the pane's window draws at, the size of the screen it is drawn on in device-independent units, the
/// platform and idiom OnPlatform and OnIdiom read (as MAUI names them: Android, iOS, MacCatalyst, WinUI; Phone, Tablet,
/// Desktop), and the theme AppThemeBinding reads (Light, Dark).</summary>
public sealed record MauiPreviewRequest
{
    public required int Sequence { get; init; }

    public required string Path { get; init; }

    public required string Xaml { get; init; }

    public MauiPreviewSource? Application { get; init; }

    public double Scale { get; init; } = 1;

    public double Width { get; init; } = 390;

    public double Height { get; init; } = 844;

    public string Platform { get; init; } = MauiPreviewDevices.Android;

    public string Idiom { get; init; } = MauiPreviewDevices.Phone;

    public string Theme { get; init; } = MauiPreviewThemes.Light;
}

/// <summary>Another XAML file the drawing reads (App.xaml): its path and its text, open or on disk.</summary>
public sealed record MauiPreviewSource(string Path, string Xaml);

/// <summary>The previewer's answer to the newest request it read. A frame is a PNG at 96 DPI of <see cref="Scale"/>
/// times the screen's size, with the boxes of the file's elements drawn in it; an error says what the XAML did not load
/// for, with its line and column (from 1, as XML readers count the text sent) when it has them; nothing says why there
/// is nothing to draw.</summary>
public sealed record MauiPreviewReply
{
    public required string Kind { get; init; }

    public required int Sequence { get; init; }

    public byte[]? Png { get; init; }

    public double Scale { get; init; } = 1;

    public IReadOnlyList<MauiPreviewBox>? Elements { get; init; }

    public string? Message { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    /// <summary>What the frame was drawn without (App.xaml's resources).</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>An element of the file the frame draws: where its start tag is (line and column from 1, as XML readers count,
/// at its name) and its box in the frame, in device-independent units (the frame's pixels over its scale).</summary>
public sealed record MauiPreviewBox(int Line, int Column, double X, double Y, double Width, double Height);

public static class MauiPreviewKinds
{
    public const string Frame = "frame";
    public const string Error = "error";
    public const string Nothing = "nothing";
}

/// <summary>The platforms and idioms a preview is drawn as, by the names MAUI's DevicePlatform and DeviceIdiom give them.</summary>
public static class MauiPreviewDevices
{
    public const string Android = "Android";
    public const string IOS = "iOS";
    public const string MacCatalyst = "MacCatalyst";
    public const string Windows = "WinUI";

    public const string Phone = "Phone";
    public const string Tablet = "Tablet";
    public const string Desktop = "Desktop";
}

public static class MauiPreviewThemes
{
    public const string Light = "Light";
    public const string Dark = "Dark";
}

public static class MauiPreviewJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Named, as the serializer names it by default, so each message's metadata can be had before the first message.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>A request's line as the previewer reads it.</summary>
    public static JsonTypeInfo<MauiPreviewRequest> Request { get; } = (JsonTypeInfo<MauiPreviewRequest>)Options.GetTypeInfo(typeof(MauiPreviewRequest));

    /// <summary>An answer's line as Passenger reads it.</summary>
    public static JsonTypeInfo<MauiPreviewReply> Reply { get; } = (JsonTypeInfo<MauiPreviewReply>)Options.GetTypeInfo(typeof(MauiPreviewReply));
}
