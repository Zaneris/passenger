using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A change's shape where it is marked beside the code: a whole bar, a bar broken into dashes, two thin rails, or a
/// wedge between lines.</summary>
public enum ChangeShape
{
    Bar,
    Dashes,
    Rails,
    Wedge,
}

/// <summary>The shape each kind of change takes as well as its colour, the gutter's and the scrollbar's stripe's alike, so
/// lines added, changed, in conflict and deleted are told apart without their colours.</summary>
public static class ChangeShapes
{
    public static ChangeShape Of(ChangeMarkKind kind) => kind switch
    {
        ChangeMarkKind.Added => ChangeShape.Bar,
        ChangeMarkKind.Deleted => ChangeShape.Wedge,
        ChangeMarkKind.Conflict => ChangeShape.Rails,
        _ => ChangeShape.Dashes,
    };
}
