using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>How a run's HTTPS is served, as its start worked it out before anything of it started: with the certificate
/// exported for its containers (<see cref="Certificate"/>), with this machine's own (neither that nor <see cref="OverHttp"/>),
/// or over HTTP for this run, <see cref="OverHttp"/> saying what and why; <see cref="Failed"/> when a step of dev-certs
/// failed, and <see cref="Said"/> what it said worth reading, for the run's console.</summary>
public sealed record HttpsChoice(DevelopmentCertificate.Exported? Certificate, Words? OverHttp, bool Failed, IReadOnlyList<string> Said)
{
    /// <summary>A run that serves no HTTPS, of which nothing was asked.</summary>
    public static readonly HttpsChoice None = new(null, null, false, []);
}

/// <summary>The development certificate of every run that serves HTTPS, a container run's and an Aspire AppHost's alike:
/// checked once before the run starts, the person asked once when it is missing or not trusted, nobody asked when an
/// agent's call started the run or the IDE is an agent's own, the wait for the answer holding no background work, and Stop
/// closing the question.</summary>
public sealed partial class BuildRunController
{
    /// <summary>Why nobody may be asked about this start, or null when the person at the window may be: an agent's call
    /// started it (its answer would wait on them for good), or the IDE is an agent's own (<c>--mcp</c>).</summary>
    private Words? NoOneToAsk() => Mcp.AgentCall.Current is not null ? LaunchWords.AgentStartedRun : parts.AsksNoOne ? LaunchWords.NobodyToAsk : null;

    /// <summary>The development certificate a run's HTTPS takes, <paramref name="serving"/> naming what of it serves HTTPS
    /// (none: nothing is asked): checked in <paramref name="folder"/>, and, missing or not trusted, the person asked once whether
    /// to make and trust it or run over HTTP; for a run in containers (<paramref name="export"/>), exported into
    /// <paramref name="state"/>. With none, what serves HTTPS serves HTTP for this run, which the choice says. Background work
    /// is let go while the person answers; stopped, it throws <see cref="OperationCanceledException"/>.</summary>
    private async Task<HttpsChoice> CertificateAsync(string configuration, IReadOnlyList<string> serving, string folder, string state, bool export, Working work)
    {
        if (serving.Count == 0)
        {
            return HttpsChoice.None;
        }
        string named = string.Join(", ", serving);
        DotnetWords dotnet = parts.DevCertificates ?? DevelopmentCertificate.Machine;
        List<string> said = [];
        work.Say(LaunchWords.CheckingCertificate);
        (DevelopmentCertificate.Standing standing, string checkSaid, string? thumbprint) =
            await BackgroundWork.Run(() => DevelopmentCertificate.CheckAsync(dotnet, folder, work.Stopping));
        work.Stopping.ThrowIfCancellationRequested();
        if (standing is DevelopmentCertificate.Standing.Missing or DevelopmentCertificate.Standing.NotTrusted)
        {
            bool missing = standing == DevelopmentCertificate.Standing.Missing;
            string stands = missing ? "missing" : "untrusted";
            // Asking would hold an agent's call on a person who may never answer: it runs over HTTP and says so.
            if (NoOneToAsk() is { } why)
            {
                return new HttpsChoice(null, LaunchWords.OverHttpUnasked(named, stands, why), false, said);
            }
            bool trust = false;
            if (parts.AskCertificate is { } ask)
            {
                // The person's answer takes as long as it takes: the run's hold on what is drawn is let go meanwhile.
                BackgroundWork.End();
                try
                {
                    trust = await ask(configuration, named, missing, work.Stopping);
                }
                finally
                {
                    BackgroundWork.Begin();
                }
            }
            work.Stopping.ThrowIfCancellationRequested();
            if (!trust)
            {
                return new HttpsChoice(null, LaunchWords.OverHttpChosen(named, stands), false, said);
            }
            work.Say(LaunchWords.TrustingCertificate);
            if (await BackgroundWork.Run(() => DevelopmentCertificate.TrustAsync(dotnet, folder, state, work.Stopping)) is { } untrusted)
            {
                return Unmade(LaunchWords.CertificateNotTrusted(untrusted));
            }
        }
        else if (standing == DevelopmentCertificate.Standing.Unknown)
        {
            said.Add($"dotnet dev-certs https --check: {checkSaid}");
        }
        if (!export)
        {
            // What runs on this machine takes the certificate from its own store.
            return new HttpsChoice(null, null, false, said);
        }
        work.Say(LaunchWords.ExportingCertificate);
        (DevelopmentCertificate.Exported? exported, string? problem) = await BackgroundWork.Run(() => DevelopmentCertificate.ExportAsync(dotnet, state, thumbprint, work.Stopping));
        work.Stopping.ThrowIfCancellationRequested();
        return exported is not null ? new HttpsChoice(exported, null, false, said) : Unmade(LaunchWords.CertificateNotExported(problem ?? string.Empty));

        HttpsChoice Unmade(Words why)
        {
            said.Add(LaunchWords.ServesHttp(count: serving.Count, services: named, why: why).Shown);
            return new HttpsChoice(null, LaunchWords.OverHttp(named, why), true, said);
        }
    }
}
