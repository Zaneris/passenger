namespace Passenger.App.Templates;

/// <summary>What New Solution says under a format list of .sln alone, which a list of one leaves unsaid.</summary>
public static class SolutionFormatNote
{
    /// <summary>Why the formats are .sln alone: an SDK before 9.0.200 makes no .slnx, and a later one whose solution template
    /// takes no format (an install or template cache out of step) makes none either; empty where .slnx is offered.</summary>
    public static string Of(string sdk, IReadOnlyList<string> formats)
    {
        if (formats is not [".sln"])
        {
            return string.Empty;
        }
        int dash = sdk.IndexOf('-', StringComparison.Ordinal);
        bool before = Version.TryParse(dash < 0 ? sdk : sdk[..dash], out Version? version) && version < new Version(9, 0, 200);
        return before ? $"SDK {sdk} makes no .slnx: SDK 9.0.200 and later do"
            : $"dotnet new sln of SDK {sdk} offers no .slnx here: its solution template takes no format";
    }
}
