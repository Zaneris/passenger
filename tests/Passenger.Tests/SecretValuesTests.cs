using Passenger.App.Containers;

namespace Passenger.Tests;

/// <summary>Values shown to a person or an agent through the one mask (<see cref="ContainerSecrets"/>): what a program
/// says in its logs and failures, a variable's value and a URL, each secret in them masked whole.</summary>
[Collection(Beside.Name)]
public sealed class SecretValuesTests
{
    private const string Masked = ContainerSecrets.Masked;

    /// <summary>A quoted password holding an escaped quote is masked to its real closing quote, as a JSON member and as a
    /// <c>NAME="value"</c>, the words after it kept; one whose quote no other closes is masked to that quote.</summary>
    [Fact]
    public void A_quoted_password_holding_an_escaped_quote_is_masked_whole()
    {
        Assert.Equal($$"""{"user": "ada", "password": "{{Masked}}", "host": "db"}""",
            ContainerSecrets.InSaid("""{"user": "ada", "password": "abc\"def", "host": "db"}"""));
        Assert.Equal($"""login failed: user="ada", password="{Masked}", host=db""",
            ContainerSecrets.InSaid("""login failed: user="ada", password="abc\"def", host=db"""));
        Assert.Equal($"""tool --password "{Masked}" --verbose""", ContainerSecrets.InSaid("""tool --password "abc\"def" --verbose"""));
        Assert.Equal($"""login failed: user="ada", password="{Masked}" """, ContainerSecrets.InSaid("""login failed: user="ada", password="C:\" """));
    }

    /// <summary>A signed URL's signature grants what the URL reaches, so an Azure SAS's sig and an S3 or GCS presigned URL's
    /// signature and session token are masked as a password is: dropped from a URL Passenger offers to open, and masked in a
    /// container's or Aspire resource's variable, in what a program says and in a connection string's SAS, the rest kept.</summary>
    [Fact]
    public void A_signed_urls_signature_is_masked_in_a_url_offered_a_variables_value_and_what_a_program_says()
    {
        const string Sas = "https://shop.blob.core.windows.net/orders/a.json?sv=2025-01-05&sp=r&se=2026-12-31T00%3A00%3A00Z&sr=b&sig=SasSig0123%2Bab%3D";
        const string S3 = "https://shop.s3.us-east-1.amazonaws.com/a.json?X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + "&X-Amz-Credential=AKIAEXAMPLE%2F20261030%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20261030T000000Z&X-Amz-Expires=3600"
            + "&X-Amz-SignedHeaders=host&X-Amz-Security-Token=S3Token0123&X-Amz-Signature=S3Sig0123abcdef";
        const string Gcs = "https://storage.googleapis.com/shop/a.json?X-Goog-Algorithm=GOOG4-RSA-SHA256&X-Goog-Expires=900&X-Goog-Signature=GcsSig0123";
        string[] signatures = ["SasSig0123", "S3Sig0123", "S3Token0123", "AKIAEXAMPLE", "GcsSig0123"];
        foreach (string url in (string[])[Sas, S3, Gcs])
        {
            string offered = ContainerSecrets.PublicHttpUrl(url)!;
            Assert.True(!signatures.Any(s => offered.Contains(s, StringComparison.Ordinal)), $"A URL offered to open, {offered}, holds its signature");
            Assert.True(offered.StartsWith(url[..url.IndexOf('?', StringComparison.Ordinal)], StringComparison.Ordinal),
                $"A signed URL offered to open lost its address: {offered}");
            string entry = ContainerSecrets.Entry($"BLOB_URL={url}");
            Assert.True(!signatures.Any(s => entry.Contains(s, StringComparison.Ordinal)), $"A container's variable shows its signature: {entry}");
            string said = ContainerSecrets.InSaid($"GET {url} failed: 403");
            Assert.True(!signatures.Any(s => said.Contains(s, StringComparison.Ordinal)), $"A program's log shows a signature: {said}");
        }
        Assert.Equal($"BLOB_URL={Sas[..Sas.IndexOf("&sig=", StringComparison.Ordinal)]}&sig={Masked}", ContainerSecrets.Entry($"BLOB_URL={Sas}"));
        Assert.Equal(Sas[..Sas.IndexOf("&sig=", StringComparison.Ordinal)], ContainerSecrets.PublicHttpUrl(Sas));
        Assert.Contains("&X-Amz-Expires=3600&X-Amz-SignedHeaders=host&", ContainerSecrets.Entry($"BLOB_URL={S3}"), StringComparison.Ordinal);
        Assert.Equal($"BlobEndpoint=https://shop.blob.core.windows.net/;SharedAccessSignature={Masked}",
            ContainerSecrets.InValue("BlobEndpoint=https://shop.blob.core.windows.net/;SharedAccessSignature=sv=2025-01-05&sig=SasSig0123"));
        Assert.Equal(Masked, ContainerSecrets.Shown("AZURE_STORAGE_SAS_SIGNATURE", "SasSig0123"));
    }
}
