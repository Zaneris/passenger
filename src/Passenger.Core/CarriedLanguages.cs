namespace Passenger.Core;

/// <summary>The languages an assembly carries words in beside English, by their tags, as its build finds its translations
/// (xlf/Name.fr.xlf beside each .resx; Passenger.Analyzers' WordsResources writes it).</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CarriedLanguagesAttribute(params string[] tags) : Attribute
{
    public IReadOnlyList<string> Tags { get; } = tags;
}
