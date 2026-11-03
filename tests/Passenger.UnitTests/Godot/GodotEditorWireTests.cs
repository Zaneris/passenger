using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>What Passenger's addon and the window say before anything else: the listing the addon reads (the folders,
/// port, both secrets and the process listening), the addon's secret taken only as a whole first line, before anything
/// after it is read, and then answered with Passenger's own.</summary>
public class GodotEditorWireTests
{
    private const string Token = "A1B2C3";
    private const string Reply = "D4E5F6";

    private static readonly byte[] TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(Token));

    [Fact]
    public void The_listing_names_the_folders_once_the_port_both_secrets_and_the_process()
    {
        using JsonDocument read = JsonDocument.Parse(GodotEditorWire.ListingText(["/games/spinner", "/games/spinner", "/link/spinner"], 50123, Token, Reply, 77));
        JsonElement root = read.RootElement;

        Assert.Equal(["/games/spinner", "/link/spinner"], root.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
        Assert.True(root.GetProperty("port").GetInt32() == 50123 && root.GetProperty("token").GetString() == Token
            && root.GetProperty("reply").GetString() == Reply && root.GetProperty("process").GetInt32() == 77, root.GetRawText());
    }

    [Theory]
    [InlineData(Token + "\n", true)]
    [InlineData(Token + "\r\n", true)]
    [InlineData("A1B2C4\n", false)]
    [InlineData(Token, false)]
    [InlineData("\n", false)]
    public async Task Only_the_secret_as_the_first_line_is_answered_with_Passengers_own(string said, bool taken)
    {
        Wire wire = new(Encoding.UTF8.GetBytes(said + "{\"id\":1}\n"));

        bool greeted = await GodotEditorWire.GreetedAsync(wire, TokenHash, Reply, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(taken, greeted);
        Assert.Equal(taken ? Reply + "\n" : string.Empty, Encoding.UTF8.GetString(wire.Written.ToArray()));
        Assert.True(!taken || wire.Unread == "{\"id\":1}\n", $"Nothing after the secret was to be read before it was checked; left: {wire.Unread}");
    }

    [Fact]
    public async Task A_first_line_longer_than_any_secret_is_refused_before_its_end()
    {
        Wire wire = new(Encoding.UTF8.GetBytes(new string('A', 4096) + "\n"));

        Assert.False(await GodotEditorWire.GreetedAsync(wire, TokenHash, Reply, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(wire.Unread.Length > 3000, $"Only the longest secret's bytes and one more were to be read; {wire.Unread.Length} left");
    }

    /// <summary>A connection's two directions: what the addon said, read a byte at a time, and what it is answered.</summary>
    private sealed class Wire(byte[] said) : Stream
    {
        private readonly MemoryStream read = new(said);

        public MemoryStream Written { get; } = new();

        public string Unread => Encoding.UTF8.GetString(said, (int)read.Position, said.Length - (int)read.Position);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}
