using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Inspector.WinUI;

/// <summary>The inspector inside a WinUI app, started by the startup hook and served as every framework's is
/// (<see cref="Serving"/>): it waits for the app's Application, has WinUI's XAML diagnostics hand it their service (through
/// <see cref="Tap"/>, which .NET's COM host beside it makes), is told of every element of every window from then on
/// (<see cref="VisualTree"/>), and answers on the UI thread WinUI names.</summary>
public static partial class Agent
{
    /// <summary>How often it looks for the app's Application while the app sets itself up (<see cref="Looking"/>).</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    /// <summary>How long WinUI has to hand the TAP its site, and the app to show its first window.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>The name WinUI's diagnostics are asked under, as its own tools name theirs.</summary>
    private const string Endpoint = "WinUIVisualDiagConnection1";

    /// <param name="project">The launched project's folder (<see cref="Wire.ProjectVariable"/>), or null.</param>
    public static void Start(string socket, string? project) => Serving.Start(socket, new WinUIUi(project));

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string module);

    /// <summary>WinUI's own entry to its XAML diagnostics, in the app's Windows App SDK.</summary>
    [LibraryImport("Microsoft.Internal.FrameworkUdk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int InitializeXamlDiagnosticsEx(string endPoint, uint pid, string xamlDll, string tap, Guid tapClass, string initialization);

    /// <summary>WinUI and the Windows App SDK it comes in, as the window says them ("WinUI 3, Windows App SDK 2.7.0"): the
    /// SDK's version as the app's dependencies file names its package, as WinUI's own assemblies carry WinUI 3's.</summary>
    private static string Framework()
    {
        foreach (string file in (AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (SdkPackage().Match(File.ReadAllText(file)) is { Success: true } found)
                {
                    return $"WinUI 3, Windows App SDK {found.Groups["version"].Value}";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                InspectorTrace.Step($"{file} could not be read: {error.Message}");
            }
        }
        return "WinUI 3";
    }

    [GeneratedRegex(@"""Microsoft\.WindowsAppSDK/(?<version>[^""]+)""")]
    private static partial Regex SdkPackage();

    /// <summary>A WinUI app as the serve loop reaches it.</summary>
    private sealed class WinUIUi(string? project) : IAppUi
    {
        private UiQueue? queue;
        private Inspector? inspector;

        public void Listen(Connection connection) => BindingFailures.Start(connection);

        public void Open(Connection connection)
        {
            using Looking looking = new(Look);
            while (!XamlLoaded() || !HasApplication())
            {
                looking.Wait();
            }
            InspectorTrace.Step("the app has its Application");
            Diagnostics diagnostics = Connect();
            object dispatcher = Projected.Of(diagnostics.DispatcherQueue()) ?? throw new InvalidOperationException("WinUI's diagnostics named no UI thread");
            UiQueue ui = new(dispatcher);
            VisualTree tree = new();
            Inspector made = ui.Invoke(() =>
            {
                Inspector inspecting = new(connection, diagnostics, tree, ui, project);
                Bindings.Inspecting(inspecting);
                Bindings.Start();
                return inspecting;
            });
            diagnostics.AdviseVisualTreeChange(Marshal.GetComInterfaceForObject(tree, typeof(IVisualTreeServiceCallback)));
            InspectorTrace.Step($"told of {tree.Roots.Count} top level(s)");
            ui.Post(Bindings.TreeKnown);
            Deadline until = Deadline.In(Patience);
            while (!until.Passed && !made.HasWindow())
            {
                looking.Wait();
            }
            (queue, inspector) = (ui, made);
        }

        /// <summary>Whether WinUI's own XAML is loaded in the process, as the app's Application.Start loads it once its Main has set
        /// up C#/WinRT: before then nothing of the projection is touched, as a call from this thread would set it up first.</summary>
        private static bool XamlLoaded() => GetModuleHandle("Microsoft.ui.xaml.dll") != 0;

        /// <summary>Whether the app has made its Application; not yet while WinUI's classes can't be activated.</summary>
        private static bool HasApplication()
        {
            try
            {
                return Projected.Static("Microsoft.UI.Xaml.Application", "Current") is not null;
            }
            catch (TargetInvocationException early) when (early.InnerException is COMException or InvalidCastException or TypeInitializationException or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>WinUI's diagnostics for this process, whose site the TAP hands over once WinUI makes it.</summary>
        private static Diagnostics Connect()
        {
            using ManualResetEventSlim handed = new();
            nint site = 0;
            AppContext.SetData(Tap.SiteKey, new Action<nint>(given =>
            {
                site = given;
                handed.Set();
            }));
            string folder = Path.GetDirectoryName(typeof(Agent).Assembly.Location)!;
            string host = Path.Combine(folder, Path.GetFileNameWithoutExtension(typeof(Agent).Assembly.Location) + ".comhost.dll");
            int done = InitializeXamlDiagnosticsEx(Endpoint, (uint)Environment.ProcessId, "Microsoft.UI.Xaml.dll", host, new Guid(Tap.Id), string.Empty);
            InspectorTrace.Step($"asked WinUI's diagnostics for this app through {host}: 0x{done:X8}");
            Marshal.ThrowExceptionForHR(done);
            if (!handed.Wait(Patience))
            {
                throw new TimeoutException($"WinUI's diagnostics made no TAP within {Patience.TotalSeconds:0} s");
            }
            return new Diagnostics(site);
        }

        public Hello Hello(string? problem) => new(Wire.Version, Environment.ProcessId, null, problem) { Framework = Framework() };

        public Task<Reply> AnswerAsync(Request request) => request.Method == Wire.Capture
            ? Pictures.TakeAsync(inspector!, queue!, request)
            : queue!.Run(() => inspector!.Handle(request));

        public void Close()
        {
            if (inspector is { } made && queue is { } ui)
            {
                ui.Post(made.Stop);
            }
        }
    }
}

/// <summary>The app's UI thread, reached through the DispatcherQueue WinUI's diagnostics name, its TryEnqueue called by
/// reflection with a handler of the app's own delegate type.</summary>
internal sealed class UiQueue(object dispatcher)
{
    private readonly MethodInfo enqueue = dispatcher.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m => m.Name == "TryEnqueue" && m.GetParameters().Length == 1 && typeof(Delegate).IsAssignableFrom(m.GetParameters()[0].ParameterType));

    /// <summary>Whether the calling thread is the UI thread.</summary>
    public bool HasAccess => Projected.Get(dispatcher, "HasThreadAccess") is true;

    /// <summary>Runs work on the UI thread, its result or its failure the task's.</summary>
    public Task<T> Run<T>(Func<T> work)
    {
        TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception error)
            {
                // Handed to the caller, whose task it fails.
                done.SetException(error);
            }
        });
        return done.Task;
    }

    /// <summary>Runs work on the UI thread and waits for it, from a thread that is not it.</summary>
    public T Invoke<T>(Func<T> work)
    {
        if (HasAccess)
        {
            return work();
        }
        using ManualResetEventSlim done = new();
        T? result = default;
        Exception? failed = null;
        Post(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                // Handed to the waiting caller, which throws it.
                failed = error;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (failed is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        }
        return result!;
    }

    /// <summary>Queues work for the UI thread.</summary>
    public void Post(Action work)
    {
        Delegate handler = Projected.Handler(enqueue.GetParameters()[0].ParameterType, work);
        if (enqueue.Invoke(dispatcher, [handler]) is not true)
        {
            throw new InvalidOperationException("The app's UI thread takes no more work");
        }
    }
}
