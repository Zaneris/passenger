using System.Reflection;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>The inspector inside a Windows Forms app, started by the startup hook and served as every framework's is
/// (<see cref="Serving"/>): it waits for the app's first form with a window, takes the UI thread's synchronization context
/// there (Windows Forms' own, which outlives any one form), and answers each request on that thread.</summary>
public static class Agent
{
    /// <summary>How often it looks for the app's first form while the app sets itself up (<see cref="Looking"/>).</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    /// <summary>How long an answer to input waits for what it set going in an app whose UI thread is never idle.</summary>
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(1);

    public static void Start(string socket) => Serving.Start(socket, new FormsUi());

    /// <summary>Windows Forms as the window names it, with the version of the WindowsDesktop runtime the app runs ("Windows
    /// Forms 10.0.1").</summary>
    private static string Framework()
    {
        Assembly forms = typeof(Form).Assembly;
        string? version = forms.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return $"Windows Forms {(plus > 0 ? version![..plus] : version ?? forms.GetName().Version?.ToString(3))}";
    }

    /// <summary>The app's first form with a window, read off its UI thread; null while it has none.</summary>
    private static Form? FirstForm()
    {
        try
        {
            foreach (Form form in Application.OpenForms)
            {
                if (form.IsHandleCreated && !form.IsDisposed)
                {
                    return form;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // A form opened or closed as the list was read: looked at again.
        }
        return null;
    }

    /// <summary>A Windows Forms app as the serve loop reaches it.</summary>
    private sealed class FormsUi : IAppUi
    {
        private SynchronizationContext? ui;
        private Inspector? inspector;

        public void Listen(Connection connection)
        {
            // Windows Forms tells of no failed binding as XAML's frameworks do: there is nothing to hear before Main.
        }

        public void Open(Connection connection)
        {
            using Looking looking = new(Look);
            while (true)
            {
                if (FirstForm() is { } form)
                {
                    try
                    {
                        (SynchronizationContext context, Inspector made) = ((SynchronizationContext, Inspector))form.Invoke(() =>
                            (SynchronizationContext.Current ?? throw new InvalidOperationException("The app's UI thread has no synchronization context"), new Inspector(connection)));
                        InspectorTrace.Step($"made on the UI thread, with {made.TopLevels().Count} top level(s)");
                        (ui, inspector) = (context, made);
                        return;
                    }
                    catch (Exception closed) when (closed is InvalidOperationException or ObjectDisposedException)
                    {
                        InspectorTrace.Step($"the first form went before the inspector was made there: {closed.Message}");
                    }
                }
                looking.Wait();
            }
        }

        public Hello Hello(string? problem) => new(Wire.Version, Environment.ProcessId, null, problem) { Framework = Framework() };

        public async Task<Reply> AnswerAsync(Request request)
        {
            SynchronizationContext thread = ui!;
            Inspector served = inspector!;
            Reply reply = await OnUi(thread, () => served.Handle(request));
            if (request.Method == Wire.Input && reply.Done is { } said)
            {
                // Once the messages the input posted have run, within a moment, the answer says what they came to.
                Task<InputDone?> took = OnUi(thread, () => served.Took(said));
                if (await Task.WhenAny(took, Task.Delay(Busy)) == took)
                {
                    return reply with { Done = await took ?? said };
                }
            }
            return reply;
        }

        public void Close()
        {
            if (inspector is { } made)
            {
                ui?.Post(_ => made.Stop(), null);
            }
        }

        /// <summary>Work on the UI thread, after what its queue holds already.</summary>
        private static Task<T> OnUi<T>(SynchronizationContext thread, Func<T> work)
        {
            TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            thread.Post(_ =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception error)
                {
                    done.SetException(error);
                }
            }, null);
            return done.Task;
        }
    }
}
