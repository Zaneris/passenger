using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A package.json script renamed in the scripts that run it: each <c>npm run</c> (Yarn's, pnpm's) of it, written
/// plain or quoted, takes the new name, quoted for the shell where the name needs it, so the call still runs the script
/// renamed and not another with an argument.</summary>
public class NpmScriptsTests
{
    private const string Scripts = """
        {
          "scripts": {
            "build": "tsc -p .",
            "all": "npm run build && npm run \"build\"",
            "ci": "yarn run 'build' -- --strict",
            "builder": "npm run builder-step",
            "watch": "pnpm run-script build"
          }
        }
        """;

    [Fact]
    public void A_name_with_a_space_is_written_quoted_in_every_run_plain_or_quoted()
    {
        string renamed = Apply(Scripts, "build", "build prod");
        Assert.Equal("""
            {
              "scripts": {
                "build": "tsc -p .",
                "all": "npm run \"build prod\" && npm run \"build prod\"",
                "ci": "yarn run 'build prod' -- --strict",
                "builder": "npm run builder-step",
                "watch": "pnpm run-script \"build prod\""
              }
            }
            """, renamed);
    }

    [Fact]
    public void A_name_the_shell_reads_whole_stays_as_each_run_wrote_it()
    {
        string renamed = Apply(Scripts, "build", "compile:all");
        Assert.Equal("""
            {
              "scripts": {
                "build": "tsc -p .",
                "all": "npm run compile:all && npm run \"compile:all\"",
                "ci": "yarn run 'compile:all' -- --strict",
                "builder": "npm run builder-step",
                "watch": "pnpm run-script compile:all"
              }
            }
            """, renamed);
    }

    [Fact]
    public void A_name_with_a_space_is_found_only_quoted_and_keeps_its_quotes_renamed()
    {
        string scripts = """{ "scripts": { "build prod": "tsc", "all": "npm run \"build prod\" && npm run build prod" } }""";
        Assert.Equal("""{ "scripts": { "build prod": "tsc", "all": "npm run \"release\" && npm run build prod" } }""", Apply(scripts, "build prod", "release"));
    }

    [Fact]
    public void A_name_holding_a_quote_goes_in_double_quotes_its_own_escaped()
    {
        Assert.Equal("""{ "a": "npm run \"it's\"" }""", Apply("""{ "a": "npm run 'build'" }""", "build", "it's"));
        Assert.Equal("""{ "a": "npm run \"say \\\"hi\\\"\"" }""", Apply("""{ "a": "npm run build" }""", "build", "say \"hi\""));
    }

    private static string Apply(string json, string script, string name)
    {
        string text = json;
        foreach ((int start, int length, string written) in NpmScripts.Renamed(json, script, name).OrderByDescending(e => e.Start))
        {
            text = text[..start] + written + text[(start + length)..];
        }
        return text;
    }
}
