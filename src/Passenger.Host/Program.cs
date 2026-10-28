using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Build.Locator;

namespace Passenger.Host;

/// <summary>The host's start: the SDK the folder it is given picks (its global.json, else the newest), whose MSBuild it
/// loads before anything touches MSBuild's types, then requests read from standard input until it closes, which is the
/// app letting go. Standard output is the protocol's alone: whatever else writes to the console goes to standard error.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Replies replies = new(Console.OpenStandardOutput());
        Console.SetOut(Console.Error);
        string folder = args is ["--folder", { } given] ? given : Environment.CurrentDirectory;
        VisualStudioInstance? sdk = Sdks.For(folder, out string? problem);
        if (sdk is null)
        {
            replies.Error(0, problem);
            return 2;
        }
        MSBuildLocator.RegisterInstance(sdk);
        Serve(replies, sdk);
        return 0;
    }

    /// <summary>In a method of its own, so no MSBuild type is resolved before the locator has said where MSBuild is.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Serve(Replies replies, VisualStudioInstance sdk) =>
        new Server(replies, new HostReady(sdk.MSBuildPath, sdk.Version.ToString())).Run(Console.In);
}

/// <summary>Finding the SDK a folder's projects build with: the one its global.json picks, else the newest.</summary>
public static class Sdks
{
    public static VisualStudioInstance? For(string folder, out string? problem)
    {
        try
        {
            VisualStudioInstance? found = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
            {
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = folder,
            }).FirstOrDefault();
            problem = found is null ? $"no .NET SDK this host's runtime ({Environment.Version}) can load was found for {folder}" : null;
            return found;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = $"the .NET SDK for {folder} could not be found: {error.Message}";
            return null;
        }
    }
}
