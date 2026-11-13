using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Passenger.Text;

namespace Passenger.Editor;

public sealed partial class CodeEditor
{
    private TopLevel? navigationKeys;
    private Point? navigationPoint;
    private KeyModifiers navigationModifiers;
    private Debounce? navigationWait;
    private CancellationTokenSource? navigationCancel;
    private (string Action, int Offset)? navigationAsked;
    private TextRange? navigationRange;

    /// <summary>Whether a modified left click's action can navigate from a character: the navigable range, or null.
    /// The host checks the action and language; the editor debounces requests and discards answers after input changes.</summary>
    public Func<string, int, CancellationToken, Task<TextRange?>>? NavigationTarget { get; set; }

    private void AttachNavigationKeys()
    {
        navigationKeys = TopLevel.GetTopLevel(this);
        navigationKeys?.AddHandler(KeyDownEvent, NavigationKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        navigationKeys?.AddHandler(KeyUpEvent, NavigationKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (navigationKeys is Window window)
        {
            window.Deactivated += NavigationEnded;
        }
        ScrollChanged += NavigationEnded;
    }

    private void DetachNavigationKeys()
    {
        navigationKeys?.RemoveHandler(KeyDownEvent, NavigationKey);
        navigationKeys?.RemoveHandler(KeyUpEvent, NavigationKey);
        if (navigationKeys is Window window)
        {
            window.Deactivated -= NavigationEnded;
        }
        ScrollChanged -= NavigationEnded;
        navigationKeys = null;
        navigationPoint = null;
        StopNavigationHover();
    }

    private void NavigationEnded(object? sender, EventArgs e)
    {
        StopNavigationHover();
        if (sender is Window)
        {
            navigationPoint = null;
        }
    }

    private void NavigationKey(object? sender, KeyEventArgs e)
    {
        if (navigationPoint is null || NavigationTarget is null)
        {
            return;
        }
        KeyModifiers key = Keystroke.Alone(e.Key, KeyModifiers.None)?.Modifiers ?? KeyModifiers.None;
        navigationModifiers = e.RoutedEvent == KeyDownEvent ? e.KeyModifiers | key : e.KeyModifiers & ~key;
        UpdateNavigationHover();
    }

    private void MoveNavigationPointer(PointerEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        navigationPoint = point.Position;
        navigationModifiers = e.KeyModifiers;
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            StopNavigationHover();
            return;
        }
        UpdateNavigationHover();
    }

    private void UpdateNavigationHover()
    {
        string? action = navigationModifiers == KeyModifiers.None ? null :
            GesturesKeymap?.ActionFor(new MouseShortcut(MouseButton.Left, navigationModifiers));
        if (NavigationTarget is null || action is null || drag.Mode != DragMode.None || navigationPoint is not { } point ||
            PlaceholderAt(point) is not null || HintUnder(point) is not null || CharacterAt(point) is not { } offset)
        {
            StopNavigationHover();
            return;
        }
        if (navigationAsked is { } asked && asked.Action == action && (asked.Offset == offset ||
            navigationRange is { } range && range.Start <= offset && offset < range.End))
        {
            NavigationCursor();
            return;
        }
        StopNavigationHover();
        navigationAsked = (action, offset);
        navigationWait ??= new Debounce(TimeSpan.FromMilliseconds(100), FindNavigationTarget);
        navigationWait.Restart();
    }

    private async Task FindNavigationTarget()
    {
        if (navigationAsked is not { } asked || NavigationTarget is not { } find)
        {
            return;
        }
        using CancellationTokenSource cancel = new();
        navigationCancel = cancel;
        TextSnapshot text = document.Snapshot;
        try
        {
            TextRange? range = await find(asked.Action, asked.Offset, cancel.Token);
            if (!cancel.IsCancellationRequested && ReferenceEquals(text, document.Snapshot) && navigationAsked == asked)
            {
                navigationRange = range;
                NavigationCursor();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        finally
        {
            if (navigationCancel == cancel)
            {
                navigationCancel = null;
            }
        }
    }

    private void StopNavigationHover()
    {
        navigationWait?.Stop();
        navigationCancel?.Cancel();
        navigationCancel = null;
        navigationAsked = null;
        navigationRange = null;
        NavigationCursor();
    }

    private void NavigationCursor()
    {
        if (navigationPoint is { } point)
        {
            Cursor = point.X < GutterWidth || drag.Mode == DragMode.Gutter ? Cursor.Default :
                navigationRange is not null || HoveredLink is not null ? HandCursor : IbeamCursor;
        }
    }
}
