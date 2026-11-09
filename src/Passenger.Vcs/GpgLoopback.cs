using System.Diagnostics;
using System.Text;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>Passenger's gpg, which git runs in place of gpg to sign a commit Passenger makes (gpg.program, given for that
/// commit alone with -c): the person's gpg (<see cref="Variable"/> names it) run with loopback pinentry, the data to sign
/// in a file of its own so gpg's input is free for its commands, and the passphrase it asks for (GET_HIDDEN on its status)
/// asked of the person through the operation's askpass socket (<see cref="GitAskPass"/>) and written to it. In place of
/// gpgsm (gpg.x509.program; <see cref="X509Variable"/> names the person's), which takes a passphrase only as it starts: run
/// first with no pinentry, which an agent holding the passphrase answers, then where it wants one, given the one asked. What
/// gpg writes and says goes to git as its own would; a call that signs nothing passes through untouched.</summary>
public static class GpgLoopback
{
    /// <summary>The variable naming the person's gpg, which makes Passenger's executable this program.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_GPG";

    /// <summary>The variable naming the person's gpgsm, which signs with an X.509 certificate's key, as git runs gpg.</summary>
    [IdentifierHome]
    public const string X509Variable = "PASSENGER_GPGSM";

    /// <summary>What Main asks before anything starts: where git runs this executable as its gpg, gpg run and its exit
    /// code; else null, and Passenger starts as itself.</summary>
    public static async Task<int?> RunAsync(string[] args)
    {
        string? gpgsm = Environment.GetEnvironmentVariable(X509Variable) is { Length: > 0 } x509 ? x509 : null;
        if ((gpgsm ?? Environment.GetEnvironmentVariable(Variable)) is not { Length: > 0 } gpg)
        {
            return null;
        }
        string? socket = Environment.GetEnvironmentVariable(GitAskPass.Variable);
        // What gpg leaves running (its agent) must never hold git's pipes, which git reads to their end.
        StandardHandles.NotInherited();
        // git signs with "--status-fd=2 -bsau KEY", its data on gpg's input.
        if (socket is null || !args.Any(a => a.Length > 2 && a[0] == '-' && a[1] != '-' && a.Contains('b') && a.Contains('s')))
        {
            return await ThroughAsync(gpg, args);
        }
        using MemoryStream data = new();
        await using (Stream input = Console.OpenStandardInput())
        {
            await input.CopyToAsync(data);
        }
        string file = Path.Combine(Path.GetTempPath(), $"passenger-gpg-{Environment.ProcessId}-{Guid.NewGuid():N}");
        AtomicFile.Write(file, data.ToArray(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            return gpgsm is null ? await SignAsync(gpg, args, file, socket) : await SignX509Async(gpgsm, args, file, socket);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<int> SignAsync(string gpg, string[] args, string file, string socket)
    {
        ProcessStartInfo start = ChildProcesses.Command(gpg, ["--pinentry-mode", "loopback", "--command-fd", "0", "--output", "-", .. args, "--", file]);
        MsysGpg.Fit(start, gpg);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process signing = await ChildProcesses.StartAsync(start);
        await using Stream output = Console.OpenStandardOutput();
        Task signature = ChildProcesses.OutputStream(signing).CopyToAsync(output);
        await using Stream errors = Console.OpenStandardError();
        Task said = StatusAsync(signing, errors, socket);
        await signing.WaitForExitAsync();
        await EndedAsync(signing, Task.WhenAll(signature, said));
        return signing.ExitCode;
    }

    /// <summary>gpg's status and words passed to git as they come, a passphrase it asks for (GET_HIDDEN) asked of the person
    /// through the socket and written to its commands.</summary>
    private static async Task StatusAsync(Process signing, Stream errors, string socket)
    {
        string? user = null;
        string? key = null;
        TextReader words = ChildProcesses.Errors(signing);
        while (await words.ReadLineAsync() is { } line)
        {
            await errors.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await errors.FlushAsync();
            string[] status = line.Split(' ', 4);
            if (status is ["[GNUPG:]", "USERID_HINT", { } hinted, { } named])
            {
                (key, user) = (hinted, named);
            }
            else if (status is ["[GNUPG:]", "GET_HIDDEN", "passphrase.enter", ..])
            {
                string asked = user is null ? key ?? "the signing key" : $"{user} ({key})";
                if (Ask(socket, $"Enter passphrase for GPG key {asked}:") is { } passphrase)
                {
                    await signing.StandardInput.WriteAsync(passphrase + "\n");
                    await signing.StandardInput.FlushAsync();
                }
                else
                {
                    // No answer: gpg reads the end of its commands and gives up.
                    signing.StandardInput.Close();
                }
            }
        }
    }

    /// <summary>A gpg that has exited, its pipes' reads given the grace a program it left running gets: an MSYS gpg's agent
    /// holds them open, so they are let go once that has passed.</summary>
    private static async Task EndedAsync(Process gpg, Task reads)
    {
        if (!await ChildProcesses.OutputEndsAsync(gpg, reads))
        {
            UnawaitedWork.Dismiss(reads);
        }
    }

    /// <summary>gpgsm signing the file: with no pinentry first, then where it wants the key's passphrase, given the one the
    /// person is asked through the socket; what the run git takes writes and says passed to git.</summary>
    private static async Task<int> SignX509Async(string gpgsm, string[] args, string file, string socket)
    {
        (int code, byte[] signature, byte[] said) = await RunX509Async(gpgsm, ["--pinentry-mode", "error", "--output", "-", .. args, "--", file], null);
        if (code != 0 && Encoding.UTF8.GetString(said).Contains(WantsPassphrase, StringComparison.Ordinal))
        {
            int named = Array.FindIndex(args, a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.EndsWith('u'));
            string key = named >= 0 && named + 1 < args.Length ? args[named + 1] : "the signing certificate";
            if (Ask(socket, $"Enter passphrase for X.509 key {key}:") is { } passphrase)
            {
                (code, signature, said) = await RunX509Async(gpgsm, ["--batch", "--pinentry-mode", "loopback", "--passphrase-fd", "0", "--output", "-", .. args, "--", file], passphrase + "\n");
            }
        }
        await using (Stream output = Console.OpenStandardOutput())
        {
            await output.WriteAsync(signature);
        }
        await using (Stream errors = Console.OpenStandardError())
        {
            await errors.WriteAsync(said);
        }
        return code;
    }

    /// <summary>What gpgsm says where its agent wanted a passphrase and might not ask for one: sm/sign.c's "error creating
    /// signature: %s <%s>" with GPG_ERR_NO_PIN_ENTRY's words (libgpg-error), the agent's answer in pinentry mode error. No
    /// status line carries the cause (gpgsm's FAILURE gpgsm-exit names none), so its words are read untranslated.</summary>
    private const string WantsPassphrase = "error creating signature: No pinentry";

    /// <summary>One run of gpgsm, given <paramref name="input"/>, its words untranslated: its exit code, what it wrote and
    /// what it said.</summary>
    private static async Task<(int Code, byte[] Output, byte[] Errors)> RunX509Async(string gpgsm, IReadOnlyList<string> args, string? input)
    {
        ProcessStartInfo start = ChildProcesses.Command(gpgsm, args);
        MsysGpg.Fit(start, gpgsm);
        start.Environment["LANGUAGE"] = "C";
        start.Environment["LC_ALL"] = "C";
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process signing = await ChildProcesses.StartAsync(start);
        using MemoryStream output = new();
        using MemoryStream errors = new();
        Task written = ChildProcesses.OutputStream(signing).CopyToAsync(output);
        Task said = ChildProcesses.ErrorStream(signing).CopyToAsync(errors);
        if (input is not null)
        {
            await signing.StandardInput.WriteAsync(input);
            await signing.StandardInput.FlushAsync();
        }
        signing.StandardInput.Close();
        await signing.WaitForExitAsync();
        await EndedAsync(signing, Task.WhenAll(written, said));
        return (signing.ExitCode, output.ToArray(), errors.ToArray());
    }

    /// <summary>gpg run as git asked, its input, output and errors git's own.</summary>
    private static async Task<int> ThroughAsync(string gpg, string[] args)
    {
        ProcessStartInfo start = ChildProcesses.Command(gpg, args);
        MsysGpg.Fit(start, gpg);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process through = await ChildProcesses.StartAsync(start);
        await using Stream input = Console.OpenStandardInput();
        await using Stream output = Console.OpenStandardOutput();
        await using Stream errors = Console.OpenStandardError();
        // Unheld: this program is git's gpg, with no window to wait for it.
        Task given = BackgroundWork.Unheld(async () =>
        {
            await input.CopyToAsync(through.StandardInput.BaseStream);
            through.StandardInput.Close();
        });
        Task copied = Task.WhenAll(ChildProcesses.OutputStream(through).CopyToAsync(output), ChildProcesses.ErrorStream(through).CopyToAsync(errors));
        await through.WaitForExitAsync();
        await EndedAsync(through, copied);
        // git's input to gpg is written whole before git reads its output, so what is left of it is nobody's.
        UnawaitedWork.Dismiss(given);
        return through.ExitCode;
    }

    /// <summary>A question put through the operation's socket as git's askpass helper puts it (<see cref="GitAskPass.Put"/>):
    /// the answer, or null for none, saying why on gpg's errors where the IDE gave none for want of itself.</summary>
    private static string? Ask(string socket, string prompt)
    {
        (string? answer, string? why) = GitAskPass.Put(socket, prompt, Waits.Of(GitAskPass.TakeLimit));
        if (why is not null)
        {
            Console.Error.WriteLine(GitAskPass.NoAnswer + why);
        }
        return answer;
    }
}
