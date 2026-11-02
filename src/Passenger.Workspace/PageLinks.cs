using System.Security.Cryptography;

namespace Passenger.Workspace;

/// <summary>A stylesheet a page links: the address it names as the page is served (a Razor <c>@Assets["…"]</c> read as the
/// path it names, a view's <c>~/</c> as the app's root), and the hashes its <c>integrity</c> gives, if any.</summary>
public sealed record LinkedSheet(string Href, string? Integrity);

/// <summary>The stylesheets a page's <c>&lt;link rel="stylesheet"&gt;</c>s name, and whether a stylesheet's bytes are what
/// a link's <c>integrity</c> says, as a browser checks them: one of the strongest hashes it gives matches. Pure.</summary>
public static class PageLinks
{
    public static IReadOnlyList<LinkedSheet> Stylesheets(string markup, bool razor)
    {
        List<LinkedSheet> found = [];
        foreach (MarkupTag tag in MarkupTags.Read(markup, razor))
        {
            if (!tag.Name.Equals("link", StringComparison.OrdinalIgnoreCase) || tag.Attribute("rel") is not { } rel || tag.Attribute("href") is not { } href
                || !markup.AsSpan(rel.ValueStart, rel.ValueEnd - rel.ValueStart).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (Address(markup[href.ValueStart..href.ValueEnd]) is { } address)
            {
                string? integrity = tag.Attribute("integrity") is { } hashes ? markup[hashes.ValueStart..hashes.ValueEnd].Trim() : null;
                found.Add(new LinkedSheet(address, integrity is { Length: > 0 } ? integrity : null));
            }
        }
        return found;
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
