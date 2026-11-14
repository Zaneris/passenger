using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>What Vim mode asks of the window: :w, :q, :wq and :e as Save, Close Tab and opening a file, Ctrl+O and Ctrl+I
/// as Back and Forward, = as Reformat Code over its lines, :action as any action of the IDE, :bufdo, :windo, :argdo, :cdo,
/// :cfdo and a line of commands joined by '|' as its commands run in each file or place, its jumps kept among the places
/// Back goes to, and its words in the status bar.</summary>
public partial class MainWindow
{
    /// <summary>Vim mode's mappings file, followed once Vim mode is first on.</summary>
    private Passenger.App.Editors.VimMappingsFile? vimMappings;

    /// <summary>The mappings Vim mode's keys go through in this window, as its mappings file was last read.</summary>
    public VimMappings VimMappings => vimRegisters.Mappings;

    /// <summary>Where Vim mode's mappings file is read from: vimrc in the window's state folder.</summary>
    public string VimMappingsPath => Path.Combine(StateFolder, Passenger.App.Editors.VimMappingsFile.FileName);

    /// <summary>Vim mode on: its mappings file read and followed from then on, every editor of the window taking what it maps.</summary>
    private void FollowVimMappings(bool on)
    {
        if (!on || vimMappings is not null)
        {
            return;
        }
        Passenger.App.Editors.VimMappingsFile file = new(StateFolder);
        vimMappings = file;
        file.Read += (_, read) => TakeVimMappings(read);
        Activated += (_, _) => file.LookAgain();
        Closed += (_, _) => file.Dispose();
        file.LookAgain();
    }

    /// <summary>A reading of the mappings file in force: its mappings and set lines; each line passed over said, once.</summary>
    private void TakeVimMappings(VimMappings read)
    {
        vimRegisters.Mappings = read;
        IReadOnlyList<string> refused = Vim.SetAll(vimRegisters, read.Settings);
        List<string> said = [.. read.Problems.Select(p => $"line {p.Line}: {p.Why}"), .. refused];
        if (said.Count > 0)
        {
            Say($"Vim mode passed over {Plural.Of(said.Count, "line")} of {Passenger.App.Editors.VimMappingsFile.FileName}, {said[0]}{(said.Count > 1 ? " (and more: see the IDE log)" : string.Empty)}");
            foreach (string line in said)
            {
                IdeErrors.Write($"Vim mode: {VimMappingsPath} {line}");
            }
        }
    }

    private void FollowVim(CodeEditor editor)
    {
        editor.VimAsked += (_, request) => OnVimAsked(editor, request);
        editor.VimSaid += (_, said) => status.Say(said.Text, said.Error ? ProblemSeverity.Error : null);
        editor.VimJumped += (_, from) =>
        {
            if (editor == Editors.CodeEditor)
            {
                navigator.Jumped(from);
            }
        };
    }

    private void OnVimAsked(CodeEditor editor, VimRequest request)
    {
        Editors.Front(Avalonia.Controls.TopLevel.GetTopLevel(editor) ?? this);
        switch (request.Kind)
        {
            case VimRequestKind.Save:
                Request(A.Save);
                break;
            case VimRequestKind.SaveAll:
                Request(A.SaveAll);
                break;
            case VimRequestKind.Close:
                Request(A.CloseTab);
                break;
            case VimRequestKind.SaveAndClose:
                IdeErrors.Observe(SaveThenCloseAsync());
                break;
            case VimRequestKind.Open:
                OpenFile(request.Path!);
                break;
            case VimRequestKind.Reformat:
                // = refuses where Reformat Code would, in its words.
                if (actions!.WhyNot(A.Reformat, Context()) is { } refused)
                {
                    Say(refused, RefusalPlace(A.Reformat));
                }
                else if (editor == Editors.CodeEditor && Editors.Active is { } shown)
                {
                    Task reformatting = Assist.Actions.ReformatLinesAsync(shown, request.Lines);
                    // A command line's run waits for it before its next command, so the layout is part of its change.
                    if (vimRunWork is { } waited)
                    {
                        waited.Add(reformatting);
                    }
                    else
                    {
                        IdeErrors.Observe(reformatting);
                    }
                }
                break;
            case VimRequestKind.Action:
                RunNamed(request.Path!);
                break;
            case VimRequestKind.EachFile or VimRequestKind.EachEditor or VimRequestKind.EachTab or VimRequestKind.EachPlace or VimRequestKind.EachPlaceFile
                or VimRequestKind.Commands:
                IdeErrors.Observe(RunVimAsync(request.Kind, request.Path!));
                break;
            case VimRequestKind.Older or VimRequestKind.Newer:
                for (int i = 0; i < Math.Clamp(request.Count, 1, Navigation.NavigationHistory.Limit); i++)
                {
                    Request(request.Kind == VimRequestKind.Older ? A.Back : A.Forward);
                }
                break;
        }
    }

    /// <summary>What the commands of the command line Vim mode is running started that the next waits for (= laying out
    /// its lines); null while none runs.</summary>
    private List<Task>? vimRunWork;

    /// <summary>A stop of a command line's run: an open file, a split's editor, a place in a file, or the file shown as it is.</summary>
    private sealed record VimStop(OpenDocument? Document = null, EditorGroup? Group = null, ToolWindows.Usage? Place = null);

    /// <summary>:bufdo, :windo, :argdo, :cdo, :cfdo, and a line of commands joined by '|': its commands, parted at '|', run in
    /// turn in each open file, split, tab's file, place or file of the Find window's places (else Problems'), or in the file
    /// shown, each shown as they run there and each waiting for what the one before started, as one command of global
    /// undo whose change of each file is one step of that file's undo. The first command that fails ends it there, as
    /// Vim's does, and a file shown read-only, which every change refuses, is passed over and said.</summary>
    private async Task RunVimAsync(VimRequestKind kind, string line)
    {
        string named = kind switch
        {
            VimRequestKind.EachFile => ":bufdo",
            VimRequestKind.EachEditor => ":windo",
            VimRequestKind.EachTab => ":argdo",
            VimRequestKind.EachPlace => ":cdo",
            VimRequestKind.EachPlaceFile => ":cfdo",
            _ => ":",
        };
        if (vimRunWork is not null)
        {
            status.Say($"{named} runs inside another command line: Vim mode runs one at a time", ProblemSeverity.Error);
            return;
        }
        IReadOnlyList<string> commands = Vim.Bars(line);
        IReadOnlyList<VimStop> stops = VimStops(kind);
        if (stops.Count == 0)
        {
            status.Say($"{named} has no places to run at: the Find window shows no search, and Problems lists none", null);
            return;
        }
        List<string> passed = [];
        Dictionary<OpenDocument, long> marks = [];
        Dictionary<ToolWindows.Usage, Anchor> anchors = [];
        List<Task> work = vimRunWork = [];
        try
        {
            using CommandHistory.Recording recording = Editors.Commands.RecordWrites(named == ":" ? $":{line}" : $"{named} {line}");
            try
            {
                foreach (VimStop stop in stops)
                {
                    if (!ReachVimStop(stop, passed, anchors))
                    {
                        continue;
                    }
                    foreach (string command in commands)
                    {
                        if (Editors.Active is not { HoldsText: true } shown)
                        {
                            return;
                        }
                        if (marks.TryAdd(shown, shown.Text.UndoPlace))
                        {
                            recording.Watch(shown.Path);
                        }
                        VimMessage? said = Editors.CodeEditor.RunVimCommand(command);
                        while (work.Count > 0)
                        {
                            Task[] started = [.. work];
                            work.Clear();
                            await Task.WhenAll(started);
                        }
                        if (said is { Error: true })
                        {
                            return;
                        }
                    }
                }
            }
            finally
            {
                // Each file's change one step of its undo before the command takes it, as Vim's command line is one change.
                foreach ((OpenDocument document, long mark) in marks)
                {
                    document.Text.JoinUndoSince(mark);
                }
            }
        }
        finally
        {
            vimRunWork = null;
            foreach (Anchor anchor in anchors.Values)
            {
                anchor.Remove();
            }
        }
        if (passed.Count > 0)
        {
            status.Say($"{named} passed over {Plural.Of(passed.Count, "read-only file")}: {Plural.Listed(passed)}", null);
        }
    }

    /// <summary>Where a command line's run stops, in turn: the open files (:bufdo), the splits (:windo), the tabs' files in
    /// their order (:argdo), the Find window's places, else those Problems lists (:cdo, and their files' first for :cfdo),
    /// or the file shown alone.</summary>
    private IReadOnlyList<VimStop> VimStops(VimRequestKind kind)
    {
        switch (kind)
        {
            case VimRequestKind.EachFile:
                return [.. Editors.Documents.Where(d => d.HoldsText).Select(d => new VimStop(d))];
            case VimRequestKind.EachEditor:
                return [.. Editors.Groups.Where(g => g.Active is { HoldsText: true }).Select(g => new VimStop(g.Active, g))];
            case VimRequestKind.EachTab:
                return [.. Editors.Groups.SelectMany(g => g.Tabs).Select(t => t.Document).Where(d => d.HoldsText).Distinct().Select(d => new VimStop(d))];
            case VimRequestKind.EachPlace or VimRequestKind.EachPlaceFile:
                IReadOnlyList<ToolWindows.Usage> places = find?.Places is { Count: > 0 } found ? found
                    : [.. (Bottom.Problems.SolutionProblems ?? Bottom.Problems.FileProblems).Where(p => p.Path is not null)
                        .Select(p => new ToolWindows.Usage(p.Path!, p.Position.Line, p.Position.Column, 0, string.Empty, Passenger.Languages.CSharp.UsageKind.Other, string.Empty))];
                return [.. (kind == VimRequestKind.EachPlace ? places : places.DistinctBy(p => p.Path, PathNames.Comparer)).Select(p => new VimStop(Place: p))];
            default:
                return [new VimStop()];
        }
    }

    /// <summary>Shows a stop of a command line's run, its file opened where it is a place's, the caret at the place as the
    /// edits before left it; false where it is passed over, read-only.</summary>
    private bool ReachVimStop(VimStop stop, List<string> passed, Dictionary<ToolWindows.Usage, Anchor> anchors)
    {
        OpenDocument? document = stop.Document;
        if (stop.Place is { } place && (document = Editors.Find(place.Path)) is null && OpenFile(place.Path) is null)
        {
            document = Editors.Find(place.Path);
        }
        if (stop.Place is not null && document is null || document?.IsReadOnly == true)
        {
            if (document is not null && !passed.Contains(document.Name))
            {
                passed.Add(document.Name);
            }
            return false;
        }
        if (stop.Group is { } group)
        {
            Editors.SetActiveGroup(group);
            group.FocusShown();
        }
        else if (document is not null)
        {
            Editors.Activate(document);
        }
        if (stop.Place is { } at && document is not null)
        {
            if (!anchors.TryGetValue(at, out Anchor? held))
            {
                // Every place in the file held as it first comes, so the edits at one move the rest as Vim's list moves.
                TextSnapshot text = document.Text.Snapshot;
                foreach (ToolWindows.Usage other in find?.Places.Where(p => PathNames.Same(p.Path, at.Path)) ?? [])
                {
                    anchors.TryAdd(other, document.Text.AnchorAt(OffsetOf(text, other), AnchorGravity.Before));
                }
                held = anchors.TryGetValue(at, out Anchor? made) ? made : anchors[at] = document.Text.AnchorAt(OffsetOf(text, at), AnchorGravity.Before);
            }
            TextSnapshot now = document.Text.Snapshot;
            int offset = Math.Min(held.Offset, now.Length);
            int line = now.GetLineFromOffset(offset);
            Editors.CodeEditor.GoTo(new TextPosition(line, offset - now.GetLineStart(line)));
        }
        return true;

        static int OffsetOf(TextSnapshot text, ToolWindows.Usage place)
        {
            int line = Math.Clamp(place.Line, 0, Math.Max(0, text.LineCount - 1));
            LineSpan span = text.GetLine(line);
            return span.Start + Math.Clamp(place.Column, 0, span.Length);
        }
    }

    /// <summary>:action: the IDE's action of the id given, else the one whose title is the words given, run as its keys
    /// run it; one that cannot run says why, as its keys would.</summary>
    private void RunNamed(string named)
    {
        string words = named.Trim().TrimEnd('…', '.');
        string id = actions![named.Trim()] is { } byId ? byId.Id
            : actions.All.FirstOrDefault(a => string.Equals(a.Title.TrimEnd('…'), words, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Title.TrimEnd('…').Replace(" ", string.Empty, StringComparison.Ordinal), words, StringComparison.OrdinalIgnoreCase))?.Id ?? named.Trim();
        Request(id);
    }

    /// <summary>:wq and :x: the file saved as Save saves it where it changed, then its tab closed.</summary>
    private async Task SaveThenCloseAsync()
    {
        if (Editors.Active is { IsModified: true })
        {
            await SaveExplicitly(all: false);
        }
        Request(A.CloseTab);
    }
}
