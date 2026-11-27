using System.Reflection;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Controls.Xaml.Diagnostics;

namespace Passenger.Inspector.Maui;

/// <summary>What MAUI's XAML Incremental Hot Reload did in the app, as MAUI's own diagnostics say it (HotReloadDiagnostics):
/// each update its handler was given, in turn, with the types whose XAML it patched and each open instance whose patch
/// failed. A XAML file's change reaches a MAUI app as the code MAUI's source generator writes of it, in the program's C#
/// update, so what it came to is read from the update after the ones heard before it.</summary>
internal sealed class XamlUpdates : IDisposable
{
    /// <summary>MAUI's switch for XAML Incremental Hot Reload, which a Debug build's EnableMauiIncrementalHotReload turns on.</summary>
    private const string Switch = "Microsoft.Maui.RuntimeFeature.IsIncrementalHotReloadEnabled";

    /// <summary>How long MAUI may take to say what an update came to once it was applied: it patches on the UI thread.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly Lock gate = new();
    private readonly List<Update> heard = [];

    /// <summary>The instances whose patch failed, by the version MAUI gave the update, until it says the update applied.</summary>
    private readonly Dictionary<int, List<(Type Type, Exception Error)>> failing = [];

    /// <summary>Waited for while no update after those asked of has come.</summary>
    private TaskCompletionSource? arrived;

    public XamlUpdates()
    {
        HotReloadDiagnostics.UpdateRequested += OnRequested;
        HotReloadDiagnostics.UpdateFailed += OnFailed;
        HotReloadDiagnostics.UpdateApplied += OnApplied;
        HotReloadDiagnostics.UpdateSkipped += OnSkipped;
    }

    /// <summary>How many updates MAUI's handler has been given.</summary>
    public int Heard
    {
        get
        {
            lock (gate)
            {
                return heard.Count;
            }
        }
    }

    /// <summary>What a XAML file's change came to: the first update after the <see cref="XamlChange.Heard"/> ones, once MAUI
    /// said it was done, read for its class (<see cref="XamlChange.Class"/>), whose open instances <paramref name="open"/>
    /// names on the UI thread, given the change where MAUI's patch does not bring them to it (<see cref="XamlChange.Whole"/>),
    /// so each is made again.</summary>
    public async Task<XamlReloaded> ReloadedAsync(XamlChange change, Func<Type, XamlChange?, Task<(IReadOnlyList<string> Objects, string? Refused)>> open)
    {
        string file = Path.GetFileName(change.File);
        if (!AppContext.TryGetSwitch(Switch, out bool on) || !on)
        {
            return Refused($"{file} went into the app's code, but it is built without MAUI's XAML Incremental Hot Reload (EnableMauiIncrementalHotReload), "
                + "so only what it makes from now shows the change");
        }
        Update? update = null;
        using CancellationTokenSource limit = new(Limit);
        while (update is null)
        {
            Task next;
            lock (gate)
            {
                update = heard.Count > (change.Heard ?? 0) ? heard[change.Heard ?? 0] : null;
                next = update?.Ended.Task ?? (arrived ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            try
            {
                await next.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                return Refused($"{file} went into the app's code, but MAUI did not say within {Limit.TotalSeconds:0} s what it made of it");
            }
        }
        Type? type = TypeOf(change, update.Updated);
        if (type is null)
        {
            // The change wrote no other code of its type: what is open of it shows the file as it is.
            Type? unchanged = TypeOf(change, AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).SelectMany(a => a.GetCustomAttributes<XamlResourceIdAttribute>())
                .Select(id => id.Type).OfType<Type>());
            return unchanged is null ? new XamlReloaded([], null, []) : Taken(await open(unchanged, null));
        }
        if (change.Whole == true)
        {
            return Taken(await open(type, change));
        }
        if (!update.Handled.Contains(type))
        {
            return Refused($"MAUI cannot patch an open {Words.TypeName(type)} with this change to {file}: restart the app, or open a new one, to see it");
        }
        if (update.Failed.FirstOrDefault(f => f.Type == type) is { Error: { } error })
        {
            return Refused($"{file} could not be patched into the open {Words.TypeName(type)}: {error.GetBaseException().Message}");
        }
        (IReadOnlyList<string> objects, _) = await open(type, null);
        return new XamlReloaded(objects.Count > 0 || update.Instances == 0 ? objects : [Words.TypeName(type)], null, []);
    }

    /// <summary>The type of those given that a XAML file's code is of: the one its build gave the file's path in the project
    /// (XamlResourceId, a resource dictionary's with no x:Class too), else the one its x:Class names.</summary>
    private static Type? TypeOf(XamlChange change, IEnumerable<Type> types)
    {
        List<Type> among = [.. types];
        foreach (Assembly assembly in among.Select(t => t.Assembly).Distinct())
        {
            foreach (XamlResourceIdAttribute id in assembly.GetCustomAttributes<XamlResourceIdAttribute>())
            {
                if (id.Type is { } type && among.Contains(type) && id.Path.Replace('\\', '/') == change.Uri)
                {
                    return type;
                }
            }
        }
        return among.FirstOrDefault(t => t.FullName == change.Class);
    }

    private static XamlReloaded Taken((IReadOnlyList<string> Objects, string? Refused) open) => new(open.Refused is null ? open.Objects : [], open.Refused, []);

    private static XamlReloaded Refused(string why) => new([], why, []);

    private void OnRequested(object? sender, HotReloadRequestedEventArgs args)
    {
        lock (gate)
        {
            heard.Add(new Update(args.UpdatedTypes, args.HandledTypes));
            arrived?.TrySetResult();
            arrived = null;
        }
    }

    private void OnFailed(object? sender, HotReloadErrorEventArgs args)
    {
        lock (gate)
        {
            if (!failing.TryGetValue(args.Version, out List<(Type, Exception)>? failed))
            {
                failing[args.Version] = failed = [];
            }
            failed.Add((args.UpdatedType, args.Exception));
        }
    }

    private void OnApplied(object? sender, HotReloadAppliedEventArgs args)
    {
        lock (gate)
        {
            if (Of(args.UpdatedTypes) is { } update)
            {
                update.Instances = args.InstanceCount;
                update.Failed = failing.Remove(args.ToVersion, out List<(Type, Exception)>? failed) ? failed : [];
                update.Ended.TrySetResult();
            }
        }
    }

    private void OnSkipped(object? sender, HotReloadSkippedEventArgs args)
    {
        lock (gate)
        {
            if (Of(args.UpdatedTypes) is { } update)
            {
                update.Instances = 0;
                update.Ended.TrySetResult();
            }
        }
    }

    /// <summary>The update MAUI's word is of: the one given those very types, else the first it has not said is done.</summary>
    private Update? Of(IReadOnlyList<Type> types) =>
        heard.FirstOrDefault(u => ReferenceEquals(u.Updated, types) && !u.Ended.Task.IsCompleted) ?? heard.FirstOrDefault(u => !u.Ended.Task.IsCompleted);

    public void Dispose()
    {
        HotReloadDiagnostics.UpdateRequested -= OnRequested;
        HotReloadDiagnostics.UpdateFailed -= OnFailed;
        HotReloadDiagnostics.UpdateApplied -= OnApplied;
        HotReloadDiagnostics.UpdateSkipped -= OnSkipped;
    }

    /// <summary>An update MAUI's handler was given: the types it changed, those of them whose XAML it patches, and once done how
    /// many open instances it patched and each whose patch failed.</summary>
    private sealed class Update(IReadOnlyList<Type> updated, IReadOnlyList<Type> handled)
    {
        public IReadOnlyList<Type> Updated { get; } = updated;

        public IReadOnlyList<Type> Handled { get; } = handled;

        public int Instances { get; set; }

        public List<(Type Type, Exception Error)> Failed { get; set; } = [];

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
