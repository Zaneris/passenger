using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>What the server reads of an .editorconfig (the extension's <see cref="Handlers.EditorConfigKeys"/>): its C#
/// options and the diagnostics the projects' analyzers report, asked once per file until the server's projects change.</summary>
public sealed partial class CSharpService
{
    /// <summary>Each .editorconfig's answer, by its path, from the session that gave it, until the projects change.</summary>
    private readonly Dictionary<string, EditorConfigAsked> editorConfigKeys = new(PathNames.Comparer);

    /// <summary>What the server reads of the .editorconfig at a path; null while it runs no project, and without the
    /// extension. An answer of none is asked again the next time.</summary>
    internal Task<EditorConfigKeysFound?> EditorConfigKeysAsync(string path)
    {
        UiThread.Check();
        if (session is not { } running)
        {
            return Task.FromResult<EditorConfigKeysFound?>(null);
        }
        if (!editorConfigKeys.TryGetValue(path, out EditorConfigAsked? asked) || asked.Session != running || asked.Empty)
        {
            asked = new EditorConfigAsked(running);
            asked.Found = asked.AskAsync(path);
            editorConfigKeys[path] = asked;
        }
        return asked.Found;
    }

    /// <summary>An .editorconfig's keys asked of a session, and whether it answered none.</summary>
    private sealed class EditorConfigAsked(RoslynSession session)
    {
        private volatile bool empty;

        public RoslynSession Session => session;

        public Task<EditorConfigKeysFound?> Found { get; set; } = Task.FromResult<EditorConfigKeysFound?>(null);

        public bool Empty => empty;

        public async Task<EditorConfigKeysFound?> AskAsync(string path)
        {
            try
            {
                EditorConfigKeysFound found = await session.ExtensionAsync<EditorConfigKeysFound>(Handlers.EditorConfigKeys, new EditorConfigKeysAsked(path));
                empty = found.Projects.Count == 0;
                return empty ? null : found;
            }
            catch (LanguageServerFailed)
            {
                empty = true;
                return null;
            }
        }
    }

    /// <summary>Which of the values an .editorconfig gives the server's C# options their own reading refuses, by their places
    /// in <paramref name="pairs"/>; none without the server or its extension.</summary>
    internal async Task<IReadOnlyList<int>> EditorConfigRefusedAsync(IReadOnlyList<EditorConfigPair> pairs)
    {
        if (session is not { } running || pairs.Count == 0)
        {
            return [];
        }
        try
        {
            return (await running.ExtensionAsync<EditorConfigValuesRefused>(Handlers.EditorConfigValues, new EditorConfigValuesAsked(pairs))).Refused;
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
    }

    /// <summary>The projects changed, and with them the analyzers each .editorconfig is read by.</summary>
    private void ForgetEditorConfigKeys() => editorConfigKeys.Clear();
}
