using System.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>A commit's signature read as the Log reads it: which commits of a batch are signed and in which format, from
/// their gpgsig headers' armour, then git's verdict on each said in a sentence that begins with what it is worth.</summary>
public class CommitSignatureTests
{
    private const string Tree = "tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\nauthor A <a@example.org> 1700000000 +0000\ncommitter A <a@example.org> 1700000000 +0000\n";

    [Fact]
    public void A_batch_names_the_signed_commits_by_their_armour_and_passes_over_the_unsigned_missing_and_unknown()
    {
        string batch = Object("1111", Tree + "gpgsig -----BEGIN SSH SIGNATURE-----\n U1NIU0lH\n -----END SSH SIGNATURE-----\n\nSigned\n")
            + Object("2222", Tree + "\nUnsigned, though its message says\ngpgsig -----BEGIN PGP SIGNATURE-----\n")
            + "3333 missing\n"
            + Object("4444", Tree + "gpgsig-sha256 -----BEGIN PGP SIGNATURE-----\n iQEz\n -----END PGP SIGNATURE-----\n\nSHA-256\n")
            + Object("5555", Tree + "gpgsig -----BEGIN SIGNED MESSAGE-----\n MIIB\n -----END SIGNED MESSAGE-----\n\nX.509\n")
            + Object("6666", Tree + "gpgsig -----BEGIN SOMETHING ELSE-----\n -----END SOMETHING ELSE-----\n\nUnknown\n");
        Dictionary<string, SigningFormat> formats = CommitSignature.Formats(Encoding.UTF8.GetBytes(batch));
        Assert.Equal(["1111 Ssh", "4444 OpenPgp", "5555 X509"], formats.Select(f => $"{f.Key} {f.Value}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Gits_verdicts_are_read_for_the_signed_and_said_with_the_signer_and_key()
    {
        Dictionary<string, SigningFormat> formats = new() { ["1111"] = SigningFormat.Ssh, ["2222"] = SigningFormat.Ssh, ["3333"] = SigningFormat.OpenPgp, ["4444"] = SigningFormat.OpenPgp };
        string output = string.Concat(new[]
        {
            ["1111", "G", "me@example.org", "SHA256:abc"],
            ["2222", "U", string.Empty, "SHA256:def"],
            ["3333", "N", string.Empty, string.Empty],
            ["4444", "B", "Me <me@example.org>", "0123ABCD"],
            new[] { "9999", "N", string.Empty, string.Empty },
        }.Select(fields => string.Join('\u001f', fields) + "\u001e\n"));
        Dictionary<string, CommitSignature> verdicts = CommitSignature.Verdicts(output, formats, "error: cannot run gpg: No such file or directory\n");
        Assert.Equal(4, verdicts.Count);
        Assert.Equal((SignatureState.Good, "Good SSH signature by me@example.org, key SHA256:abc"), (verdicts["1111"].State, verdicts["1111"].Said));
        Assert.Equal((SignatureState.Unverified, "Unverified SSH signature, key SHA256:def: no allowed signers file lists its signer"),
            (verdicts["2222"].State, verdicts["2222"].Said));
        Assert.Equal((SignatureState.Unverified, "Unverified GPG signature: git could not check it, as cannot run gpg: No such file or directory"),
            (verdicts["3333"].State, verdicts["3333"].Said));
        Assert.Equal((SignatureState.Bad, "Bad GPG signature by Me <me@example.org>, key 0123ABCD: the commit is not what was signed"),
            (verdicts["4444"].State, verdicts["4444"].Said));
    }

    [Fact]
    public void Each_verdict_has_its_state_and_words()
    {
        CommitSignature Of(char verdict) => new(SigningFormat.OpenPgp, verdict, "Me", "ABCD");
        Assert.Equal(
            [
                "G Good Good GPG signature by Me, key ABCD",
                "U Unverified Unverified GPG signature by Me, key ABCD: the key is not trusted",
                "E Unverified Unverified GPG signature, key ABCD: it cannot be checked, as the key is not in the keyring",
                "B Bad Bad GPG signature by Me, key ABCD: the commit is not what was signed",
                "X Expired Expired GPG signature by Me, key ABCD",
                "Y Expired Expired GPG signature by Me, key ABCD: the key has expired",
                "R Bad Bad GPG signature by Me, key ABCD: the key was revoked",
            ],
            "GUEBXYR".Select(v => $"{v} {Of(v).State} {Of(v).Said}"));
        Assert.Equal("X.509", new CommitSignature(SigningFormat.X509, 'G', null, null).Kind);
    }

    private static string Object(string hash, string body) => $"{hash} commit {Encoding.UTF8.GetByteCount(body)}\n{body}\n";
}
