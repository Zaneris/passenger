using Passenger.App.Containers;

namespace Passenger.Tests;

/// <summary>A running program's command line as the system gives it, shown to a person or an agent through the one mask
/// (<see cref="ContainerSecrets.CommandLines"/>): each word on its own, so a secret's value goes and the words after it stay.</summary>
[Collection(Beside.Name)]
public sealed class CommandLineSecretsTests
{
    private const string Masked = ContainerSecrets.Masked;

    /// <summary>A password, an API key, a connection string's password and a URL's are masked where they stand in the line,
    /// and every other word, the program's path and an option after a secret among them, shows as the system gave it.</summary>
    [Fact]
    public void A_command_lines_secrets_are_masked_word_by_word_and_its_other_words_kept()
    {
        Func<string, string> shown = ContainerSecrets.CommandLines();
        Assert.Equal(
            $"/usr/local/share/dotnet/dotnet bin/Shop.dll --Password={Masked} --ApiKey={Masked} --verbose",
            shown("/usr/local/share/dotnet/dotnet bin/Shop.dll --Password=x --ApiKey=y --verbose"));
        Assert.Equal(
            $"dotnet Shop.dll --ConnectionStrings:Db={Masked};Username=app;Password={Masked} --urls https://ada:{Masked}@db.example/shop",
            shown("dotnet Shop.dll --ConnectionStrings:Db=Host=db;Username=app;Password=hunter2 --urls https://ada:s3cret@db.example/shop"));
    }

    /// <summary>A word in double quotes, as the system quotes one holding a space, is masked inside its quotes, and one
    /// quoted within (Windows' <c>--password="two words"</c>) as a whole value; neither spills into the word after it.</summary>
    [Fact]
    public void A_quoted_words_secret_is_masked_whole_and_the_word_after_it_kept()
    {
        Func<string, string> shown = ContainerSecrets.CommandLines();
        Assert.Equal($"\"C:\\Program Files\\Shop\\Shop.exe\" --Password={Masked} --name \"two words\"",
            shown("\"C:\\Program Files\\Shop\\Shop.exe\" \"--Password=two words\" --name \"two words\""));
        Assert.Equal($"Shop.exe --password={Masked} --verbose", shown("Shop.exe --password=\"two words\" --verbose"));
    }

    /// <summary>A line with nothing to mask comes back as it was, the same string, its spacing and quotes as the system gave
    /// them.</summary>
    [Fact]
    public void A_command_line_with_no_secret_comes_back_as_it_was()
    {
        string line = "\"C:\\Program Files\\dotnet\\dotnet.exe\"  exec   Shop.dll --urls http://localhost:5000 --port=5000 \"\\\"quoted\\\" word\"";
        Assert.Same(line, ContainerSecrets.CommandLines()(line));
    }
}
