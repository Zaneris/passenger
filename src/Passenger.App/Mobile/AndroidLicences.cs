using System.Xml;
using Passenger.Core;

namespace Passenger.App.Mobile;

/// <summary>A licence of the Android SDK, as Google's repository names it (<c>android-sdk-license</c>), and its text.</summary>
public sealed record AndroidLicence(string Id, string Text);

/// <summary>The licences of the Android SDK packages to be installed, read from Google's repository of them (the one
/// the Android workload's installer downloads from), which names each package's licence and gives its text. Shown to
/// the person before anything is installed, so what they accept is what Google publishes now.</summary>
public static class AndroidLicences
{
    /// <summary>A stand-in of Google's repository, for tests: the URL of its XML.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_ANDROID_REPOSITORY";

    private const string Repository = "https://dl.google.com/android/repository/repository2-3.xml";

    /// <summary>The licences the packages use (the SDK's own licence where one is not listed), each once, in the
    /// repository's order.</summary>
    public static async Task<IReadOnlyList<AndroidLicence>> ReadAsync(IReadOnlyList<string> packages, CancellationToken cancel)
    {
        string address = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } standIn ? standIn : Repository;
        using HttpClient client = OutboundHttp.Client(TimeSpan.FromSeconds(60), 64L * 1024 * 1024);
        await using Stream body = await client.GetStreamAsync(new Uri(address), cancel);
        return Read(body, packages);
    }

    /// <summary>The licences a repository's XML gives the packages, each once; the SDK's own licence for a package it does
    /// not list. DTDs are refused.</summary>
    public static IReadOnlyList<AndroidLicence> Read(Stream xml, IReadOnlyList<string> packages)
    {
        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        Dictionary<string, string> uses = new(StringComparer.Ordinal);
        using XmlReader reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
        string? package = null;
        // Reading a licence's text leaves the reader on the node after it, which is not read past.
        bool onNext = false;
        while (onNext || reader.Read())
        {
            onNext = false;
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            switch (reader.LocalName)
            {
                case "license" when reader.GetAttribute("id") is { Length: > 0 } id:
                    texts[id] = reader.ReadElementContentAsString().Trim();
                    onNext = !reader.EOF;
                    break;
                case "remotePackage":
                    package = reader.GetAttribute("path");
                    break;
                case "uses-license" when package is not null && reader.GetAttribute("ref") is { Length: > 0 } used:
                    uses.TryAdd(package, used);
                    break;
            }
        }
        List<string> wanted = [];
        foreach (string asked in packages)
        {
            string id = uses.GetValueOrDefault(asked) ?? SdkLicence;
            if (!wanted.Contains(id))
            {
                wanted.Add(id);
            }
        }
        if (wanted.Count == 0)
        {
            wanted.Add(SdkLicence);
        }
        return [.. wanted.Select(id => new AndroidLicence(id, texts.GetValueOrDefault(id)
            ?? throw new XmlException($"Google's repository gives no text of {id}")))];
    }

    /// <summary>The Android SDK's own licence, which its platforms and tools take.</summary>
    private const string SdkLicence = "android-sdk-license";
}
