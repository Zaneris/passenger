using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>What Vim mode asks of the window: :w, :q, :wq and :e as Save, Close Tab and opening a file, Ctrl+O and Ctrl+I
/// as Back and Forward, its jumps kept among the places Back goes to, and its words in the status bar.</summary>
public partial class MainWindow
{
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
            case VimRequestKind.Older or VimRequestKind.Newer:
                for (int i = 0; i < Math.Clamp(request.Count, 1, Navigation.NavigationHistory.Limit); i++)
                {
                    Request(request.Kind == VimRequestKind.Older ? A.Back : A.Forward);
                }
                break;
        }
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
