using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>Each open .feature file read and its steps matched (<see cref="FeatureAnalysis"/>) off the window's thread, as it
/// opens, once typing pauses, and as its project's bindings are read again: its marks set and its colours laid over its
/// grammar's, and the last reading kept for the editor's commands. A UI-thread object.</summary>
internal sealed class FeatureChecks : OpenFileChecks<FeatureChecks.Kept>
{
    public const string Checker = "Passenger's reading of its steps";

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly ProjectSession session;

    public FeatureChecks(ProjectSession session) : base(Settle)
    {
        this.session = session;
        session.Steps.Changed += (_, _) => CheckAll();
    }

    public static bool IsFeature(string path) => PathNames.HasExtension(path, [".feature"]);

    /// <summary>Raised on the window's thread as an open file's reading lands.</summary>
    public event EventHandler<OpenDocument>? Analysed;

    protected override (string Checker, Kept Kept)? Follow(OpenDocument document)
    {
        if (!IsFeature(document.Path))
        {
            return null;
        }
        Kept kept = new();
        if (document.Highlighter is { } grammar)
        {
            kept.Layer = new ColourLayer(grammar, document.Text);
            document.Highlighter = kept.Layer;
        }
        return (Checker, kept);
    }

    protected override Task CheckAsync(OpenDocument document, Kept kept) => AnalyseAsync(document, kept);

    protected override void LetGo(OpenDocument document, Kept kept)
    {
        if (kept.Layer is { } layer)
        {
            if (document.Highlighter == layer)
            {
                document.Highlighter = layer.Syntax;
            }
            layer.Dispose();
        }
    }

    /// <summary>An open feature's last reading where it is of the text and the bindings as they stand; null otherwise.</summary>
    public FeatureAnalysis? Of(OpenDocument document) =>
        KeptOf(document)?.Last is { } last && last.Version == document.Text.Version && last.StepsRead == session.Steps.Version ? last : null;

    /// <summary>An open feature's reading of the text as it stands, read now where the last is older.</summary>
    public async Task<FeatureAnalysis?> CurrentAsync(OpenDocument document) =>
        KeptOf(document) is { } kept ? Of(document) ?? await AnalyseAsync(document, kept) : null;

    /// <summary>A feature's text read against what is known of its project now, a text that is not open read from disk.</summary>
    public async Task<FeatureAnalysis> ReadAsync(string path, Passenger.Text.TextSnapshot text)
    {
        string? folder = session.FeatureProjectFolder(path);
        ProjectSteps? steps = session.Steps.For(session.ProjectOf(path)?.Path);
        return await BackgroundWork.Run(() => FeatureAnalysis.Of(text, -1, folder is null ? ReqnrollConfig.Parse("{}", false) : ReqnrollConfig.In(folder), steps, ShownColours.None));
    }

    private async Task<FeatureAnalysis> AnalyseAsync(OpenDocument document, Kept kept)
    {
        Passenger.Text.TextSnapshot text = document.Text.Snapshot;
        int version = document.Text.Version;
        string? folder = session.FeatureProjectFolder(document.Path);
        ProjectSteps? steps = session.Steps.For(session.ProjectOf(document.Path)?.Path);
        ShownColours shown = kept.Layer?.Shown ?? ShownColours.None;
        int read = session.Steps.Version;
        FeatureAnalysis analysis = await BackgroundWork.Run(() =>
            FeatureAnalysis.Of(text, version, folder is null ? ReqnrollConfig.Parse("{}", false) : ReqnrollConfig.In(folder), steps, shown, read));
        if (!Stands(document, version))
        {
            return analysis;
        }
        kept.Last = analysis;
        document.Markers = analysis.Marks;
        kept.Layer?.Set(analysis.Colours, version, analysis.ColoursDiffer);
        Analysed?.Invoke(this, document);
        return analysis;
    }

    /// <summary>What is kept of an open feature: its colours laid over its grammar's, and its last reading.</summary>
    internal sealed class Kept
    {
        public ColourLayer? Layer { get; set; }

        public FeatureAnalysis? Last { get; set; }
    }
}
