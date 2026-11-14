using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>What Vim mode asks of the window: :w, :q, :wq and :e as Save, Close Tab and opening a file, Ctrl+O and Ctrl+I
/// as Back and Forward, = as Reformat Code over its lines, :action as any action of the IDE, its jumps kept among the
/// places Back goes to, and its words in the status bar.</summary>
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
                    IdeErrors.Observe(Assist.Actions.ReformatLinesAsync(shown, request.Lines));
                }
                break;
            case VimRequestKind.Action:
                RunNamed(request.Path!);
                break;
            case VimRequestKind.Older or VimRequestKind.Newer:
                for (int i = 0; i < Math.Clamp(request.Count, 1, Navigation.NavigationHistory.Limit); i++)
                {
                    Request(request.Kind == VimRequestKind.Older ? A.Back : A.Forward);
                }
                break;
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
