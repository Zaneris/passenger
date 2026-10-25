using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A place the caret was: its file, its offset in the text and that offset's line.</summary>
public sealed record HistoryPlace(string Path, int Offset, int Line);

/// <summary>Back and Forward, Last and Next Edit Location, and the places Recent Locations lists. A navigation (go to
/// definition, a usage, a bookmark) puts the place it left behind and forgets what was ahead; Back puts the caret's
/// place ahead. The caret moved by a key or a click changes neither, so Forward still goes where Back came from. An
/// edit keeps its place among the places edited, which Last Edit Location steps back through and Next Edit Location
/// forward. Each list keeps at most <see cref="Limit"/> places, and the places in an open file move with its
/// text, each by an anchor in it.</summary>
public sealed class NavigationHistory
{
    /// <summary>How many places each way.</summary>
    public const int Limit = 150;

    /// <summary>Two places in a file fewer lines apart than this are one.</summary>
    private const int NearLines = 4;

    private readonly List<Spot> behind = [];
    private readonly List<Spot> ahead = [];

    /// <summary>The text of each file open, whose places it carries.</summary>
    private readonly Dictionary<string, TextDocument> open = new(StringComparer.Ordinal);

    /// <summary>The places left, by a navigation or for another file, and those edited, the latest last: Recent Locations'.</summary>
    private readonly List<Kept> visited = [];
    private readonly List<Kept> edited = [];

    /// <summary>Where Last and Next Edit Location stand among the places edited: past the last until they are used, and
    /// put back there by the next edit.</summary>
    private int editStep;
    private long count;

    /// <summary>A navigation left a place: it goes behind, taking the place of the last there when near it.</summary>
    public void Left(HistoryPlace from)
    {
        PutBehind(from);
        Clear(ahead);
        Visited(from);
    }

    /// <summary>A place left otherwise (another file shown): Recent Locations lists it, Back does not go there.</summary>
    public void Visited(HistoryPlace place) => Merge(visited, place);

    /// <summary>A place edited: Last Edit Location goes back to it from anywhere else, starting again from the latest.</summary>
    public void Edited(HistoryPlace place)
    {
        Merge(edited, place);
        editStep = edited.Count;
    }

    /// <summary>Whether Last (a step of -1) or Next Edit Location (1) has a place edited to go to.</summary>
    public bool CanStepEdits(int step) => step < 0 ? editStep > 0 && edited.Count > 0 : editStep < edited.Count - 1;

    /// <summary>The place edited before (a step of -1) or after (1) the one Last Edit Location went to last, passing over
    /// the caret's place and those whose file is gone; null with none.</summary>
    public HistoryPlace? StepEdits(int step, HistoryPlace? here, Func<string, bool> exists)
    {
        for (int i = Math.Min(editStep, edited.Count) + step; i >= 0 && i < edited.Count; i += step)
        {
            HistoryPlace place = edited[i].Spot.Place;
            if ((here is null || !Near(here, place)) && exists(place.Path))
            {
                editStep = i;
                return place;
            }
        }
        return null;
    }

    /// <summary>Recent Locations' places, the latest first: those left and edited, or only those edited, one for each
    /// stretch of a file, none near the caret's place.</summary>
    public IReadOnlyList<HistoryPlace> Recent(bool editedOnly, HistoryPlace? here)
    {
        List<HistoryPlace> listed = [];
        foreach (Kept recent in (editedOnly ? edited : visited.Concat(edited)).OrderByDescending(p => p.When))
        {
            HistoryPlace place = recent.Spot.Place;
            if ((here is null || !Near(here, place)) && listed.All(l => !Near(l, place)))
            {
                listed.Add(place);
            }
        }
        return listed;
    }

    /// <summary>The files edited, the latest first.</summary>
    public IReadOnlyList<string> EditedFiles => [.. edited.OrderByDescending(p => p.When).Select(p => p.Spot.Place.Path).Distinct()];

    /// <summary>Forgets a place Recent Locations listed (its Delete), with those near it.</summary>
    public void Forget(HistoryPlace place)
    {
        Forget(visited, place);
        int before = edited.Count;
        Forget(edited, place);
        if (edited.Count != before)
        {
            editStep = edited.Count;
        }
    }

    /// <summary>Where Back goes from the caret's place, which goes ahead; null with nothing behind. A place whose file
    /// is gone is passed over.</summary>
    public HistoryPlace? Back(HistoryPlace? here, Func<string, bool> exists)
    {
        if (Take(behind, exists) is not { } target)
        {
            return null;
        }
        if (here is not null)
        {
            Keep(ahead, Made(here));
        }
        return target;
    }

    /// <summary>Where Forward goes from the caret's place, which goes behind; null with nothing ahead. A place near the
    /// caret's is passed over while there is another.</summary>
    public HistoryPlace? Forward(HistoryPlace? here, Func<string, bool> exists)
    {
        HistoryPlace? target = Take(ahead, exists);
        while (target is not null && here is not null && Near(here, target) && ahead.Count > 0)
        {
            target = Take(ahead, exists) ?? target;
        }
        if (target is null)
        {
            return null;
        }
        if (here is not null && (here.Path != target.Path || here.Line != target.Line))
        {
            PutBehind(here);
        }
        return target;
    }

    /// <summary>A file opened: its places move with its text until it closes. Text typed at a place goes before it, so a
    /// place stays on the text it was at.</summary>
    public void Attach(string path, TextDocument text)
    {
        open[path] = text;
        foreach (Spot spot in Spots().Where(s => s.Place.Path == path))
        {
            spot.Attach(text);
        }
    }

    /// <summary>A file closed: its places stay where they were carried.</summary>
    public void Detach(string path)
    {
        open.Remove(path);
        foreach (Spot spot in Spots().Where(s => s.Place.Path == path))
        {
            spot.Detach();
        }
    }

    /// <summary>The session ended with files still open, which the next session follows: every file is let go as if
    /// closed, its places kept where they were carried.</summary>
    public void DetachAll()
    {
        foreach (string path in open.Keys.ToList())
        {
            Detach(path);
        }
    }

    /// <summary>Carries the places in a file, or in every file of a folder, to where it moved.</summary>
    public void Move(string from, string to)
    {
        foreach (Spot spot in Spots())
        {
            spot.Move(from, to);
        }
        foreach (string path in open.Keys.Where(p => PathNames.Within(p, from)).ToList())
        {
            open.Remove(path, out TextDocument? text);
            open[PathNames.Moved(path, from, to)] = text!;
        }
    }

    /// <summary>Forgets every place, as another folder opens: each folder's are its own.</summary>
    public void Clear()
    {
        Clear(behind);
        Clear(ahead);
        foreach (Kept kept in visited.Concat(edited))
        {
            kept.Spot.Detach();
        }
        visited.Clear();
        edited.Clear();
        editStep = 0;
    }

    private IEnumerable<Spot> Spots() => behind.Concat(ahead).Concat(visited.Select(k => k.Spot)).Concat(edited.Select(k => k.Spot));

    private Spot Made(HistoryPlace place)
    {
        Spot made = new(place);
        if (open.TryGetValue(place.Path, out TextDocument? text))
        {
            made.Attach(text);
        }
        return made;
    }

    private static void Clear(List<Spot> places)
    {
        foreach (Spot spot in places)
        {
            spot.Detach();
        }
        places.Clear();
    }

    private static void Forget(List<Kept> places, HistoryPlace near)
    {
        for (int i = places.Count - 1; i >= 0; i--)
        {
            if (Near(places[i].Spot.Place, near))
            {
                places[i].Spot.Detach();
                places.RemoveAt(i);
            }
        }
    }

    /// <summary>Keeps a place as the latest of its list, in place of the last when near it.</summary>
    private void Merge(List<Kept> places, HistoryPlace place)
    {
        if (places.Count > 0 && Near(places[^1].Spot.Place, place))
        {
            places[^1].Spot.Detach();
            places.RemoveAt(places.Count - 1);
        }
        places.Add(new Kept(Made(place), ++count));
        if (places.Count > Limit)
        {
            places[0].Spot.Detach();
            places.RemoveAt(0);
        }
    }

    private void PutBehind(HistoryPlace place)
    {
        if (behind.Count > 0 && Near(behind[^1].Place, place))
        {
            behind[^1].Detach();
            behind.RemoveAt(behind.Count - 1);
        }
        Keep(behind, Made(place));
    }

    private static bool Near(HistoryPlace one, HistoryPlace other) => one.Path == other.Path && Math.Abs(one.Line - other.Line) < NearLines;

    private static void Keep(List<Spot> places, Spot place)
    {
        places.Add(place);
        if (places.Count > Limit)
        {
            places[0].Detach();
            places.RemoveAt(0);
        }
    }

    private static HistoryPlace? Take(List<Spot> places, Func<string, bool> exists)
    {
        while (places.Count > 0)
        {
            Spot last = places[^1];
            places.RemoveAt(places.Count - 1);
            HistoryPlace place = last.Place;
            last.Detach();
            if (exists(place.Path))
            {
                return place;
            }
        }
        return null;
    }

    /// <summary>A place Recent Locations may list, and how recently it was kept, counted over both of its lists.</summary>
    private sealed record Kept(Spot Spot, long When);

    /// <summary>A place kept, read from its anchor while its file is open.</summary>
    private sealed class Spot(HistoryPlace place)
    {
        private HistoryPlace place = place;
        private Anchor? anchor;
        private TextDocument? text;

        public HistoryPlace Place
        {
            get
            {
                if (anchor is not null && text is not null)
                {
                    int offset = anchor.Offset;
                    int line = text.Snapshot.GetLineFromOffset(offset);
                    if (offset != place.Offset || line != place.Line)
                    {
                        place = place with { Offset = offset, Line = line };
                    }
                }
                return place;
            }
        }

        public void Attach(TextDocument document)
        {
            Detach();
            text = document;
            anchor = document.AnchorAt(place.Offset, AnchorGravity.After);
        }

        /// <summary>Its file closed, or it is no longer kept: it stays where it was last carried.</summary>
        public void Detach()
        {
            place = Place;
            anchor?.Remove();
            anchor = null;
            text = null;
        }

        public void Move(string from, string to) => place = Place with { Path = PathNames.Moved(place.Path, from, to) };
    }
}
