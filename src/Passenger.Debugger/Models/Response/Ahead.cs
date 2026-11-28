namespace Passenger.Debugger.Models.Response;

/// <summary>What a stop's method will do from where it stands, as far as its values tell for sure: the code it won't run
/// (each a place its symbols give, lines and columns from one), the values its locals will take on the lines it will run,
/// and the line from which it can no longer tell (a condition it could only answer by running code), none where it read on
/// to the method's end.</summary>
public sealed record AheadRead(string Path, IReadOnlyList<AheadSpan> WontRun, IReadOnlyList<AheadValue> Values, int? UnsureFrom);

/// <summary>A piece of code a stop's method won't run.</summary>
public sealed record AheadSpan(int Line, int Column, int EndLine, int EndColumn);

/// <summary>The value a local will hold after a line ahead runs.</summary>
public sealed record AheadValue(int Line, string Name, string Value);
