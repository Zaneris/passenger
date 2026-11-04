using Passenger.Core;

namespace Passenger.App;

/// <summary>A run configuration the command line starts once the folder's configurations are read, as Run (<c>--run
/// [NAME]</c>) or Debug (<c>--debug [NAME]</c>) starts the one chosen: the configuration named as Edit Configurations
/// names it, letter case aside, else the one chosen. Read and checked here from the words alone; a name none of the
/// folder's configurations has is refused naming those it has, as a malformed flag is.</summary>
public sealed record ConfigurationStart(string? Name, bool Debug)
{
    /// <summary>The flag that asks for it.</summary>
    public string Flag => Debug ? "--debug" : "--run";

    /// <summary>What it does to the configuration, as Run's and Debug's refusals say it.</summary>
    public string Verb => Debug ? "debug" : "run";

    /// <summary>Why it cannot start where Passenger opens no folder: the configuration's name, if any, comes after the flag
    /// and the folder before it, so a folder given only after the flag was taken for the name.</summary>
    public string NoFolder =>
        $"{Flag} starts a run configuration of the folder Passenger opens, and none is given: passenger FOLDER {Flag} [NAME], the folder first";

    /// <summary>The start a <c>--run</c> or <c>--debug</c> at <paramref name="at"/> asks for, its name the word after it
    /// unless that is another flag or empty, which <paramref name="at"/> then moves past.</summary>
    public static ConfigurationStart Read(IReadOnlyList<string> args, ref int at)
    {
        bool debug = args[at] == "--debug";
        if (at + 1 < args.Count && args[at + 1].Length > 0 && !args[at + 1].StartsWith('-'))
        {
            at++;
            return new ConfigurationStart(args[at], debug);
        }
        return new ConfigurationStart(null, debug);
    }

    /// <summary>Null where the folder (by its <paramref name="shown"/> name) has the configuration named among its
    /// <paramref name="names"/>, or any to choose from where none is named; else why not, listing the names it has.</summary>
    public string? WhyNot(string shown, IReadOnlyList<string> names)
    {
        string asked = Name is null ? Flag : $"{Flag} '{Name}'";
        if (names.Count == 0)
        {
            return $"{asked}: {shown} has no run configuration to {Verb}";
        }
        return Name is null || names.Any(n => string.Equals(n, Name, StringComparison.OrdinalIgnoreCase)) ? null
            : $"{asked} names no run configuration of {shown}, whose configurations are {Plural.Listed([.. names.Select(n => $"'{n}'")])}";
    }
}
