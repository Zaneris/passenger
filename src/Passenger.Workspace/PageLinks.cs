using System.Security.Cryptography;

namespace Passenger.Workspace;

/// <summary>A stylesheet a page links: the address it names as the page is served (a Razor <c>@Assets["…"]</c> read as the
/// path it names, a view's <c>~/</c> as the app's root), and the hashes its <c>integrity</c> gives, if any.</summary>
public sealed record LinkedSheet(string Href, string? Integrity);

/// <summary>The stylesheets a page's <c>&lt;link rel="stylesheet"&gt;</c>s name, and whether a stylesheet's bytes are what
/// a link's <c>integrity</c> says, as a browser checks them: one of the strongest hashes it gives matches. Pure.</summary>
public static class PageLinks
{
    /// <summary>Each stylesheet a page links, its address as the app serves it: a relative one taken from the page's
    /// <c>&lt;base href&gt;</c>, and one under that base read from the app's root, as an app served under that path base
    /// (<c>UsePathBase</c>) serves it.</summary>
    public static IReadOnlyList<LinkedSheet> Stylesheets(string markup, bool razor)
    {
        List<LinkedSheet> found = [];
        IReadOnlyList<MarkupTag> tags = MarkupTags.Read(markup, razor);
        string? based = BasePath(markup, tags);
        foreach (MarkupTag tag in tags)
        {
            if (!tag.Name.Equals("link", StringComparison.OrdinalIgnoreCase) || tag.Attribute("rel") is not { } rel || tag.Attribute("href") is not { } href
                || !markup.AsSpan(rel.ValueStart, rel.ValueEnd - rel.ValueStart).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (Address(markup[href.ValueStart..href.ValueEnd]) is { } written && Served(written, based) is { } address)
            {
                string? integrity = tag.Attribute("integrity") is { } hashes ? markup[hashes.ValueStart..hashes.ValueEnd].Trim() : null;
                found.Add(new LinkedSheet(address, integrity is { Length: > 0 } ? integrity : null));
            }
        }
        return found;
    }

    /// <summary>The path a page's <c>&lt;base href&gt;</c> names when it is another than the root (<c>/app/</c>, ending in
    /// '/'); null for none, the root, or one on another host.</summary>
    private static string? BasePath(string markup, IReadOnlyList<MarkupTag> tags)
    {
        if (tags.FirstOrDefault(t => t.Name.Equals("base", StringComparison.OrdinalIgnoreCase) && t.Attribute("href") is not null) is not { } tag)
        {
            return null;
        }
        MarkupAttribute href = tag.Attribute("href")!.Value;
        string value = markup[href.ValueStart..href.ValueEnd].Trim();
        if (value.Contains("://", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }
        string path = "/" + value.Trim('/');
        return path == "/" ? null : path + "/";
    }

    /// <summary>An address as the app serves it under a page's base: a relative one after the base, then one under the base
    /// from the app's root; as written for one on the web, or with no base.</summary>
    private static string? Served(string address, string? based)
    {
        if (based is null || address.Contains("://", StringComparison.Ordinal) || address.StartsWith("//", StringComparison.Ordinal))
        {
            return address;
        }
        string full = address.StartsWith('/') ? address : based + address;
        return full.StartsWith(based, StringComparison.OrdinalIgnoreCase) ? full[(based.Length - 1)..] : full;
    }

    /// <summary>A page's <c>&lt;style&gt;</c> blocks as a stylesheet's text: the page's text with everything outside them
    /// blank (its line breaks kept), so a rule's places are the page's own; null for a page with none.</summary>
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

    /// <summary>The address an href's value names; null for one an expression makes.</summary>
    private static string? Address(string value)
    {
        string href = value.Trim();
        foreach (string asset in (string[])["@Assets[\"", "@(Assets[\""])
        {
            if (href.StartsWith(asset, StringComparison.Ordinal))
            {
                int close = href.IndexOf('"', asset.Length);
                return close > asset.Length ? href[asset.Length..close] : null;
            }
        }
        if (href.Contains('@', StringComparison.Ordinal) && !href.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        return href.StartsWith("~/", StringComparison.Ordinal) ? href[1..] : href.Length > 0 ? href : null;
    }

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
