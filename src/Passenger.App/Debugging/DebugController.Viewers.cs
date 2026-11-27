using Avalonia.Controls;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Dap;

namespace Passenger.App.Debugging;

public sealed partial class DebugController
{
    private Window? valueViewer;

    private void OpenValueViewer(Window owner, VariableNode node, DapStackFrame? at)
    {
        DebugSession? session = Session;
        CancellationToken stop = stopShown.Token;
        bool StillPaused() => !stop.IsCancellationRequested && Session == session && session?.State == DebugState.Paused;

        Window window;
        if (node.IsString && StringValue(node.Value) is { } text)
        {
            Func<string, Task<bool>>? save = StillPaused() && at is not null && node.Settable is not null
                ? changed => StillPaused() ? SetValue(node, SymbolDisplay.FormatLiteral(changed, quote: true), at) : Task.FromResult(false)
                : null;
            window = new VariableTextWindow(node.Name, text, save);
        }
        else if (node.IsTable && session is { Engine: DebugEngine.Managed } && StillPaused())
        {
            window = new VariableTableWindow(node.Name, (start, count, index) => node.Reference > 0
                ? session.VariableTableAsync(new VariableTableArguments(node.Reference, start, count, index))
                : Task.FromResult(new VariableTableResponse([], ["Value"], 0, [])));
        }
        else
        {
            return;
        }
        valueViewer?.Close();
        valueViewer = window;
        CancellationTokenRegistration gone = stop.Register(window.Close);
        window.Closed += (_, _) =>
        {
            gone.Dispose();
            if (valueViewer == window)
            {
                valueViewer = null;
            }
        };
        window.Show(owner);
    }
}
