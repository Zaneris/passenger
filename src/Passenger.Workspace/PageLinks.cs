using System.Security.Cryptography;

namespace Passenger.Workspace;

/// <summary>A stylesheet a page links, by a <c>&lt;link&gt;</c> or a <c>&lt;style&gt;</c> block's <c>@import</c>: the address
/// a browser asks for (a Razor <c>@Assets["…"]</c> or <c>@Url.Content("…")</c> read as the path it names, a view's <c>~/</c> as the app's root); the
/// hashes its <c>integrity</c> gives, if any; and under a page's <c>&lt;base href&gt;</c> that address without the base's
/// path, where an app mounted at that path (<c>UsePathBase</c>) serves it: a page can't say which it is.</summary>
public sealed record LinkedSheet(string Href, string? Integrity, string? Mounted = null)
{
    /// <summary>Whether an expression makes its address, known only as the app runs: <see cref="Href"/> is what is written.</summary>
    public bool IsExpression { get; init; }
}

/// <summary>The stylesheets a page's <c>&lt;link rel="stylesheet"&gt;</c>s and <c>&lt;style&gt;</c> blocks name, resolved as a
/// browser resolves them, and whether a stylesheet's bytes are what a link's <c>integrity</c> says, as a browser checks
/// them: one of the strongest hashes it gives matches. Pure.</summary>
public static class PageLinks
{
    /// <summary>The app's root as a URL no host answers, for an address to be resolved from it as a browser would.</summary>
    private static readonly Uri AppRoot = new("https://app.invalid/");

    /// <summary>What a Razor expression naming an address by one string is written between.</summary>
    private static readonly (string Open, string Close)[] Named = [("@Assets[\"", "\"]"), ("@(Assets[\"", "\"])"), ("@Url.Content(\"", "\")"), ("@(Url.Content(\"", "\"))")];

    /// <summary>Each stylesheet a page links: its address from the page's <c>&lt;base href&gt;</c> (a CDN's too), else from
    /// the page's own address where that is known (<paramref name="page"/>, from the app's root), else as written.</summary>
    public static IReadOnlyList<LinkedSheet> Stylesheets(string markup, bool razor, string? page = null)
    {
        List<LinkedSheet> found = [];
        IReadOnlyList<MarkupTag> tags = MarkupTags.Read(markup, razor);
        (Uri? based, string? mount) = BaseOf(markup, tags, razor, page);
        foreach (MarkupTag tag in tags)
        {
            if (!tag.Name.Equals("link", StringComparison.OrdinalIgnoreCase) || tag.Attribute("rel") is not { } rel || tag.Attribute("href") is not { } href
                || !markup.AsSpan(rel.ValueStart, rel.ValueEnd - rel.ValueStart).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            string value = markup[href.ValueStart..href.ValueEnd].Trim();
            if (Address(value, razor) is { } written)
            {
                string? integrity = tag.Attribute("integrity") is { } hashes ? markup[hashes.ValueStart..hashes.ValueEnd].Trim() : null;
                found.Add(Linked(written, integrity is { Length: > 0 } ? integrity : null, based, mount));
            }
            else if (value.Length > 0)
            {
                found.Add(new LinkedSheet(value, null) { IsExpression = true });
            }
        }
        if (StyleText(markup, razor) is { } style)
        {
            found.AddRange(CssRules.Read(style).Imports.Select(imported => Linked(imported, null, based, mount)));
        }
        return found;
    }

    /// <summary>What a page's addresses are resolved from (its <c>&lt;base href&gt;</c>, else its own address; null for neither
    /// or the root, whose addresses stay as written), and the base's folder when it is on the app's own host, past the root.</summary>
    private static (Uri? Url, string? Mount) BaseOf(string markup, IReadOnlyList<MarkupTag> tags, bool razor, string? page)
    {
        Uri? own = page is not null && Uri.TryCreate(AppRoot, page, out Uri? address) ? address : null;
        if (tags.FirstOrDefault(t => t.Name.Equals("base", StringComparison.OrdinalIgnoreCase) && t.Attribute("href") is not null) is not { } tag)
        {
            return (own, null);
        }
        MarkupAttribute href = tag.Attribute("href")!.Value;
        if (Address(markup[href.ValueStart..href.ValueEnd], razor) is not { } written || !Uri.TryCreate(own ?? AppRoot, written, out Uri? based))
        {
            return (own, null);
        }
        if (based.Host != AppRoot.Host)
        {
            return (based, null);
        }
        string folder = based.AbsolutePath[..(based.AbsolutePath.LastIndexOf('/') + 1)];
        return folder == "/" ? (own is null ? null : based, null) : (based, folder);
    }

    /// <summary>A stylesheet as a page's base gives its address, and that address without the base's folder when it is under it.</summary>
    private static LinkedSheet Linked(string written, string? integrity, Uri? based, string? mount)
    {
        if (based is null || !Uri.TryCreate(based, written, out Uri? target))
        {
            return new LinkedSheet(written, integrity);
        }
        string address = AddressOf(target);
        return new LinkedSheet(address, integrity, mount is not null && address.StartsWith(mount, StringComparison.OrdinalIgnoreCase) ? address[(mount.Length - 1)..] : null);
    }

    /// <summary>An address from the app's root, or whole on another host.</summary>
    private static string AddressOf(Uri target) => target.Host == AppRoot.Host ? target.PathAndQuery : target.AbsoluteUri;

    /// <summary>The address an <c>@import</c> names from its stylesheet's own, as a browser resolves it: a '..' stops at the
    /// app's root, and one on another host is whole.</summary>
    public static string Imported(string sheet, string import) =>
        Uri.TryCreate(AppRoot, sheet, out Uri? from) && Uri.TryCreate(from, import, out Uri? target) ? AddressOf(target) : import;

    /// <summary>Whether an address is the CSS isolation bundle of a project of a package id: at the app's root, or the root of
    /// the static web assets' base path the project gives itself, as its build places it.</summary>
    public static bool IsBundle(string address, string package, string? basePath)
    {
        string served = basePath?.Replace('\\', '/').Trim('/') ?? string.Empty;
        string bundle = $"{package}.styles.css";
        string place = served.Length == 0 || served.Equals($"_content/{package}", StringComparison.OrdinalIgnoreCase) ? bundle : $"{served}/{bundle}";
        return address.TrimStart('/').Equals(place, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A page's <c>&lt;style&gt;</c> blocks as a stylesheet's text: the page's text with everything outside them
    /// blank (its line breaks kept), so a rule's places are the page's own, an at-rule's '@' that Razor writes doubled single;
    /// null for a page with none.</summary>
    public static string? StyleText(string markup, bool razor)
    {
        char[]? kept = null;
        foreach (MarkupTag tag in MarkupTags.Read(markup, razor))
        {
            if (!tag.Name.Equals("style", StringComparison.OrdinalIgnoreCase) || markup.IndexOf('>', tag.Start) is not (> 0 and int open))
            {
                continue;
            }
            int close = markup.IndexOf("</style", open, StringComparison.OrdinalIgnoreCase);
            close = close < 0 ? markup.Length : close;
            if (kept is null)
            {
                kept = new char[markup.Length];
                for (int i = 0; i < markup.Length; i++)
                {
                    kept[i] = markup[i] is '\n' or '\r' ? markup[i] : ' ';
                }
            }
            markup.AsSpan(open + 1, close - open - 1).CopyTo(kept.AsSpan(open + 1));
            for (int i = open + 1; razor && i + 1 < close; i++)
            {
                if (kept[i] == '@' && kept[i + 1] == '@')
                {
                    kept[i] = ' ';
                }
            }
        }
        return kept is null ? null : new string(kept);
    }

    /// <summary>The base path a package's static web assets are served under, as its <c>build</c> props give its assets
    /// (Microsoft.AspNetCore.StaticWebAssets.props' <c>BasePath</c>); null where they give none.</summary>
    public static string? AssetBasePath(string props)
    {
        int at = props.IndexOf("<BasePath>", StringComparison.Ordinal);
        int end = at < 0 ? -1 : props.IndexOf("</BasePath>", at, StringComparison.Ordinal);
        return end < 0 ? null : props[(at + "<BasePath>".Length)..end].Trim().Trim('/') is { Length: > 0 } path ? path : null;
    }

    /// <summary>The address an href's value names; null for none, or one an expression makes.</summary>
    private static string? Address(string value, bool razor)
    {
        string href = value.Trim();
        foreach ((string open, string close) in Named)
        {
            if (href.StartsWith(open, StringComparison.Ordinal))
            {
                string named = href.EndsWith(close, StringComparison.Ordinal) && href.Length > open.Length + close.Length ? href[open.Length..^close.Length] : string.Empty;
                return named.Length > 0 && !named.Contains('"', StringComparison.Ordinal) ? FromRoot(named) : null;
            }
        }
        if (razor && href.Contains('@', StringComparison.Ordinal) && !href.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        return href.Length > 0 ? FromRoot(href) : null;
    }

    private static string FromRoot(string href) => href.StartsWith("~/", StringComparison.Ordinal) ? href[1..] : href;

    /// <summary>Whether bytes match a link's integrity: true with none given or none of an algorithm a browser checks.</summary>
    public static bool Matches(string? integrity, ReadOnlySpan<byte> content)
    {
        if (integrity is null)
        {
            return true;
        }
        List<(int Strength, string Algorithm, string Hash)> given = [];
        foreach (string token in integrity.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = token.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0)
            {
                continue;
            }
            string algorithm = token[..dash].ToLowerInvariant();
            // A hash may carry options after a '?', which a browser passes over.
            string hash = token[(dash + 1)..].Split('?')[0];
            int strength = algorithm switch { "sha256" => 1, "sha384" => 2, "sha512" => 3, _ => 0 };
            if (strength > 0)
            {
                given.Add((strength, algorithm, hash));
            }
        }
        if (given.Count == 0)
        {
            return true;
        }
        int strongest = given.Max(g => g.Strength);
        foreach ((int strength, string algorithm, string hash) in given.Where(g => g.Strength == strongest))
        {
            byte[] made = algorithm switch
            {
                "sha256" => SHA256.HashData(content),
                "sha384" => SHA384.HashData(content),
                _ => SHA512.HashData(content),
            };
            if (Convert.ToBase64String(made) == hash)
            {
                return true;
            }
        }
        return false;
    }
}
