namespace Passenger.Inspector;

/// <summary>The walks of an app's tree the same in either framework, each with a stack of its own, so no tree the app's
/// code makes is too deep for the thread that walks it, whatever a level's frames cost there: the inspector runs inside the
/// person's app, which a stack overflow would end. <typeparamref name="T"/> is what the framework's tree is made of, and
/// <c>held</c> gives what an element holds, in the framework's order. Nothing here touches Avalonia or WPF.</summary>
internal static class ElementTrees
{
    /// <summary>An element and all it holds as the wire's <see cref="Element"/>, down to <see cref="Element.DeepestNesting"/>
    /// levels, where one holding more says so (<see cref="Element.Deeper"/>): <paramref name="place"/> says where each is from
    /// where its holder is (the root from <paramref name="outside"/>), and <paramref name="make"/> makes each once what it
    /// holds is made.</summary>
    public static Element Made<T, TPlace>(T root, TPlace outside, Func<T, IReadOnlyList<T>> held, Func<T, TPlace, TPlace> place,
        Func<T, TPlace, IReadOnlyList<Element>, Element> make)
    {
        // The elements whose children are being made, outermost first.
        List<Making<T, TPlace>> open = [];
        Enter(root, outside);
        while (true)
        {
            Making<T, TPlace> at = open[^1];
            if (at.Next < at.Held.Count)
            {
                Enter(at.Held[at.Next++], at.Place);
                continue;
            }
            open.RemoveAt(open.Count - 1);
            Element made = make(at.Element, at.Place, at.Made);
            made = at.Deeper ? made with { Deeper = true } : made;
            if (open.Count == 0)
            {
                return made;
            }
            open[^1].Made.Add(made);
        }

        void Enter(T element, TPlace holder)
        {
            TPlace placed = place(element, holder);
            IReadOnlyList<T> children = held(element);
            bool walked = open.Count < Element.DeepestNesting - 1;
            open.Add(new Making<T, TPlace>(element, placed, walked ? children : [], !walked && children.Count > 0));
        }
    }

    /// <summary>What an element holds, each before what it holds in turn, in the framework's order.</summary>
    public static IEnumerable<T> Below<T>(T root, Func<T, IEnumerable<T>> held)
    {
        // What is left of each level walked into, outermost first.
        List<IEnumerator<T>> open = [held(root).GetEnumerator()];
        try
        {
            while (open.Count > 0)
            {
                if (!open[^1].MoveNext())
                {
                    open[^1].Dispose();
                    open.RemoveAt(open.Count - 1);
                    continue;
                }
                T element = open[^1].Current;
                yield return element;
                open.Add(held(element).GetEnumerator());
            }
        }
        finally
        {
            foreach (IEnumerator<T> level in open)
            {
                level.Dispose();
            }
        }
    }

    /// <summary>An element being made: where it is, what it holds and how many of those are made, and whether it holds
    /// more than is walked.</summary>
    private sealed class Making<T, TPlace>(T element, TPlace place, IReadOnlyList<T> held, bool deeper)
    {
        public T Element { get; } = element;

        public TPlace Place { get; } = place;

        public IReadOnlyList<T> Held { get; } = held;

        public bool Deeper { get; } = deeper;

        public int Next { get; set; }

        public List<Element> Made { get; } = [];
    }
}
