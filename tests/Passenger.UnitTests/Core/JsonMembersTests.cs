using System.Text.Json;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A JSON member is read as the kind it should be, by every reader of another program's or a person's JSON (an
/// engine's answers, launch profiles, dotnet new's templates, a server's messages): one of another kind, or a holder that is
/// no object, reads as nothing rather than throwing out of the reader.</summary>
public sealed class JsonMembersTests
{
    private const string Written = """
        {
          "name": "web", "port": 8080, "big": 3000000000, "half": 1.5, "on": true, "off": false, "none": null,
          "digits": "8080", "inner": { "Name": "kept" }, "list": ["a", 2, "b", null], "empty": []
        }
        """;

    [Fact]
    public void Each_read_takes_a_member_of_its_own_kind()
    {
        using JsonDocument read = JsonDocument.Parse(Written);
        JsonElement root = read.RootElement;
        Assert.Equal("web", JsonMembers.Text(root, "name"));
        Assert.Equal(8080, JsonMembers.Number(root, "port"));
        Assert.True(JsonMembers.Bool(root, "on"), "A member written true did not read as true");
        Assert.False(JsonMembers.Bool(root, "off"), "A member written false did not read as false");
        Assert.Equal("kept", JsonMembers.Text(JsonMembers.Object(root, "inner"), "Name"));
        Assert.Equal(["Name"], JsonMembers.Properties(root, "inner").Select(p => p.Name));
        Assert.Equal(4, JsonMembers.Items(root, "list").Count);
        Assert.Equal(["a", "b"], JsonMembers.Texts(root, "list"));
    }

    [Fact]
    public void A_scalar_reads_as_its_text_whatever_kind_it_was_written_as()
    {
        using JsonDocument read = JsonDocument.Parse(Written);
        JsonElement root = read.RootElement;
        Assert.Equal("web", JsonMembers.Scalar(root, "name"));
        Assert.Equal("8080", JsonMembers.Scalar(root, "port"));
        Assert.Equal("1.5", JsonMembers.Scalar(root, "half"));
        Assert.Equal("true", JsonMembers.Scalar(root, "on"));
        Assert.Equal("false", JsonMembers.Scalar(root, "off"));
        Assert.True(JsonMembers.Scalar(root, "none") is null, "A null member read as text, which an engine's reader takes as a value given");
        Assert.True(JsonMembers.Scalar(root, "inner") is null && JsonMembers.Scalar(root, "list") is null,
            "An object or an array read as a scalar's text, which an engine's reader would take as the value it asked for");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("port")]
    [InlineData("big")]
    [InlineData("half")]
    [InlineData("on")]
    [InlineData("none")]
    [InlineData("digits")]
    [InlineData("inner")]
    [InlineData("list")]
    [InlineData("missing")]
    public void A_member_of_another_kind_reads_as_nothing(string name)
    {
        using JsonDocument read = JsonDocument.Parse(Written);
        JsonElement root = read.RootElement;
        JsonElement member = root.TryGetProperty(name, out JsonElement found) ? found : default;
        Assert.True(member.ValueKind == JsonValueKind.String || JsonMembers.Text(root, name) is null, $"{name} read as text though it is {member.ValueKind}");
        Assert.True((member.ValueKind == JsonValueKind.Number && name == "port") || JsonMembers.Number(root, name) is null,
            $"{name} read as a whole number an int holds though it is {member.ValueKind}");
        Assert.True(member.ValueKind is JsonValueKind.True or JsonValueKind.False || JsonMembers.Bool(root, name) is null, $"{name} read as true or false");
        Assert.True(member.ValueKind == JsonValueKind.Object || JsonMembers.Object(root, name) is null, $"{name} read as an object");
        Assert.True(member.ValueKind == JsonValueKind.Object || JsonMembers.Properties(root, name).Count == 0, $"{name} read as an object's members");
        Assert.True(member.ValueKind == JsonValueKind.Array || JsonMembers.Items(root, name).Count == 0, $"{name} read as an array's items");
        Assert.True(member.ValueKind == JsonValueKind.Array || JsonMembers.Texts(root, name).Count == 0, $"{name} read as an array's strings");
    }

    [Theory]
    [InlineData("[{ \"name\": \"web\" }]")]
    [InlineData("\"name\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void A_holder_that_is_no_object_has_no_members(string text)
    {
        using JsonDocument read = JsonDocument.Parse(text);
        JsonElement holder = read.RootElement;
        Assert.True(JsonMembers.Text(holder, "name") is null && JsonMembers.Scalar(holder, "name") is null && JsonMembers.Number(holder, "name") is null
            && JsonMembers.Bool(holder, "name") is null && JsonMembers.Object(holder, "name") is null && JsonMembers.Properties(holder, "name").Count == 0
            && JsonMembers.Items(holder, "name").Count == 0 && JsonMembers.Texts(holder, "name").Count == 0,
            $"A member was read out of {text}, where System.Text.Json's own read throws past a reader's catch of malformed JSON");
    }

    [Fact]
    public void No_holder_at_all_has_no_members()
    {
        JsonElement? none = null;
        Assert.True(JsonMembers.Text(none, "name") is null && JsonMembers.Object(default(JsonElement), "name") is null && JsonMembers.Items(none, "name").Count == 0,
            "A member was read out of a holder a missing object gave, so a read through an object that is not there would not chain");
    }

    [Fact]
    public void A_name_is_matched_as_written_unless_any_case_is_asked_for()
    {
        using JsonDocument read = JsonDocument.Parse(Written);
        JsonElement inner = JsonMembers.Object(read.RootElement, "inner")!.Value;
        Assert.True(JsonMembers.Text(inner, "name") is null, "A member named Name was read as name, where the reader asked for that letter case alone");
        Assert.Equal("kept", JsonMembers.Text(inner, "name", anyCase: true));
        Assert.Equal("kept", JsonMembers.Text(JsonMembers.Object(read.RootElement, "INNER", anyCase: true), "NAME", anyCase: true));
    }
}
