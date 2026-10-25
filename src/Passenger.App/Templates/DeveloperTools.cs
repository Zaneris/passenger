using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.App.Templates;

/// <summary>Takes Avalonia Accelerate's developer tools out of what a template made, as Passenger leads no one into
/// Accelerate's paid tools: the AvaloniaUI.DiagnosticsSupport reference and the WithDeveloperTools or
/// AttachDeveloperTools calls of Debug builds. Only those lines go, with the #if DEBUG around a call when it held nothing
/// else; the rest of each file stays byte for byte.</summary>
public static partial class DeveloperTools
{
    [GeneratedRegex("""^[ \t]*<Package(?:Reference|Version)\b[^>]*\bInclude="AvaloniaUI\.DiagnosticsSupport"[^>]*?(?:/>|>.*?</Package(?:Reference|Version)>)[ \t]*(?:\r?\n)?""",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Package();

    [GeneratedRegex(@"^[ \t]*#if DEBUG[ \t]*\r?\n(?:[ \t]*[^\r\n]*\b(?:WithDeveloperTools|AttachDeveloperTools)\(\)[^\r\n]*\r?\n)+[ \t]*#endif[ \t]*(?:\r?\n)?",
        RegexOptions.Multiline)]
    private static partial Regex DebugOnly();

    [GeneratedRegex(@"^[ \t]*(?:\.WithDeveloperTools\(\)|this\.AttachDeveloperTools\(\)(?:[ \t]*\|>[ \t]*ignore)?;?)[ \t]*(?:\r?\n)?", RegexOptions.Multiline)]
    private static partial Regex Call();

    /// <summary>Takes them out of the projects, package versions and code under a folder (build output aside); the files
    /// it changed.</summary>
    public static IReadOnlyList<string> TakeOut(string folder)
    {
        List<string> changed = [];
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }
            Regex[]? edits = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" => [Package()],
                ".cs" or ".fs" or ".vb" => [DebugOnly(), Call()],
                _ => null,
            };
            if (edits is null)
            {
                continue;
            }
            if (FileText.Load(file) is not { } read)
            {
                continue;
            }
            string edited = edits.Aggregate(read.Text, (now, edit) => edit.Replace(now, string.Empty));
            if (edited != read.Text)
            {
                FileText.Save(file, edited, read.Encoding, read.HasBom, read.CrBreaks);
                changed.Add(file);
            }
        }
        return changed;
    }
}
