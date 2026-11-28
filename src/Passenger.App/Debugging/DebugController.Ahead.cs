using Passenger.App.Workspace;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>The code ahead of a stop: at each stop the debugger reads, from the values its top frame stands with and running
/// none of the program's code, which of the method's code won't run, which the editor fades, and the values its locals will
/// take on the lines ahead, shown after them as inline values are. Read again at every stop and step, gone once the program
/// runs on.</summary>
public sealed partial class DebugController
{
    /// <summary>Whether a stop fades the code ahead it won't run (Settings' Grey the code ahead a stop won't run): changed at
    /// a stop, the stop's lines follow at once.</summary>
    public bool GreysAhead
    {
        get => greysAhead;
        set
        {
            if (greysAhead == value)
            {
                return;
            }
            greysAhead = value;
            aheadShown = null;
            if (inlineShown is { } inline && Session is { State: DebugState.Paused } session && view.ShownSnapshot is null)
            {
                ShowInlineValues(inline.Frame, inline.Values);
                if (value)
                {
                    IdeErrors.Observe(ShowAheadAsync(session, inline.Frame, shown));
                }
            }
        }
    }

    private bool greysAhead = true;

    /// <summary>The frame whose code ahead the lines show, and what the debugger read of it, until the program goes on.</summary>
    private (DapStackFrame Frame, AheadResponse Read)? aheadShown;

    /// <summary>What a stop's top frame will do from where it stands, as the debugger reads it; null where it reads none (a
    /// debugger that is not Passenger's .NET one, a frame below the top).</summary>
    public async Task<AheadResponse?> AheadOfAsync(DebugSession session, DapStackFrame frame)
    {
        if (!greysAhead || session.Engine != DebugEngine.Managed || session.State != DebugState.Paused)
        {
            return null;
        }
        try
        {
            return await session.AheadAsync(frame.Id);
        }
        catch (DapException error)
        {
            IdeLog.Write(IdeLog.Debugger, $"The code ahead was not read: {error.Reason}");
            return null;
        }
    }

    /// <summary>Reads the code ahead of the chosen frame, where it is its stop's top one, and shows it while that stop and
    /// frame are still the ones shown.</summary>
    private async Task ShowAheadAsync(DebugSession session, DapStackFrame current, int stop)
    {
        if (Stop?.Frames.FirstOrDefault()?.Id != current.Id || await AheadOfAsync(session, current) is not { } read)
        {
            return;
        }
        if (stop != shown || frame != current || view.ShownSnapshot is not null || inlineShown is not { } inline)
        {
            return;
        }
        aheadShown = (current, read);
        ShowInlineValues(inline.Frame, inline.Values);
    }

    /// <summary>The code ahead the frame won't run, as ranges of the document as it now is.</summary>
    private IReadOnlyList<TextRange> FadedAhead(OpenDocument document, DapStackFrame current)
    {
        if (aheadShown is not { } ahead || ahead.Frame.Id != current.Id)
        {
            return [];
        }
        TextSnapshot text = document.Text.Snapshot;
        List<TextRange> ranges = [];
        foreach (DapAheadSpan span in ahead.Read.WontRun)
        {
            int start = Offset(span.Line, span.Column);
            int end = Offset(span.EndLine, span.EndColumn);
            if (end > start)
            {
                ranges.Add(new TextRange(start, end - start));
            }
        }
        return ranges;

        int Offset(int line, int column)
        {
            LineSpan at = text.GetLine(Math.Clamp(Current(document.Path, line - 1), 0, text.LineCount - 1));
            return at.Start + Math.Clamp(column - 1, 0, at.Length);
        }
    }

    /// <summary>The values the frame's locals will take on the lines ahead of its stop, after those lines.</summary>
    private IReadOnlyList<LineAnnotation> ValuesAhead(OpenDocument document, DapStackFrame current, int stopLine)
    {
        if (aheadShown is not { } ahead || ahead.Frame.Id != current.Id)
        {
            return [];
        }
        return [.. ahead.Read.Values
            .Select(value => new LineAnnotation(Current(document.Path, value.Line - 1), $"{value.Name}: {InlineValues.Cut(value.Value)}", LineAnnotationLook.Muted))
            .Where(annotation => annotation.Line > stopLine)];
    }
}
