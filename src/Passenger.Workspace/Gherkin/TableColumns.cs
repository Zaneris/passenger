using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace.Gherkin;

/// <summary>Which names of a step's table (its header's cells, or a vertical table's first column) Reqnroll's table helpers
/// set no member from, ported from Reqnroll 3's TEHelpers and TableHelperExtensionMethods: a name takes a member where its
/// letters, digits and underscores, and the member's name, read alike (underscores left out, accents taken off as Reqnroll
/// names identifiers, a leading digit given an underscore, any letter case), or where it matches one of the member's
/// <c>[TableAliases]</c>. <c>CreateSet</c> reads its header; <c>CreateInstance</c> a table of one row by its header unless
/// it is a field and value table (a header of Field and Value, or two columns whose row names a property), any other by
/// its first column, the header left out.</summary>
public static partial class TableColumns
{
    /// <summary>The names of a table no member of the type a read makes takes, each with its row.</summary>
    public static IEnumerable<(FeatureRow Row, FeatureCell Cell)> Unread(FeatureTable table, TableRead read)
    {
        if (table.Rows.Count == 0)
        {
            return [];
        }
        FeatureRow header = table.Rows[0];
        IEnumerable<(FeatureRow Row, FeatureCell Cell)> names = read.Helper == "CreateInstance" && IsVertical(table, read)
            ? table.Rows.Skip(1).Where(r => r.Cells.Count > 0).Select(r => (r, r.Cells[0]))
            : header.Cells.Select(c => (header, c));
        return names.Where(n => !read.Members.Any(m => Takes(m, n.Item2.Value)));
    }

    /// <summary>Why a column is passed over, in words saying what the type's members are.</summary>
    public static string Why(TableRead read, string column) =>
        $"{read.Helper}<{TypeNames.Short(read.Type)}> sets no member from \"{column}\", so it is passed over: "
        + (read.Members.Count == 0 ? $"{TypeNames.Short(read.Type)} has no public property or field" : $"{TypeNames.Short(read.Type)} has {Plural.Listed([.. read.Members.Select(m => m.Name)])}")
        + " (a column names one in any letter case, spaces and other characters left out)";

    /// <summary>Whether a table's name sets a member, as Reqnroll matches a column's name to a member's.</summary>
    public static bool Takes(TableMember member, string column) =>
        string.Equals(Normalized(member.Name), Normalized(NotInNames().Replace(column, "")), StringComparison.OrdinalIgnoreCase)
        || member.Aliases.Any(alias => Aliases(alias, column));

    /// <summary>Whether CreateInstance reads a table as fields and values, one row each: a header of Field and Value, any
    /// number of rows but one, or two columns whose one row's first cell names a property.</summary>
    private static bool IsVertical(FeatureTable table, TableRead read)
    {
        IReadOnlyList<FeatureCell> header = table.Rows[0].Cells;
        if (header is [{ Value: "Field" }, { Value: "Value" }])
        {
            return true;
        }
        return table.Rows.Count != 2
            || (header.Count == 2 && table.Rows[1].Cells.FirstOrDefault()?.Value is { } first && read.Members.Any(m => m.Property && Takes(m, first)));
    }

    private static bool Aliases(string alias, string column)
    {
        try
        {
            return Regex.IsMatch(column, alias, RegexOptions.None, TextSearch.Apart);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>A name as Reqnroll compares it: its underscores left out, then made an identifier as Reqnroll makes one
    /// (quotes, characters no identifier holds and letter case aside, accents taken off, a leading digit given an
    /// underscore).</summary>
    private static string Normalized(string name)
    {
        StringBuilder made = new();
        foreach (char each in name.Replace("_", "", StringComparison.Ordinal))
        {
            if (IdentifierPart().IsMatch(each.ToString()))
            {
                made.Append(Accents.TryGetValue(each, out string? plain) ? plain : each.ToString());
            }
        }
        return made.Length > 0 && char.IsDigit(made[0]) ? "_" + made : made.ToString();
    }

    /// <summary>The Latin letters Reqnroll writes for accented ones in an identifier.</summary>
    private static readonly Dictionary<char, string> Accents = new()
    {
        ['À'] = "A", ['Á'] = "A", ['Â'] = "A", ['Ã'] = "A", ['Ä'] = "A", ['Å'] = "A", ['Æ'] = "AE",
        ['Ç'] = "C", ['È'] = "E", ['É'] = "E", ['Ê'] = "E", ['Ë'] = "E", ['Ì'] = "I", ['Í'] = "I",
        ['Î'] = "I", ['Ï'] = "I", ['Ð'] = "D", ['Ñ'] = "N", ['Ò'] = "O", ['Ó'] = "O", ['Ô'] = "O",
        ['Õ'] = "O", ['Ö'] = "O", ['Ø'] = "O", ['Ù'] = "U", ['Ú'] = "U", ['Û'] = "U", ['Ü'] = "U",
        ['Ý'] = "Y", ['ß'] = "B", ['à'] = "a", ['á'] = "a", ['â'] = "a", ['ã'] = "a", ['ä'] = "a",
        ['å'] = "a", ['æ'] = "ae", ['ç'] = "c", ['è'] = "e", ['é'] = "e", ['ê'] = "e", ['ë'] = "e",
        ['ì'] = "i", ['í'] = "i", ['î'] = "i", ['ï'] = "i", ['ñ'] = "n", ['ò'] = "o", ['ó'] = "o",
        ['ô'] = "o", ['õ'] = "o", ['ö'] = "o", ['ø'] = "o", ['ù'] = "u", ['ú'] = "u", ['û'] = "u",
        ['ü'] = "u", ['ý'] = "y", ['ÿ'] = "y", ['Ą'] = "A", ['Ł'] = "L", ['Ľ'] = "L", ['Ś'] = "S",
        ['Š'] = "S", ['Ş'] = "S", ['Ť'] = "T", ['Ź'] = "Z", ['Ž'] = "Z", ['Ż'] = "Z", ['ą'] = "a",
        ['ł'] = "l", ['ľ'] = "l", ['ś'] = "s", ['š'] = "s", ['ş'] = "s", ['ť'] = "t", ['ź'] = "z",
        ['ž'] = "z", ['ż'] = "z", ['Ŕ'] = "R", ['Ă'] = "A", ['Ĺ'] = "L", ['Ć'] = "C", ['Č'] = "C",
        ['Ę'] = "E", ['Ě'] = "E", ['Ď'] = "D", ['Đ'] = "D", ['Ń'] = "N", ['Ň'] = "N", ['Ő'] = "O",
        ['Ř'] = "R", ['Ů'] = "U", ['Ű'] = "U", ['Ţ'] = "T", ['ŕ'] = "r", ['ă'] = "a", ['ĺ'] = "l",
        ['ć'] = "c", ['č'] = "c", ['ę'] = "e", ['ě'] = "e", ['ď'] = "d", ['đ'] = "d", ['ń'] = "n",
        ['ň'] = "n", ['ő'] = "o", ['ř'] = "r", ['ů'] = "u", ['ű'] = "u", ['ţ'] = "t",
    };

    // What a column's name keeps before it is compared: letters, digits and underscores.
    [GeneratedRegex(@"[^\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Nl}\p{Nd}_]")]
    private static partial Regex NotInNames();

    // What an identifier keeps (the underscore, a connector, among them).
    [GeneratedRegex(@"^[\p{Ll}\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{Nl}\p{Nd}\p{Pc}]$")]
    private static partial Regex IdentifierPart();
}
