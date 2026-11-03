using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.Telemetry;

/// <summary>A run's Telemetry beside its console, the Run and Debug windows' alike: the one <see cref="TelemetryView"/>, made
/// the first time the pane has a run's records to show, streaming them only while the pane is shown in a window.</summary>
public sealed class TelemetryPane : Panel
{
    private TelemetryView? view;
    private TelemetrySource? source;
    private bool attached;

    /// <summary>The records shown: a run's dashboard's, or none.</summary>
    public TelemetrySource? Source
    {
        get => source;
        set
        {
            if (source != value)
            {
                source = value;
                Follow();
            }
        }
    }

    /// <summary>Its view, made as it is first asked for.</summary>
    public TelemetryView View
    {
        get
        {
            if (view is null)
            {
                view = new TelemetryView();
                view.ShownChanged += (_, _) => Follow();
                Children.Add(view.Both);
            }
            return view;
        }
    }

    /// <summary>Whether its view has been made.</summary>
    public bool HasView => view is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Follow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        attached = false;
        Follow();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Follow();
        }
    }

    /// <summary>Streams its source's page shown while the pane is in a window and shown, else nothing.</summary>
    private void Follow()
    {
        bool shows = attached && IsVisible && source is not null;
        if (view is null && !shows)
        {
            return;
        }
        View.Watch(shows ? source : null, View.Shown);
    }
}
