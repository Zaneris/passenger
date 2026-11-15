using System.Text.Json;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>A .NET frame of a stop in the browser, as .NET's debug proxy gives it: its id for the proxy, its method, its
/// file and its place there (lines and columns from zero), and the object its locals are read from.</summary>
public sealed record WebAssemblyFrame(string Id, string Method, string? Path, int Line, int Column, string? Locals);

/// <summary>What .NET's browser debug proxy says of a stop and a value, read as the Debug window shows it: the frames of the
/// program's .NET code (the browser's own, of its WebAssembly and JavaScript, passed over), a value's words and C# type,
/// and what a breakpoint is asked for by.</summary>
public static class WebAssemblyValues
{
    /// <summary>How the proxy's ids of a .NET frame, scope, object or method begin.</summary>
    private const string DotnetId = "dotnet:";

    /// <summary>A stop's .NET frames, the innermost first.</summary>
    public static IReadOnlyList<WebAssemblyFrame> Frames(JsonElement paused) =>
        [.. JsonMembers.Items(paused, "callFrames").Where(f => JsonMembers.Text(f, "callFrameId")?.StartsWith(DotnetId, StringComparison.Ordinal) == true)
            .Select(f => new WebAssemblyFrame(JsonMembers.Text(f, "callFrameId")!, JsonMembers.Text(f, "functionName") ?? string.Empty,
                PathOf(JsonMembers.Text(f, "url")), JsonMembers.Number(JsonMembers.Object(f, "location"), "lineNumber") ?? 0,
                JsonMembers.Number(JsonMembers.Object(f, "location"), "columnNumber") ?? 0,
                JsonMembers.Items(f, "scopeChain").Select(s => JsonMembers.Text(JsonMembers.Object(s, "object"), "objectId")).FirstOrDefault(id => id is not null)))];

    /// <summary>The file a proxy's URL of a source names, canonical; null for one of no file (a <c>dotnet://</c> source
    /// the machine does not have).</summary>
    public static string? PathOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? address) && address.IsFile ? PathNames.Canonical(address.LocalPath) : null;

    /// <summary>The pattern a breakpoint in a file is asked for by: the file's path as its program's symbols name it, matched
    /// whole, its separators either way and its letter case as the file system tells it apart.</summary>
    public static string PatternOf(string path, bool ignoreCase)
    {
        string escaped = string.Join(@"[\\/]", path.Split('/', '\\').Select(Regex.Escape));
        return $"{(ignoreCase ? "(?i)" : string.Empty)}^{escaped}$";
    }

    /// <summary>A remote value as the Debug window shows it: a string quoted as C# writes it, a number or truth value as it
    /// reads, null as null, any other object by what the proxy says of it.</summary>
    public static string Shown(JsonElement value)
    {
        string? type = JsonMembers.Text(value, "type");
        JsonElement? raw = JsonMembers.Value(value, "value");
        return type switch
        {
            "string" => raw is { ValueKind: JsonValueKind.String } text ? Quoted(text.GetString()!) : JsonMembers.Text(value, "description") ?? "null",
            "undefined" => "null",
            _ when JsonMembers.Text(value, "subtype") == "null" => "null",
            "object" when raw is { ValueKind: JsonValueKind.Null } && JsonMembers.Text(value, "objectId") is null => "null",
            _ => JsonMembers.Text(value, "description") ?? raw?.GetRawText() ?? string.Empty,
        };
    }

    /// <summary>A remote value read for a tracepoint's expression, as every debugger tells one to its specifiers
    /// (<see cref="DisplayFormat.Shown"/>): a string with its own text, a number holding a whole one with its value and the
    /// bytes its C# type holds it in (an int's 4 where the proxy names no type and it fits one, else a long's 8), anything
    /// else as the Debug window shows it.</summary>
    public static DisplayValue Displayed(JsonElement value)
    {
        JsonElement? raw = JsonMembers.Value(value, "value");
        return JsonMembers.Text(value, "type") switch
        {
            "string" when raw is { ValueKind: JsonValueKind.String } text => DisplayValue.OfText(text.GetString()!, Shown(value)),
            "number" when raw is { ValueKind: JsonValueKind.Number } number && WholeOf(number) is { } whole && BytesOf(value, whole) is { } bytes =>
                DisplayValue.OfWhole(whole, bytes),
            _ => DisplayValue.Said(Shown(value)),
        };
    }

    /// <summary>A JSON number's value where it is whole; null for one with a fraction or an exponent.</summary>
    private static Int128? WholeOf(JsonElement number) =>
        number.TryGetInt64(out long signed) ? signed : number.TryGetUInt64(out ulong unsigned) ? unsigned : null;

    /// <summary>The bytes a whole number's C# type holds it in, by the type the proxy names, else by its size; null for a
    /// type of no whole numbers (a double holding one).</summary>
    private static int? BytesOf(JsonElement value, Int128 whole) =>
        TypeOf(value) is not { } named ? (whole >= int.MinValue && whole <= int.MaxValue ? sizeof(int) : sizeof(long))
        : PrimitiveTypes.OfKeyword(named)?.Code switch
        {
            PrimitiveTypeCode.SByte or PrimitiveTypeCode.Byte => sizeof(byte),
            PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 => sizeof(short),
            PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 => sizeof(int),
            PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => sizeof(long),
            _ => null,
        };

    /// <summary>A remote value's type as C# names it ("int", "Bwa.Client.Pages.Counter"); null where the proxy names none.</summary>
    public static string? TypeOf(JsonElement value)
    {
        string? named = JsonMembers.Text(value, "className");
        if (named is null)
        {
            return JsonMembers.Text(value, "type") == "string" ? "string" : null;
        }
        // A number the proxy computed is said to be an Object: its JavaScript kind is all there is.
        if (named == "Object" && JsonMembers.Text(value, "type") == "number")
        {
            return null;
        }
        return (PrimitiveTypes.Named(named) ?? PrimitiveTypes.Named($"System.{named}"))?.Keyword ?? named;
    }

    /// <summary>The object a remote value holds whose members open beneath its row; null for a string, a null, or a value with none.</summary>
    public static string? Inside(JsonElement value) =>
        JsonMembers.Text(value, "type") == "object" && JsonMembers.Text(value, "subtype") != "null"
            && JsonMembers.Text(value, "objectId") is { } id && id.StartsWith(DotnetId, StringComparison.Ordinal) ? id : null;

    /// <summary>The words of an evaluation the proxy refused, or of an exception the program threw.</summary>
    public static string Failure(JsonElement details) =>
        JsonMembers.Text(JsonMembers.Object(details, "exception"), "description") ?? JsonMembers.Text(details, "text") ?? "The expression could not be evaluated";

    private static string Quoted(string text) => $"\"{text.Replace("\\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", @"\n", StringComparison.Ordinal).Replace("\r", @"\r", StringComparison.Ordinal).Replace("\t", @"\t", StringComparison.Ordinal)}\"";
}
