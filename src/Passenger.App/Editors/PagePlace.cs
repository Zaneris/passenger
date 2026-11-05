using System.Globalization;

namespace Passenger.App.Editors;

/// <summary>Where a page is scrolled to, across and down in the page's own pixels: read from the page through the web
/// view's script call before it is given again, and put back the same way once the new page has loaded. Each system's
/// engine answers the scripts alike, WebView2 its answer as JSON.</summary>
public readonly record struct PagePlace(double X, double Y)
{
    /// <summary>The script whose answer is the page's place, as "x,y".</summary>
    public const string Read = "window.scrollX + ',' + window.scrollY";

    /// <summary>Whether the page is scrolled from its start, so there is a place to put back.</summary>
    public bool Away => X != 0 || Y != 0;

    /// <summary>The script putting the place back once the page has loaded, unless the page has scrolled itself by then; a
    /// page shorter than the place stops at its end, and the page's smooth scrolling is set aside for the jump.</summary>
    public string Restore => string.Create(CultureInfo.InvariantCulture, $$"""
        (function (x, y) {
          function back() {
            if (window.scrollX !== 0 || window.scrollY !== 0) { return; }
            var root = document.documentElement, was = root.style.scrollBehavior;
            root.style.scrollBehavior = 'auto';
            window.scrollTo(x, y);
            root.style.scrollBehavior = was;
          }
          if (document.readyState === 'complete') { back(); } else { window.addEventListener('load', back, { once: true }); }
        })({{X}}, {{Y}});
        """);

    /// <summary>The place a web view answered <see cref="Read"/> with, bare or as a JSON string; null for an answer that
    /// names none.</summary>
    public static PagePlace? Of(string? answer)
    {
        if (answer is null)
        {
            return null;
        }
        ReadOnlySpan<char> said = answer.AsSpan().Trim();
        if (said.Length >= 2 && said[0] == '"' && said[^1] == '"')
        {
            said = said[1..^1];
        }
        int comma = said.IndexOf(',');
        return comma > 0
            && double.TryParse(said[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.IsFinite(x)
            && double.TryParse(said[(comma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && double.IsFinite(y)
            ? new PagePlace(x, y)
            : null;
    }
}
