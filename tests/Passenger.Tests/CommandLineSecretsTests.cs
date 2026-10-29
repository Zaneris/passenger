using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A command line shown to a person or an agent through the one mask: a running program's as the system gives it
/// (<see cref="ContainerSecrets.CommandLines"/>), one Passenger starts (<see cref="ContainerSecrets.CommandLine"/>), one a
/// program echoes (<see cref="ContainerSecrets.InSaid"/>) and an Aspire resource's arguments: each word on its own, so a
/// secret's value goes and the words after it stay, and the word after a flag naming a secret goes too.</summary>
[Collection(Beside.Name)]
public sealed class CommandLineSecretsTests
{
    private const string Masked = ContainerSecrets.Masked;

    /// <summary>A secret given as the word after its flag (<c>--password hunter2</c>, <c>--api-key sk-live-123</c>, PuTTY's
    /// <c>-pw</c>) is masked where it stands, in double quotes still when it was, its spacing kept; a flag that says it takes
    /// no value leaves the next flag as it is, which is read in turn.</summary>
    [Fact]
    public void A_running_programs_secret_given_as_the_word_after_its_flag_is_masked_and_its_other_words_kept()
    {
        Func<string, string> shown = ContainerSecrets.CommandLines();
        Assert.Equal(
            $"dotnet bin/Shop.dll --password {Masked} --api-key {Masked} --port 5000 --project App.csproj --verbose",
            shown("dotnet bin/Shop.dll --password hunter2 --api-key sk-live-123 --port 5000 --project App.csproj --verbose"));
        Assert.Equal($"\"C:\\Tools\\plink.exe\"  -pw  \"{Masked}\"  \"--token\" {Masked} --secret \"\" ada@db.example",
            shown("\"C:\\Tools\\plink.exe\"  -pw  \"two words\"  \"--token\" sk-live-123 --secret \"\" ada@db.example"));
        Assert.Equal($"docker login --password-stdin --username ada --no-password --token {Masked} registry.example",
            shown("docker login --password-stdin --username ada --no-password --token -sk-live-123 registry.example"));
    }

    /// <summary>A word after a flag that names no secret stays as the system gave it, and so does one after a flag of one
    /// letter, which names nothing the rule can read: <c>-p</c> is a port to psql and ssh, a Compose project to docker
    /// compose and a password only to some.</summary>
    [Fact]
    public void A_word_after_a_flag_naming_no_secret_or_of_one_letter_comes_back_as_it_was()
    {
        string line = "dotnet run --port 5000 --project App.csproj --keymap vs --monkey business -p 5432 -P -k value";
        Assert.Same(line, ContainerSecrets.CommandLines()(line));
        string said = "docker compose -p shop up --build --port 5000 -- --hotkey f5";
        Assert.Same(said, ContainerSecrets.InSaid(said));
    }

    /// <summary>A command Passenger starts, as its log and consoles echo it (<see cref="ChildProcesses.Shown"/>), has the word
    /// after a flag naming a secret masked whatever it holds, and the rest as it runs; an empty one shows as it is.</summary>
    [Fact]
    public void A_command_passenger_starts_has_the_word_after_a_secrets_flag_masked_in_its_echo()
    {
        Assert.Equal($"docker login --username ada --password {Masked} registry.example",
            ContainerSecrets.CommandLine(ChildProcesses.Command("docker", ["login", "--username", "ada", "--password", "hunter2", "registry.example"])));
        Assert.Equal($"dotnet nuget add source https://feed.example/v3/index.json --name feed --api-key {Masked} --configfile nuget.config",
            ContainerSecrets.CommandLine(ChildProcesses.Command("dotnet",
                ["nuget", "add", "source", "https://feed.example/v3/index.json", "--name", "feed", "--api-key", "two words", "--configfile", "nuget.config"])));
        Assert.Equal("docker compose -p shop up --password \"\" --port 5000",
            ContainerSecrets.CommandLine(ChildProcesses.Command("docker", ["compose", "-p", "shop", "up", "--password", "", "--port", "5000"])));
    }

    /// <summary>A command a program echoes (BuildKit's word that a <c>RUN</c> failed, an Aspire resource's arguments) has the
    /// word after a flag naming a secret masked, the quote after it kept, and the rest of the line as it was.</summary>
    [Fact]
    public void A_command_a_program_echoes_has_the_word_after_a_secrets_flag_masked()
    {
        Assert.Equal(
            $"failed to solve: process \"/bin/sh -c dotnet nuget add source https://feed.example/v3/index.json --username ada --password {Masked}\" did not complete successfully: exit code: 1",
            ContainerSecrets.InSaid("failed to solve: process \"/bin/sh -c dotnet nuget add source https://feed.example/v3/index.json --username ada --password hunter2\" did not complete successfully: exit code: 1"));
        Assert.Equal($"tool --password-stdin --token '{Masked}' --verbose\nnext --api-key {Masked}",
            ContainerSecrets.InSaid("tool --password-stdin --token 'two words' --verbose\nnext --api-key sk-live-123"));
        string[] arguments = ["run", "--api-key", Masked, "--port", "5000", "--password", ""];
        Assert.Equal(arguments, ContainerSecrets.Arguments(["run", "--api-key", "sk-live-123", "--port", "5000", "--password", ""]));
    }

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
