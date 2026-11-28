namespace Passenger.Core;

public static partial class Plural
{
    /// <summary>Words as a sentence lists them, for a message's argument: in English as <see cref="Listed(IReadOnlyList{string})"/>
    /// lists them, in a carried language by its own patterns; names given through <see cref="Words.Names"/>.</summary>
    public static Words Listed(IReadOnlyList<Words> items) => Words.Of(new SaidList([.. items], Either: false));

    /// <summary>Words as a sentence offers a choice of them, as <see cref="Listed(IReadOnlyList{Words})"/> lists them.</summary>
    public static Words Either(IReadOnlyList<Words> items) => Words.Of(new SaidList([.. items], Either: true));

    private sealed record SaidList(Words[] Items, bool Either) : ISaid
    {
        public string In(UiLanguage language)
        {
            string[] said = [.. Items.Select(i => i.In(language))];
            return (language.Reached is { } reached ? LanguagePatterns.Joined(reached, said, Either) : null)
                ?? (Either ? Plural.Either(said) : Listed(said));
        }

        public bool Equals(SaidList? other) => other is not null && Either == other.Either && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => HashCode.Combine(Either, Items.Length);
    }
}
