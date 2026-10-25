namespace Passenger.Text;

/// <summary>How lines differ from the last commit, as the gutter's change bar shows it; a conflict is a merge's change
/// both sides made otherwise.</summary>
public enum ChangeMarkKind
{
    Added,
    Modified,
    Deleted,
    Conflict,
}

/// <summary>A change bar: lines (from zero) added or modified since the last commit, or the place lines were deleted
/// (a count of zero, drawn as a mark between lines).</summary>
public readonly record struct ChangeMark(int Line, int Count, ChangeMarkKind Kind);

/// <summary>Words that changed inside changed lines, as a diff marks them over its lines' tint: added (green), taken away
/// (grey), changed (blue) or in conflict (red), each in a stronger colour than its lines.</summary>
public readonly record struct InlineChange(TextRange Range, ChangeMarkKind Kind);
