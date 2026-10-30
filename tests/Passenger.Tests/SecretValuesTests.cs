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
}
