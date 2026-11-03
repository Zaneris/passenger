using Passenger.App.Workspace;

namespace Passenger.App.Vcs;

/// <summary>A diff to show, whichever way Settings shows it (<see cref="Settings.AppSettings.OpenDiffsIn"/>): its title,
/// what makes two the same (the versions and files compared), the file its right side is of, the open file whose text that
/// side is (null where it is a copy, read-only), and its view, made afresh for each window or group that shows it.</summary>
public sealed record DiffContent(string Title, string Key, string Path, OpenDocument? File, Func<DiffView> Make);

/// <summary>Where a diff is shown: a window of its own over the IDE's (<see cref="DiffWindow"/>) or a tab of the editors
/// (<see cref="DiffTab"/>), each holding open the file its right side edits.</summary>
public interface IDiffHost
{
    /// <summary>What it shows.</summary>
    DiffContent Shown { get; }

    /// <summary>Its view: the window's, or the one shown by the group worked in (else another showing it); null for a tab
    /// no group shows now.</summary>
    DiffView? View { get; }

    void Close();

    /// <summary>Its file shown by Jump to Source: a window over the editors closes so the file shows; a tab among them stays.</summary>
    void GiveWay();
}
