using System.Runtime.InteropServices;

namespace Passenger.Inspector.WinUI;

/// <summary>COM's IObjectWithSite, as WinUI's XAML diagnostics hand a TAP its site.</summary>
[ComImport]
[Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    [PreserveSig]
    int SetSite(nint site);

    [PreserveSig]
    int GetSite(ref Guid riid, out nint site);
}

/// <summary>The TAP WinUI's XAML diagnostics make in the app (by its CLSID, through .NET's COM host beside this assembly):
/// it hands the site WinUI gives it, which serves IXamlDiagnostics and IVisualTreeService, to the inspector that asked for
/// it, through what that inspector left in the app's AppContext (<see cref="SiteKey"/>), whichever load context the COM host
/// made this class in.</summary>
[ComVisible(true)]
[Guid(Id)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class Tap : IObjectWithSite
{
    /// <summary>The TAP's CLSID, which the inspector names to WinUI.</summary>
    public const string Id = "6F0B9E2A-6C35-4E1E-9D5B-3A2F1E7C4B10";

    /// <summary>The AppContext data the inspector leaves its hand for the site in: an Action taking the site's IUnknown,
    /// one reference added for it.</summary>
    public const string SiteKey = "Passenger.Inspector.WinUI.Site";

    public int SetSite(nint site)
    {
        if (site != 0 && AppContext.GetData(SiteKey) is Action<nint> take)
        {
            Marshal.AddRef(site);
            take(site);
        }
        return 0;
    }

    public int GetSite(ref Guid riid, out nint site)
    {
        site = 0;
        // E_FAIL: the site is the inspector's to hold.
        return unchecked((int)0x80004005);
    }
}
