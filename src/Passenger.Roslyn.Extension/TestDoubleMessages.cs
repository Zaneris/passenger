using System.Text.Json.Serialization;

namespace Passenger.Roslyn.Extension;

// Test doubles' messages to Passenger's extension in Roslyn's server, compiled into both.

/// <summary>What a place in a C# file can be given for a test, asked with the solution's test projects by their files.</summary>
public sealed record DoublesAsked(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("testProjects")] IReadOnlyList<string> TestProjects);

/// <summary>What a place can be given for a test: the type its name names (none where it names none a test could stand in
/// for), why no fake can be made of it where none can (<see cref="FakeSeams"/>), whether a fake of it leaves its members
/// that are not virtual its own, and each test project that sees it, where a fake of it would go; and, where the place is
/// in a call missing arguments a double can stand in for, the mocking libraries its project references
/// (<see cref="MockingLibraries"/>), each of which can fill them.</summary>
public sealed record DoublesOffered(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("seam")] string? Seam,
    [property: JsonPropertyName("keepsOwn")] bool KeepsOwn,
    [property: JsonPropertyName("fakes")] IReadOnlyList<FakePlace> Fakes,
    [property: JsonPropertyName("libraries")] IReadOnlyList<string> Libraries);

/// <summary>A test project a fake can go into: its file, its name, the fake's file, and whether a file is there already.</summary>
public sealed record FakePlace(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("exists")] bool Exists);

/// <summary>Create Fake's request: a fake of the type a place names, written into the test project of this file.</summary>
public sealed record FakeAsked(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("project")] string Project);

/// <summary>Why no fake can stand in for a class of the solution's own, which a seam answers.</summary>
public static class FakeSeams
{
    /// <summary>A class no other may derive from.</summary>
    public const string Sealed = "sealed";

    /// <summary>A static class, which nothing makes.</summary>
    public const string Static = "static";

    /// <summary>A class whose constructors no class deriving from it outside its assembly can call.</summary>
    public const string Closed = "closed";
}

/// <summary>Fill Arguments with Doubles' request: the missing arguments of the call a place is in, each a double of the
/// library named where one can stand in for its parameter, else its type's default.</summary>
public sealed record ArgumentsAsked(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("library")] string Library);

/// <summary>The mocking libraries Fill Arguments with Doubles writes doubles of, by their assemblies' names.</summary>
public static class MockingLibraries
{
    public const string NSubstitute = "NSubstitute";
    public const string Moq = "Moq";
    public const string FakeItEasy = "FakeItEasy";
}
