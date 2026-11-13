namespace Passenger.App;

/// <summary>Which of a VS Code colour theme's token rules colours a TextMate scope stack, as VS Code's TextMate themes match:
/// a selector's last scope names the scope it colours and every scope under it (<c>string</c> is
/// <c>string.quoted.double.json</c> too), its scopes before that name parents, each found further out in the stack in order;
/// the innermost scope a rule colours wins, then the selector naming more of that scope, then the one naming more parents,
/// then the later rule. A colour and a font style are each taken from the best rule that sets them. Pure.</summary>
public static class VsCodeScopes
{
    /// <summary>The colour and font style the rules give a stack of scopes, outermost first; null for each the rules leave
    /// to the editor's own.</summary>
    public static (uint? Foreground, string? FontStyle) Resolve(IReadOnlyList<VsCodeRule> rules, IReadOnlyList<string> stack)
    {
        uint? foreground = null;
        string? fontStyle = null;
        (int Depth, int Segments, int Parents, int Order) bestColour = (-1, 0, 0, 0);
        (int Depth, int Segments, int Parents, int Order) bestStyle = (-1, 0, 0, 0);
        for (int order = 0; order < rules.Count; order++)
        {
            VsCodeRule rule = rules[order];
            foreach (string selector in rule.Selectors)
            {
                if (Score(selector, stack) is not { } score)
                {
                    continue;
                }
                (int, int, int, int) ranked = (score.Depth, score.Segments, score.Parents, order);
                if (rule.Foreground is { } colour && ranked.CompareTo(bestColour) > 0)
                {
                    (foreground, bestColour) = (colour, ranked);
                }
                if (rule.FontStyle is { } style && ranked.CompareTo(bestStyle) > 0)
                {
                    (fontStyle, bestStyle) = (style, ranked);
                }
            }
        }
        return (foreground, fontStyle);
    }

    /// <summary>How well a selector names a stack: the depth of the scope it colours, how many of that scope's parts it
    /// names, and how many parents it names; null where it names none of the stack.</summary>
    public static (int Depth, int Segments, int Parents)? Score(string selector, IReadOnlyList<string> stack)
    {
        // An exclusion ("a - b") is read as what it names before its first minus.
        int minus = selector.IndexOf(" -", StringComparison.Ordinal);
        string named = minus >= 0 ? selector[..minus] : selector;
        string[] parts = [.. named.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ">")];
        if (parts.Length == 0)
        {
            return null;
        }
        for (int depth = stack.Count - 1; depth >= 0; depth--)
        {
            if (!Names(parts[^1], stack[depth]))
            {
                continue;
            }
            // Its parents, innermost first, each further out than the one before.
            int at = depth - 1;
            bool found = true;
            for (int p = parts.Length - 2; p >= 0 && found; p--)
            {
                while (at >= 0 && !Names(parts[p], stack[at]))
                {
                    at--;
                }
                found = at >= 0;
                at--;
            }
            if (found)
            {
                return (depth, parts[^1].Split('.').Length, parts.Length - 1);
            }
        }
        return null;
    }

    /// <summary>Whether a selector's scope names a scope: the same, or the scope's first parts.</summary>
    private static bool Names(string selector, string scope) =>
        scope.StartsWith(selector, StringComparison.Ordinal) && (scope.Length == selector.Length || scope[selector.Length] == '.');
}
