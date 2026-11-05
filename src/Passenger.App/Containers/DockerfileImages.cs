using Passenger.Text;

namespace Passenger.App.Containers;

/// <summary>A FROM's image: its line, its stretch of the text, the image as written and its repository (the image without
/// its tag or digest).</summary>
internal sealed record BaseImage(int Line, TextRange Range, string Image, string Repository);

/// <summary>What a Dockerfile's FROMs name, from its text alone.</summary>
internal static class DockerfileImages
{
    /// <summary>The images a Dockerfile's FROMs name, but scratch, an earlier stage's name, and one an ARG gives
    /// (<c>$VERSION</c>), which only a build knows.</summary>
    public static IReadOnlyList<BaseImage> Of(TextSnapshot text)
    {
        List<BaseImage> found = [];
        HashSet<string> stages = new(StringComparer.OrdinalIgnoreCase);
        for (int line = 0; line < text.LineCount; line++)
        {
            string said = text.GetLineText(line);
            string trimmed = said.TrimStart();
            if (trimmed.Length < 5 || !trimmed.StartsWith("FROM", StringComparison.OrdinalIgnoreCase) || trimmed[4] is not (' ' or '\t'))
            {
                continue;
            }
            int at = said.Length - trimmed.Length + 4;
            string[] words = said[at..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            int index = Array.FindIndex(words, w => !w.StartsWith("--", StringComparison.Ordinal));
            if (index < 0)
            {
                continue;
            }
            string image = words[index];
            bool named = !image.Equals("scratch", StringComparison.OrdinalIgnoreCase) && !image.Contains('$', StringComparison.Ordinal) && !stages.Contains(image);
            if (index + 2 < words.Length && words[index + 1].Equals("AS", StringComparison.OrdinalIgnoreCase))
            {
                stages.Add(words[index + 2]);
            }
            if (!named)
            {
                continue;
            }
            int start = text.GetLine(line).Start + said.IndexOf(image, at, StringComparison.Ordinal);
            string bare = image.Split('@')[0];
            int colon = bare.LastIndexOf(':');
            found.Add(new BaseImage(line, new TextRange(start, image.Length), image, colon > bare.LastIndexOf('/') ? bare[..colon] : bare));
        }
        return found;
    }

    /// <summary>The FROM naming an image within a stretch of the text as it stands: a finding's own, where several name that
    /// image, however the text moved it; null once that stretch names another.</summary>
    public static BaseImage? Marked(TextSnapshot text, TextRange marked, string image) =>
        Of(text).FirstOrDefault(b => b.Image == image && b.Range.Start >= marked.Start && b.Range.End <= marked.End);
}
