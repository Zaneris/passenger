using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>Each open .feature file read and its steps matched (<see cref="FeatureAnalysis"/>) off the window's thread, as it
/// opens, once typing pauses, and as its project's bindings are read again: its marks set and its colours laid over its
/// grammar's, and the last reading kept for the editor's commands. A UI-thread object.</summary>
internal sealed class FeatureChecks : IDisposable
{
    public const string Checker = "Passenger's reading of its steps";

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly ProjectSession session;
    private readonly Dictionary<OpenDocument, Kept> documents = [];

    public FeatureChecks(ProjectSession session)
    {
        this.session = session;
        session.Steps.Changed += (_, _) => Changed();
    }

    public static bool IsFeature(string path) => PathNames.HasExtension(path, [".feature"]);

    /// <summary>Raised on the window's thread as an open file's reading lands.</summary>
    public event EventHandler<OpenDocument>? Analysed;

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (!IsFeature(document.Path) || documents.ContainsKey(document))
        {
            return;
        }
        Kept kept = new();
        kept.Pause = new Debounce(Settle, () => AnalyseAsync(document), () => document.Checking(Checker));
        kept.Typed = (_, _) => kept.Pause.Restart();
        if (document.Highlighter is { } grammar)
        {
            kept.Layer = new ColourLayer(grammar, document.Text);
            document.Highlighter = kept.Layer;
        }
        documents[document] = kept;
        document.Text.Changed += kept.Typed;
        IdeErrors.Observe(FirstAsync(document));
    }

    private async Task FirstAsync(OpenDocument document)
    {
        using OpenDocument.CheckHold hold = document.Checking(Checker);
        await AnalyseAsync(document);
    }

    public string? CheckOf(OpenDocument document) => documents.ContainsKey(document) ? Checker : null;

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out Kept? gone))
        {
            document.Text.Changed -= gone.Typed;
            gone.Pause.Stop();
            if (gone.Layer is { } layer)
            {
                if (document.Highlighter == layer)
                {
                    document.Highlighter = layer.Syntax;
                }
                layer.Dispose();
            }
            if (document.Markers.Count > 0)
            {
                document.Markers = [];
            }
        }
    }

    public void Dispose()
    {
        foreach (OpenDocument document in documents.Keys.ToList())
        {
            Detach(document);
        }
    }

    /// <summary>The bindings were read again: every open feature is matched again.</summary>
    public void Changed()
    {
        foreach (OpenDocument document in documents.Keys.ToList())
        {
            IdeErrors.Observe(AnalyseAsync(document));
        }
    }

    /// <summary>An open feature's last reading where it is of the text as it stands; null otherwise.</summary>
    public FeatureAnalysis? Of(OpenDocument document) =>
        documents.TryGetValue(document, out Kept? kept) && kept.Last is { } last && last.Version == document.Text.Version ? last : null;

    /// <summary>An open feature's reading of the text as it stands, read now where the last is older.</summary>
    public async Task<FeatureAnalysis?> CurrentAsync(OpenDocument document)
    {
        if (!documents.ContainsKey(document))
        {
            return null;
        }
        return Of(document) ?? await AnalyseAsync(document);
    }

    /// <summary>A feature's text read against what is known of its project now, a text that is not open read from disk.</summary>
    public async Task<FeatureAnalysis> ReadAsync(string path, Passenger.Text.TextSnapshot text)
    {
        string? folder = session.FeatureProjectFolder(path);
        ProjectSteps? steps = session.Steps.For(session.ProjectOf(path)?.Path);
        return await BackgroundWork.Run(() => FeatureAnalysis.Of(text, -1, folder is null ? ReqnrollConfig.Parse("{}", false) : ReqnrollConfig.In(folder), steps, ShownColours.None));
    }

    private async Task<FeatureAnalysis?> AnalyseAsync(OpenDocument document)
    {
        if (!documents.TryGetValue(document, out Kept? kept))
        {
            return null;
        }
        Passenger.Text.TextSnapshot text = document.Text.Snapshot;
        int version = document.Text.Version;
        string? folder = session.FeatureProjectFolder(document.Path);
        ProjectSteps? steps = session.Steps.For(session.ProjectOf(document.Path)?.Path);
        ShownColours shown = kept.Layer?.Shown ?? ShownColours.None;
        FeatureAnalysis analysis = await BackgroundWork.Run(() =>
            FeatureAnalysis.Of(text, version, folder is null ? ReqnrollConfig.Parse("{}", false) : ReqnrollConfig.In(folder), steps, shown));
        if (!documents.ContainsKey(document) || document.Text.Version != version)
        {
            return analysis;
        }
        kept.Last = analysis;
        if (!analysis.Marks.SequenceEqual(document.Markers))
        {
            document.Markers = analysis.Marks;
        }
        kept.Layer?.Set(analysis.Colours, version, analysis.ColoursDiffer);
        Analysed?.Invoke(this, document);
        return analysis;
    }

    private sealed class Kept
    {
        public Debounce Pause { get; set; } = null!;

        public EventHandler<Passenger.Text.DocumentChangedEventArgs> Typed { get; set; } = null!;

        public ColourLayer? Layer { get; set; }

        public FeatureAnalysis? Last { get; set; }
    }
}
