using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App.Debugging;
using Passenger.Dap;

namespace Passenger.App.ToolWindows;

/// <summary>The selected debug session's memory, address expression and selected byte.</summary>
public sealed class DebugMemoryView : DockPanel
{
    private const int PageSize = 256;
    private const string AddressPrompt = "Enter a hexadecimal address or an address expression";
    private readonly DebugModel model;
    private readonly TextBox address = new() { Name = "memoryAddress", PlaceholderText = "0x… or an address expression", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button go = new() { Content = "Go" };
    private readonly CheckBox follow = new() { Content = "Follow", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button previous = ToolButtons.Of(IconKeys.ArrowUp, "Previous 256 bytes");
    private readonly Button next = ToolButtons.Of(IconKeys.ArrowDown, "Next 256 bytes");
    private readonly TextBlock status = new() { Classes = { StyleClasses.Muted }, Margin = new Thickness(Gaps.Normal, Gaps.Tight), TextWrapping = TextWrapping.Wrap };
    private readonly EmptyState empty = new();
    private readonly MemoryByteGrid grid = new();
    private readonly MemoryDataInspector inspector = new();
    private readonly PaneSplit data;
    private readonly ConditionalWeakTable<DebugSession, Location> locations = new();
    private DebugSession? session;
    private Location? location;
    private (int Stop, int? Id) frame;
    private (int Stop, int? Frame)? automatic;
    private ReadMemoryResponse? shown;
    private int version;
    private bool choosing;

    public DebugMemoryView(DebugModel model)
    {
        this.model = model;
        AutomationProperties.SetName(address, "Memory address");
        ToolTip.SetTip(follow, "Resolve the address expression again when the program stops");
        address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                IdeErrors.Observe(GoAsync());
                e.Handled = true;
            }
        };
        go.Click += (_, _) => IdeErrors.Observe(GoAsync());
        follow.IsCheckedChanged += (_, _) =>
        {
            if (!choosing && location is { } current)
            {
                current.Follow = follow.IsChecked == true;
            }
        };
        previous.Click += (_, _) => IdeErrors.Observe(MoveAsync(-PageSize));
        next.Click += (_, _) => IdeErrors.Observe(MoveAsync(PageSize));
        ToolFlow fields = new() { Margin = new Thickness(Gaps.Small, Gaps.Tight), Spacing = Gaps.Small };
        fields.Children.AddRange([new TextBlock { Text = "Address", VerticalAlignment = VerticalAlignment.Center }, address,
            go, follow, ToolBars.Rule(), previous, next]);
        ToolFlow.SetFills(address, new ToolLayout.Fills(140));
        Children.Add(ToolBars.Top(fields));
        SetDock(status, Dock.Bottom);
        Children.Add(status);
        data = new PaneSplit(grid, inspector, "3*,1,*", "2*,1,*");
        Children.Add(new Panel { Children = { data, empty } });

        grid.ByteSelected += (_, selected) =>
        {
            if (location is { } current)
            {
                current.Selected = selected;
                Inspect();
            }
        };
        inspector.WidthSelected += (_, width) => grid.MarkWidth(width);
        model.ToolsChanged += (_, _) => UpdateStop();
        model.MovedChanged += (_, _) => UpdateStop();
        model.FramesShown += (_, _) => FrameShown(model.SelectedFrame?.Id);
        model.FrameChosen += (_, selected) => FrameShown(selected.Id);
        AttachedToVisualTree += (_, _) => UiThread.Post(UpdateStop);
        Say("Start a .NET debug session to inspect memory");
    }

    internal void FocusAddress()
    {
        address.Focus();
        address.SelectAll();
    }

    internal Task OpenAsync(string reference, string? expression)
    {
        UpdateStop(refresh: false);
        // A managed reference's target can move after the Variables row was read.
        address.Text = expression ?? reference;
        return GoAsync();
    }

    internal void UpdateStop() => UpdateStop(refresh: true);

    private void UpdateStop(bool refresh)
    {
        DebugSession? chosen = model.Sessions.FirstOrDefault(tab => tab.IsChosen)?.Session;
        if (session != chosen)
        {
            if (session is not null)
            {
                session.StateChanged -= OnSessionChanged;
            }
            session = chosen;
            location = chosen is null ? null : locations.GetOrCreateValue(chosen);
            frame = default;
            automatic = null;
            version++;
            shown = null;
            choosing = true;
            address.Text = location?.Input;
            follow.IsChecked = location?.Follow ?? true;
            choosing = false;
            Say(AddressPrompt);
            if (chosen is not null)
            {
                chosen.StateChanged += OnSessionChanged;
            }
        }
        string? unavailable = Unavailable;
        address.IsEnabled = go.IsEnabled = follow.IsEnabled = unavailable is null;
        if (unavailable is not null)
        {
            automatic = null;
            version++;
            Say(unavailable);
            return;
        }
        if (location?.Input is not { Length: > 0 })
        {
            Say(AddressPrompt);
            return;
        }
        if (!refresh || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }
        bool resolve = ShouldResolve();
        if (resolve && frame.Stop != session!.Stops)
        {
            Say("Reading the current stack frame…");
            return;
        }
        if (automatic != StopKey(resolve))
        {
            MarkAutomatic();
            IdeErrors.Observe(ReadAsync(resolve));
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => UpdateStop();

    private void FrameShown(int? id)
    {
        UpdateStop(refresh: false);
        frame = (session?.Stops ?? 0, id);
        UpdateStop();
    }

    private string? Unavailable => session switch
    {
        null or { State: DebugState.Ended } => "Start a .NET debug session to inspect memory",
        { State: DebugState.Starting } => "The debugger is starting",
        { ReadsMemory: false } => "The Memory tab is available for local .NET debug sessions",
        { State: not DebugState.Paused } => "Pause the program to inspect memory",
        _ => model.ShownSnapshot is null ? null : "Return to the live stop to inspect memory",
    };

    private bool ShouldResolve() => location is { Input: { Length: > 0 } input } current && !IsLiteral(input) && (current.Follow || current.Page is null);

    private static bool IsLiteral(string input) => input.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

    private (int Stop, int? Frame) StopKey(bool resolve) => (session!.Stops, resolve ? frame.Id : null);

    private void MarkAutomatic() => automatic = StopKey(ShouldResolve());

    private async Task GoAsync()
    {
        if (location is not { } current || Unavailable is not null)
        {
            return;
        }
        current.Input = address.Text?.Trim();
        current.Page = current.Origin = null;
        current.Selected = 0;
        MarkAutomatic();
        await ReadAsync(ShouldResolve());
    }

    private Task MoveAsync(int distance)
    {
        if (location is not { Page: ulong page } current || Unavailable is not null)
        {
            return Task.CompletedTask;
        }
        if (!MemoryAddresses.TryOffset(page, distance, out ulong moved) && distance > 0)
        {
            return Task.CompletedTask;
        }
        current.Page = moved;
        current.Selected = 0;
        return ReadAsync(resolve: false);
    }

    private async Task ReadAsync(bool resolve)
    {
        if (Unavailable is not null || session is not { } paused || location is not { } current)
        {
            return;
        }
        int asked = ++version;
        int stop = paused.Stops;
        Say("Reading memory…");
        try
        {
            string? input = current.Input;
            if (resolve)
            {
                if (frame.Stop != stop || frame.Id is not int id || input is null)
                {
                    Say("Choose a live stack frame to resolve the address");
                    return;
                }
                input = (await paused.ResolveMemoryAsync(input, id)).Address;
                if (!StillCurrent())
                {
                    return;
                }
            }
            ulong? origin = !resolve && current.Origin is ulong known ? known
                : MemoryAddresses.TryParse(input, out ulong parsed) ? parsed : null;
            if (origin is not ulong at)
            {
                Say(AddressPrompt);
                return;
            }
            if (current.Origin != at || current.Page is null)
            {
                current.Selected = 0;
                current.Page = at;
            }
            current.Origin = at;
            ulong page = current.Page.Value;
            string reference = MemoryAddresses.Show(page);
            ReadMemoryResponse reply = await paused.ReadMemoryAsync(reference, PageSize);
            if (!StillCurrent())
            {
                return;
            }
            previous.IsEnabled = page > 0;
            next.IsEnabled = MemoryAddresses.TryOffset(page, PageSize, out _);
            if (reply.Data.Length == 0)
            {
                Say($"The debugger could not read memory at {reference}", leavePaging: true);
                return;
            }
            shown = reply;
            current.Selected = Math.Min(current.Selected, reply.Data.Length - 1);
            bool[] changed = current.Remember(stop, page, reply.Data);
            grid.Show(page, reply.Data, changed, current.Selected);
            empty.IsVisible = false;
            data.IsVisible = true;
            string unread = reply.UnreadableBytes is > 0 ? $" · {reply.UnreadableBytes} unreadable bytes" : string.Empty;
            int differences = changed.Count(value => value);
            string changes = differences > 0 ? $" · {differences} changed since the previous stop" : string.Empty;
            status.Text = $"Resolved {MemoryAddresses.Show(at)}{changes}{unread}";
            status.IsVisible = true;
            Inspect();
        }
        catch (DapException error)
        {
            if (StillCurrent())
            {
                Say(error.Reason);
            }
        }

        bool StillCurrent() => asked == version && paused.State == DebugState.Paused && paused.Stops == stop;
    }

    private void Inspect()
    {
        if (shown is { } page && location is { Page: ulong addressOfPage } current)
        {
            inspector.Show(addressOfPage + (ulong)current.Selected, page.Data.AsSpan(current.Selected), page.PointerSize, page.LittleEndian);
        }
    }

    private void Say(string words, bool leavePaging = false)
    {
        shown = null;
        empty.Text = words;
        empty.IsVisible = true;
        data.IsVisible = status.IsVisible = false;
        if (!leavePaging)
        {
            previous.IsEnabled = next.IsEnabled = false;
        }
    }

    private sealed class Location
    {
        private const int MostPages = 16;
        private int stop;
        private List<(ulong Address, byte[] Bytes)> last = [];
        private List<(ulong Address, byte[] Bytes)> current = [];
        public string? Input { get; set; }
        public bool Follow { get; set; } = true;
        public ulong? Origin { get; set; }
        public ulong? Page { get; set; }
        public int Selected { get; set; }

        public bool[] Remember(int now, ulong address, byte[] bytes)
        {
            if (stop != now)
            {
                last = stop + 1 == now ? current : [];
                current = [];
                stop = now;
            }
            bool[] changed = new bool[bytes.Length];
            foreach ((ulong previousAddress, byte[] previousBytes) in last)
            {
                ulong start = Math.Max(address, previousAddress);
                ulong end = Math.Min(address + (ulong)(bytes.Length - 1), previousAddress + (ulong)(previousBytes.Length - 1));
                if (start > end)
                {
                    continue;
                }
                int count = (int)(end - start) + 1;
                int at = (int)(start - address);
                int before = (int)(start - previousAddress);
                for (int i = 0; i < count; i++)
                {
                    changed[at + i] = bytes[at + i] != previousBytes[before + i];
                }
            }
            current.RemoveAll(page => page.Address == address);
            if (current.Count == MostPages)
            {
                current.RemoveAt(0);
            }
            current.Add((address, bytes));
            return changed;
        }
    }
}
