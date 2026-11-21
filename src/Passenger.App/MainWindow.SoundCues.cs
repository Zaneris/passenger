using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The sound cues (<see cref="SoundCues"/>): one as the caret comes to another line of the same file holding an
/// error, a warning, a breakpoint, a folded region or a change, the first of those Settings turns on, and nothing as it
/// moves along a line; one as a build or a test run passes or fails, and as the debugger stops the person's program.</summary>
public partial class MainWindow
{
    private readonly List<SoundCue> cued = [];

    /// <summary>The file and line the caret was last on, which a cue is told against.</summary>
    private (OpenDocument? File, int Line) caretCuedAt;

    /// <summary>Whether a cue the system's player could not play has said why, which it says once.</summary>
    private bool cueRefusalSaid;

    /// <summary>Every cue the window played, in order, or would have where nobody sees it.</summary>
    public IReadOnlyList<SoundCue> CuesPlayed => cued;

    private void AddSoundCues()
    {
        Editors.CaretMoved += (_, _) => CueCaretLine();
        Activated += (_, _) =>
        {
            if (!Unseen)
            {
                ScreenReaders.LookAgain();
            }
        };
    }

    private void BindSoundCues(ProjectSession next)
    {
        buildRun.BuildFinished += (_, built) =>
        {
            if (session == next && built.Status is BuildStatus.Succeeded or BuildStatus.Failed)
            {
                Cue(built.Succeeded ? SoundCue.BuildPassed : SoundCue.BuildFailed);
            }
        };
        next.Tests.Ran += (_, ended) =>
        {
            if (session == next && !ended.Canceled)
            {
                Cue(ended.Aborted || ended.Error is not null || ended.Outcomes.Any(o => o.State == Testing.TestState.Failed) ? SoundCue.TestsFailed : SoundCue.TestsPassed);
            }
        };
    }

    /// <summary>Plays a cue where Settings turns it on and has cues play now (<see cref="SoundCues.Play"/>).</summary>
    public void Cue(SoundCue cue)
    {
        UiThread.Check();
        if (!SoundCues.Play(settings, ScreenReaders.Running) || !SoundCues.IsOn(settings, cue))
        {
            return;
        }
        cued.Add(cue);
        IdeErrors.Observe(PlayCueAsync(cue));
    }

    private async Task PlayCueAsync(SoundCue cue)
    {
        string file = await BackgroundWork.Run(() => SoundCues.FileOf(cue, Path.Combine(StateFolder, "cache", "sound-cues")));
        if (await PlaySoundAsync(file, sayWhyNot: !cueRefusalSaid) is not null)
        {
            cueRefusalSaid = true;
        }
    }

    /// <summary>The caret on another line of the file it was on: that line's cue, where it has one.</summary>
    private void CueCaretLine()
    {
        OpenDocument? file = Editors.Active;
        CodeEditor editor = Editors.CodeEditor;
        int line = editor.CaretPosition.Line;
        (OpenDocument? was, int wasLine) = caretCuedAt;
        caretCuedAt = (file, line);
        if (file is null || file != was || line == wasLine || !SoundCues.Play(settings, ScreenReaders.Running))
        {
            return;
        }
        foreach (SoundCue cue in LineCues(file, editor, line, wasLine))
        {
            if (SoundCues.IsOn(settings, cue))
            {
                Cue(cue);
                return;
            }
        }
    }

    /// <summary>What a line holds that a cue tells, the most pressing first: a change only where the caret came into it
    /// from <paramref name="from"/>, not along its lines.</summary>
    private IEnumerable<SoundCue> LineCues(OpenDocument file, CodeEditor editor, int line, int from)
    {
        TextSnapshot text = editor.Document.Snapshot;
        LineSpan span = text.GetLine(Math.Min(line, text.LineCount - 1));
        List<Marker> marked = [.. editor.Markers.Where(m => m.Range.Start <= span.End && (m.Range.End > span.Start || m.Range.Start == span.Start))];
        if (marked.Any(m => m.Kind == MarkerKind.Error))
        {
            yield return SoundCue.ErrorLine;
        }
        if (marked.Any(m => m.Kind == MarkerKind.Warning))
        {
            yield return SoundCue.WarningLine;
        }
        if (session.Debug.Breakpoints.In(file.Path).Any(b => b.Line == line))
        {
            yield return SoundCue.BreakpointLine;
        }
        if (editor.FoldRegions.Any(f => f.Folded && f.Start >= span.Start && f.Start <= span.End))
        {
            yield return SoundCue.FoldedLine;
        }
        if (ChangeOn(editor, line) is { } change && ChangeOn(editor, from) != change)
        {
            yield return SoundCue.ChangedLine;
        }
    }

    /// <summary>The change bar on a line, where one is.</summary>
    private static ChangeMark? ChangeOn(CodeEditor editor, int line)
    {
        foreach (ChangeMark change in editor.ChangeMarks)
        {
            if (change.Kind == ChangeMarkKind.Deleted ? change.Line == line : line >= change.Line && line < change.Line + Math.Max(1, change.Count))
            {
                return change;
            }
        }
        return null;
    }
}
