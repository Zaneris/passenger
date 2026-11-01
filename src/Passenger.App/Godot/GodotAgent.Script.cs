using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>The helper's script as Passenger carries it, and the file Godot loads it from.</summary>
public static partial class GodotAgent
{
    private static readonly Lazy<byte[]> Source = new(() =>
    {
        using Stream stream = typeof(GodotAgent).Assembly.GetManifestResourceStream("Godot/PassengerAgent.gd")
            ?? throw new InvalidOperationException("Passenger carries no Godot/PassengerAgent.gd");
        using MemoryStream read = new();
        stream.CopyTo(read);
        return read.ToArray();
    });

    /// <summary>Where Godot loads it from: the state's cache, where nothing the game's project holds is touched.</summary>
    public static string Path => System.IO.Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", FileName);

    /// <summary>Its file, written where it is not there as carried: its path. Off the UI thread.</summary>
    public static string Written()
    {
        string path = Path;
        byte[] source = Source.Value;
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(source))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, source);
        }
        return path;
    }
}
