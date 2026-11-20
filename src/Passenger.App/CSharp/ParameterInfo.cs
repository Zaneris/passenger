using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A language's own Parameter Info, asked before its server's.</summary>
internal interface IOwnSignatures
{
    /// <summary>The card of the call an offset of a file is in, added to <paramref name="card"/>: true where it is shown,
    /// false where there is no call to show, null where the language passes it to its server.</summary>
    Task<bool?> SignAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel);
}

/// <summary>Parameter Info: the card over the caret of the signature of the call it is in, its parameter there's
/// documentation (else the signature's), shown after an opening bracket or a comma, following the caret moved through the
/// call, and gone after the closing bracket or once the caret leaves it; a language's own (<see cref="Own"/>) first where
/// it has one. A screen reader is told the signature as it shows, then the parameter the caret is at as it moves to another.</summary>
internal sealed class ParameterInfo
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly InfoCard card = new("Parameter Info");
    private CancellationTokenSource? signing;

    /// <summary>The signature and the parameter a screen reader was last told, until the card goes; a language's own card's
    /// words in place of the signature.</summary>
    private (string Signature, string? Parameter)? told;

    /// <summary>The card asked for again once the caret moved through the call rests a moment.</summary>
    private readonly Debounce following;

    /// <summary>The text's version the caret was last seen moving in, so a caret moved alone is told from one moved by typing.</summary>
    private int seen = -1;

    public ParameterInfo(AssistContext context, PopupPlacement popups)
    {
        this.context = context;
        this.popups = popups;
        popups.Add(card);
        following = new Debounce(TimeSpan.FromMilliseconds(100), () => card.IsVisible ? Show() : Task.CompletedTask);
    }

    /// <summary>The caret moved: moved alone while the card shows, the card follows it to the parameter it is at, or goes
    /// once it is out of the call; a move by typing is the typed character's (<see cref="AfterTyped"/>).</summary>
    public void CaretMoved()
    {
        int version = context.Editor.Document.Version;
        bool typing = version != seen;
        seen = version;
        if (card.IsVisible && !typing)
        {
            following.Restart();
        }
    }

    public bool IsVisible => card.IsVisible;

    /// <summary>The language that answers before the server, for the files it takes.</summary>
    public IOwnSignatures? Own { get; set; }

    /// <summary>What the card says, while it shows.</summary>
    public string? Text => card.IsVisible ? card.Text : null;

    /// <summary>The card while it shows.</summary>
    public Avalonia.Controls.Border? Card => card.IsVisible ? card : null;

    /// <summary>The card shown after a call's bracket or comma just typed, and gone after its closing bracket or the
    /// statement's end.</summary>
    public void AfterTyped(char last)
    {
        if (last is '(' or ',')
        {
            IdeErrors.Observe(Show());
        }
        else if (last is ')' or ';')
        {
            Hide();
        }
    }

    /// <summary>The card gone, and the signature still coming with it unless only the card is let go (Escape's).</summary>
    public void Hide(bool cancel = true)
    {
        if (cancel)
        {
            signing?.Cancel();
        }
        following.Stop();
        card.IsVisible = false;
        told = null;
    }

    public async Task Show()
    {
        using (BackgroundWork.Scope())
        {
            await ShowCore();
        }
    }

    private async Task ShowCore()
    {
        int caret = context.Caret;
        TextDocument document = context.Editor.Document;
        if (Own is { } own && context.Editors.Active is { } shown)
        {
            signing?.Cancel();
            CancellationTokenSource asked = new();
            signing = asked;
            Gathered parts = new();
            bool? signed;
            try
            {
                signed = await own.SignAsync(parts, shown, caret, asked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (asked.IsCancellationRequested || context.Editor.Document != document)
            {
                return;
            }
            if (signed is { } answered)
            {
                card.IsVisible = answered && parts.Count > 0;
                if (card.IsVisible)
                {
                    card.Clear();
                    parts.Into(card);
                    popups.Place(card, caret, above: true);
                    Tell(card.Text, null, null);
                }
                else
                {
                    told = null;
                }
                return;
            }
        }
        if (context.Current?.Lsp is not { } lsp)
        {
            return;
        }
        signing?.Cancel();
        CancellationTokenSource cancel = new();
        signing = cancel;
        try
        {
            SignatureHelp? help = await lsp.SignatureHelpAsync(caret, cancel.Token);
            // A late answer is for a call the caret has left, or a file no longer shown.
            if (cancel.IsCancellationRequested || context.Editor.Document != document)
            {
                return;
            }
            if (help is null || help.Signatures.Count == 0)
            {
                card.IsVisible = false;
                told = null;
                return;
            }
            int overload = Math.Clamp(help.ActiveSignature ?? 0, 0, help.Signatures.Count - 1);
            SignatureInformation signature = help.Signatures[overload];
            card.Clear();
            card.AddServed(signature.Label, signature.Code, context.Editors.Active is { } file && file.Text == document ? file.Language : null);
            int active = signature.ActiveParameter ?? help.ActiveParameter ?? -1;
            List<string> names = [.. ParameterNames(signature)];
            if (signature.Parameters is { } parameters && active >= 0 && active < parameters.Count
                && Shapes.Text(parameters[active].Documentation) is { Length: > 0 } about)
            {
                card.AddText(SentencesApart(about, names));
            }
            else if (Shapes.Text(signature.Documentation) is { Length: > 0 } documentation)
            {
                card.AddText(SentencesApart(documentation, names));
            }
            card.IsVisible = true;
            popups.Place(card, caret, above: true);
            Tell(signature.Label, help.Signatures.Count > 1 ? $"overload {overload + 1} of {help.Signatures.Count}" : null, ParameterLabel(signature, active));
        }
        catch (OperationCanceledException)
        {
            // The caret left the call, or asked again.
        }
        catch (LanguageServerFailed)
        {
            // No signature shown; the next bracket or comma asks again.
        }
    }

    /// <summary>Tells a screen reader the card shown: the whole signature (with which of its overloads, and the parameter
    /// the caret is at) where another shows than it was told, else the parameter alone where the caret is at another.</summary>
    private void Tell(string signature, string? overload, string? parameter)
    {
        string? said = told is not { } before || before.Signature != signature
            ? string.Join(", ", new[] { "Parameter Info", signature, overload, parameter is null ? null : $"parameter {parameter}" }.OfType<string>())
            : parameter is not null && parameter != before.Parameter ? $"parameter {parameter}" : null;
        told = (signature, parameter);
        if (said is not null)
        {
            popups.Tell(said, Urgency.Polite, card);
        }
    }

    /// <summary>A signature's parameter as its label shows it ("int value"), whether the server gives its words or where
    /// they stand in the signature's label; null where none is at that place.</summary>
    private static string? ParameterLabel(SignatureInformation signature, int at)
    {
        if (signature.Parameters is not { } parameters || at < 0 || at >= parameters.Count)
        {
            return null;
        }
        JsonElement label = parameters[at].Label;
        string? text = label.ValueKind switch
        {
            JsonValueKind.String => label.GetString(),
            JsonValueKind.Array when label.GetArrayLength() == 2 && label[0].TryGetInt32(out int start) && label[1].TryGetInt32(out int end)
                && 0 <= start && start <= end && end <= signature.Label.Length => signature.Label[start..end],
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>A card's parts as a language gives them, kept until the answer is known to be the one to show.</summary>
    private sealed class Gathered : IHoverCard
    {
        private readonly List<Action<InfoCard>> parts = [];

        public int Count => parts.Count;

        public void AddCode(string code, Languages.Language? language = null) => parts.Add(card => card.AddCode(code, language));

        public void AddText(string text) => parts.Add(card => card.AddText(text));

        public void AddHover(Hover hover) => parts.Add(card => card.AddHover(hover));

        public void Into(InfoCard card)
        {
            foreach (Action<InfoCard> part in parts)
            {
                part(card);
            }
        }
    }

    /// <summary>Documentation with the space back where its XML put a reference to one of the signature's parameters
    /// straight after a sentence's full stop, which the server's plain text runs into the word before: .NET's
    /// String.Join(String, IEnumerable) says "a separator.&lt;paramref name="separator"/&gt; is included", which read
    /// "a separator.separator is included".</summary>
    private static string SentencesApart(string text, IEnumerable<string> parameters) =>
        parameters.Distinct(StringComparer.Ordinal).Aggregate(text, (said, name) => Regex.Replace(
            said, $@"(?<=\p{{Ll}}\.){Regex.Escape(name)}\b", " " + name, RegexOptions.CultureInvariant));

    /// <summary>A signature's parameters' names, whether the server labels each by its name (Roslyn), as "name: type"
    /// (TypeScript) or by where it is in the signature's label.</summary>
    private static IEnumerable<string> ParameterNames(SignatureInformation signature)
    {
        for (int at = 0; at < (signature.Parameters?.Count ?? 0); at++)
        {
            string text = ParameterLabel(signature, at) ?? string.Empty;
            int colon = text.IndexOf(':', StringComparison.Ordinal);
            // TypeScript's "name?: type" names it before the colon, C#'s "type name" last.
            Match name = Regex.Match(colon >= 0 ? text[..colon] : text, @"[\p{L}_][\p{L}\p{Nd}_]*", RegexOptions.RightToLeft);
            if (name.Success)
            {
                yield return name.Value;
            }
        }
    }
}
