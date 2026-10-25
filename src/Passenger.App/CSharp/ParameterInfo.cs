using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Parameter Info: the card over the caret of the signature of the call it is in, its parameter there's
/// documentation (else the signature's), shown after an opening bracket or a comma and gone after the closing one.</summary>
internal sealed class ParameterInfo
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly InfoCard card = new();
    private CancellationTokenSource? signing;

    public ParameterInfo(AssistContext context, PopupPlacement popups)
    {
        this.context = context;
        this.popups = popups;
        popups.Add(card);
    }

    public bool IsVisible => card.IsVisible;

    /// <summary>What the card says, while it shows.</summary>
    public string? Text => card.IsVisible ? card.Text : null;

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
        card.IsVisible = false;
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
        if (context.Current?.Lsp is not { } lsp)
        {
            return;
        }
        int caret = context.Caret;
        TextDocument document = context.Editor.Document;
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
                return;
            }
            SignatureInformation signature = help.Signatures[Math.Clamp(help.ActiveSignature ?? 0, 0, help.Signatures.Count - 1)];
            card.Clear();
            card.AddCode(signature.Label);
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
        foreach (ParameterInformation parameter in signature.Parameters ?? [])
        {
            JsonElement label = parameter.Label;
            string text = label.ValueKind switch
            {
                JsonValueKind.String => label.GetString() ?? string.Empty,
                JsonValueKind.Array when label.GetArrayLength() == 2 && label[0].TryGetInt32(out int start) && label[1].TryGetInt32(out int end)
                    && 0 <= start && start <= end && end <= signature.Label.Length => signature.Label[start..end],
                _ => string.Empty,
            };
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
