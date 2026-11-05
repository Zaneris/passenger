using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>JavaScript called from C# and Razor through JS interop (<see cref="JsInteropSource"/>'s calls, bound by Roslyn):
/// Go to Declaration from a call's identifier goes to the function it names, in the module the object reference was
/// imported from where that is known, else in the component's collocated script first and then the scripts of its project
/// and those near it; from an import's path, to the module's file.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>Where Go to Declaration goes from a JS interop call's string at an offset; null elsewhere, asking nothing
    /// where the offset's line holds no string around it.</summary>
    private async Task<Declared?> JsInteropDeclaredAsync(LspDocument lsp, OpenDocument document, int offset)
    {
        TextSnapshot asked = lsp.Text.Snapshot;
        if (!InString(asked, offset) || context.CSharp is not { } csharp || await csharp.JsInteropAsync(document.Path, CancellationToken.None) is not { } found
            || lsp.Text.Snapshot != asked)
        {
            return null;
        }
        if (found.Calls.FirstOrDefault(c => lsp.OffsetOf(new LspPosition(c.At.Line, c.At.Character)) <= offset
            && offset <= lsp.OffsetOf(new LspPosition(c.End.Line, c.End.Character))) is not { } call)
        {
            return null;
        }
        OpenTexts texts = context.Editors.Texts();
        string path = document.Path;
        IReadOnlyList<(string Path, int Start, string Name)> places = await BackgroundWork.Run(() => Places(call, path, found.Projects, texts));
        List<LspLocation> located = [];
        foreach ((string file, int start, _) in places)
        {
            LspPosition at = texts.TextOf(file) is { } text ? ServerLines.Of(text).PositionOf(Math.Min(start, text.Length)) : new LspPosition(0, 0);
            located.Add(new LspLocation(DocumentUris.FromPath(file), new LspRange(at, at)));
        }
        bool import = call.Module is not null && call.Identifier == JsInteropNames.Import;
        return new Declared(located, import ? $"No project here serves the module {call.Module}" : $"No script here declares {call.Identifier}")
        {
            Names = [.. places.Select(p => (string?)p.Name)],
            Title = import ? "Modules" : "Declarations",
        };
    }

    /// <summary>The places a call leads: an import's module files that are there, at their start; else the declarations of the
    /// function it names in its module, or in the file's collocated script and then every script of its projects.</summary>
    private static IReadOnlyList<(string Path, int Start, string Name)> Places(JsInteropCall call, string path, IReadOnlyList<string> projects, OpenTexts texts)
    {
        List<(string, int, string)> places = [];
        if (call.Module is { } module)
        {
            IEnumerable<string> files = JsFunctions.ModuleFiles(module, projects).Where(f => texts.TextOf(f) is not null);
            if (call.Identifier == JsInteropNames.Import)
            {
                return [.. files.Take(1).Select(f => (f, 0, Path.GetFileName(f)))];
            }
            foreach (string file in files.Take(1))
            {
                Add(file);
            }
            return places;
        }
        HashSet<string> read = new(PathNames.Comparer);
        if (JsFunctions.CollocatedOf(path) is { } collocated && read.Add(collocated))
        {
            Add(collocated);
            if (places.Count > 0)
            {
                return places;
            }
        }
        foreach (string project in projects)
        {
            foreach ((_, IReadOnlyList<string> files) in PathNames.FilesUnder(Path.GetDirectoryName(project)!, ProjectFolders.IsOwn))
            {
                foreach (string file in files.Where(f => JsFunctions.IsScript(f) && read.Add(f)))
                {
                    Add(file);
                }
            }
        }
        return places;

        void Add(string file)
        {
            if (texts.TextOf(file) is not { } text)
            {
                return;
            }
            string script = text.ToString();
            foreach ((int start, _) in JsFunctions.Declared(script, call.Identifier))
            {
                places.Add((file, start, $"{Path.GetFileName(file)}: {text.GetLineText(text.GetLineFromOffset(start)).Trim()}"));
            }
        }
    }

    /// <summary>Whether an offset lies between two double quotes on its line, where a call's identifier or a module's path is.</summary>
    private static bool InString(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        string row = text.GetLineText(line);
        int column = offset - text.GetLine(line).Start;
        return column >= 0 && column <= row.Length && row.LastIndexOf('"', Math.Max(0, column - 1)) >= 0 && row.IndexOf('"', column) >= 0;
    }
}
