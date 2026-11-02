using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A key a commit can be signed with, as Commit Signing… offers it: what git is given (user.signingkey: an SSH
/// key's public file or the key itself, a GPG key's id, an X.509 certificate's) and how it is shown.</summary>
public sealed record SigningKey(string Value, string Shown);

/// <summary>The keys Commit Signing… offers for a format, the person's own: for SSH the key they push with first (ssh -G's
/// identity file for the remote's host), then those an ssh-agent holds (ssh-add -L) and each in ~/.ssh with its private
/// half beside it; for GPG gpg's secret keys, for X.509 gpgsm's. Only public halves and lists are read, never a secret
/// key; a program not installed offers none.</summary>
public static class SigningKeys
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static async Task<IReadOnlyList<SigningKey>> FindAsync(SigningFormat format, Git? repository) => format switch
    {
        SigningFormat.Ssh => await SshAsync(repository),
        SigningFormat.X509 => await ListedAsync("gpgsm", "crs"),
        _ => await ListedAsync("gpg", "sec"),
    };

    private static async Task<IReadOnlyList<SigningKey>> SshAsync(Git? repository)
    {
        List<SigningKey> found = [];
        if (repository is not null && await repository.PushHostAsync() is { } host && await OutputAsync("ssh", ["-G", "--", host]) is { } options)
        {
            foreach (string line in options.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("identityfile ", StringComparison.OrdinalIgnoreCase) && Expanded(line[13..].Trim()) is { } file && File.Exists(file + ".pub"))
                {
                    found.Add(new SigningKey(file + ".pub", $"{Path.GetFileName(file)}.pub, which you push to {host} with"));
                }
            }
        }
        if (Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } && await OutputAsync("ssh-add", ["-L"]) is { } held)
        {
            foreach (string line in held.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("ssh-", StringComparison.Ordinal) || line.StartsWith("ecdsa-", StringComparison.Ordinal) || line.StartsWith("sk-", StringComparison.Ordinal))
                {
                    string[] words = line.Split(' ');
                    found.Add(new SigningKey($"key::{line}", $"{(words.Length > 2 ? string.Join(' ', words[2..]) : words[0])}, from the SSH agent"));
                }
            }
        }
        string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        try
        {
            foreach (string pub in Directory.Exists(ssh) ? Directory.EnumerateFiles(ssh, "*.pub").Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
            {
                if (File.Exists(pub[..^4]))
                {
                    found.Add(new SigningKey(pub, Path.GetFileName(pub)));
                }
            }
        }
        catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Commit Signing: ~/.ssh could not be listed: {unlisted.Message}");
        }
        return [.. found.DistinctBy(k => k.Value, PathNames.Comparer)];

        static string? Expanded(string file) => file.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[2..])
            : Path.IsPathRooted(file) ? file : null;
    }

    /// <summary>gpg's or gpgsm's secret keys from their --with-colons list: each key's line (sec, or crs for a certificate)
    /// gives its id, the user line after it whom it is of.</summary>
    private static async Task<IReadOnlyList<SigningKey>> ListedAsync(string program, string kind)
    {
        if (await OutputAsync(program, ["--list-secret-keys", "--with-colons"]) is not { } listed)
        {
            return [];
        }
        List<SigningKey> found = [];
        string? id = null;
        foreach (string line in listed.Split('\n', StringSplitOptions.TrimEntries))
        {
            string[] field = line.Split(':');
            if (field.Length > 4 && field[0] == kind)
            {
                id = field[4];
            }
            else if (field.Length > 9 && field[0] == "uid" && id is not null)
            {
                found.Add(new SigningKey(id, $"{field[9]} ({id})"));
                id = null;
            }
        }
        return found;
    }

    /// <summary>What a program wrote, or null where it is not installed or failed.</summary>
    private static async Task<string?> OutputAsync(string program, IReadOnlyList<string> arguments)
    {
        if (Programs.OnPath(OperatingSystem.IsWindows() ? program + ".exe" : program) is not { } found)
        {
            return null;
        }
        try
        {
            ChildResult ran = await ChildProcesses.RunAsync(ChildProcesses.Command(found, arguments), limit: Limit);
            return ran.Succeeded ? ran.Output : null;
        }
        catch (Exception failed) when (failed is TimeoutException or System.ComponentModel.Win32Exception)
        {
            IdeErrors.Write($"Commit Signing: {program} gave no keys: {failed.Message}");
            return null;
        }
    }
}
