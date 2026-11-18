namespace Passenger.Core;

/// <summary>What opens a page in a browser program a person names: the program with the arguments given each start of it,
/// the page's address last; on macOS an app bundle through the system's open, as the Finder opens one, the arguments
/// after <c>--args</c>, which the app takes only as it starts.</summary>
public static class BrowserPrograms
{
    /// <summary>macOS's open, which opens an app bundle as the Finder does.</summary>
    public const string Open = "/usr/bin/open";

    /// <summary>The program to start and its arguments to open <paramref name="page"/> in <paramref name="program"/> given
    /// <paramref name="arguments"/>, where <paramref name="bundle"/> says the program is a macOS app bundle.</summary>
    public static (string Program, IReadOnlyList<string> Arguments) Opening(string program, IReadOnlyList<string> arguments, Uri page, bool bundle) => bundle
        ? (Open, ["-a", program, page.AbsoluteUri, .. arguments.Count > 0 ? (string[])["--args", .. arguments] : []])
        : (program, [.. arguments, page.AbsoluteUri]);
}
