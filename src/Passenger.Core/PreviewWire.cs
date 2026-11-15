using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

// What Passenger and its own previewers (WPF's, MAUI's) say to each other, one definition for every end: the App compiles
// it with Core, each previewer as a linked file. One JSON object a line each way over the previewer's standard input and
// output: Passenger sends the file's text as it stands (a PreviewRequest of the previewer's own) whenever it, the window's
// scale or what the preview is drawn as changes; the previewer answers the newest it has read (PreviewReply, by its
// sequence): a frame, the error that kept the XAML from loading, or why there is nothing to draw.
namespace Passenger.Core;

/// <summary>A XAML file to draw: its text as it stands, the application's definition (App.xaml) whose resources it sees,
/// and the scale the pane's window draws at; what else a previewer is told is its own request's.</summary>
public abstract record PreviewRequest
{
    public required int Sequence { get; init; }

    public required string Path { get; init; }

    public required string Xaml { get; init; }

    public PreviewSource? Application { get; init; }

    public double Scale { get; init; } = 1;
}

/// <summary>Another XAML file the drawing reads (App.xaml): its path, its text, open or on disk, and the URI it has in its
/// project's assembly where the previewer resolves URIs in it against one.</summary>
public sealed record PreviewSource(string Path, string Xaml, string? BaseUri = null);

/// <summary>The previewer's answer to the newest request it read. A frame is a PNG at 96 DPI of <see cref="Scale"/> times
/// the design's size, with the boxes of the file's elements drawn in it; an error says what the XAML did not load for,
/// with its line and column (from 1, as XML readers count the text sent) when it has them; nothing says why there is
/// nothing to draw.</summary>
public sealed record PreviewReply
{
    public required string Kind { get; init; }

    public required int Sequence { get; init; }

    public byte[]? Png { get; init; }

    public double Scale { get; init; } = 1;

    public IReadOnlyList<PreviewBox>? Elements { get; init; }

    public string? Message { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    /// <summary>What the frame was drawn without (App.xaml's resources, a design-time data context that did not load).</summary>
    public IReadOnlyList<PreviewNote>? Notes { get; init; }
}

/// <summary>Something the frame was drawn without and why, with the line of the file's element it is about where it has
/// one (from 1, as XML readers count, in the text sent).</summary>
public sealed record PreviewNote(string Message, int? Line = null);

/// <summary>An element of the file the frame draws: where its start tag is (line and column from 1, as XML readers count,
/// at its name) and its box in the frame, in the design's units (the frame's pixels over its scale).</summary>
public sealed record PreviewBox(int Line, int Column, double X, double Y, double Width, double Height);

public static class PreviewKinds
{
    public const string Frame = "frame";
    public const string Error = "error";
    public const string Nothing = "nothing";
}

/// <summary>The wire's rules: its answers' type, the largest frame drawn, and a previewer's start, which keeps standard
/// output for the protocol alone.</summary>
public static class PreviewWire
{
    /// <summary>The largest side a frame is drawn at, in pixels.</summary>
    public const int Largest = 8192;

    /// <summary>An answer's line as Passenger reads it.</summary>
    public static JsonTypeInfo<PreviewReply> Reply { get; } = PeerJson.Of<PreviewReply>();

    /// <summary>Why a design of this size, in its units, is not drawn at this scale; null for one that is.</summary>
    public static string? TooLarge(double width, double height, double scale) =>
        width * scale > Largest || height * scale > Largest ? $"The design is too large to draw: {width:0} × {height:0}" : null;

    /// <summary>The previewer's start: an exception that ends it said whole on standard error, then in the one line Passenger
    /// shows of a previewer that ended; standard output the protocol's alone, in UTF-8, whatever else writes to the console
    /// (the project's own code included) going to standard error. The protocol's writer and the requests' reader.</summary>
    public static (TextWriter Protocol, TextReader Requests) Start()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.ExceptionObject);
            Console.Error.WriteLine(e.ExceptionObject is Exception error ? $"The previewer failed: {error.GetType().Name}: {error.Message}" : "The previewer failed");
        };
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        TextWriter protocol = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        TextReader requests = new StreamReader(Console.OpenStandardInput(), utf8);
        Console.SetOut(Console.Error);
        return (protocol, requests);
    }

    /// <summary>An answer written as its line, whole, whichever thread answers.</summary>
    public static void Answer(TextWriter protocol, PreviewReply reply)
    {
        string line = JsonSerializer.Serialize(reply, Reply);
        lock (protocol)
        {
            protocol.WriteLine(line);
        }
    }
}
