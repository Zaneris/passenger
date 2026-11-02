namespace Library;

/// <summary>A library's types that carry no debugger attributes, which a client tells the debugger to present: a point
/// whose ToString counts each call, and a node with a kind of node deriving from it, as an engine's are.</summary>
public struct Point2(float x, float y)
{
	public static int Calls;

	public float X = x;

	public float Y = y;

	public override readonly string ToString()
	{
		Calls++;
		return $"<{X}; {Y}>";
	}
}

public class Node(string name)
{
	private readonly List<Node> children = [];

	public int Depth;

	public string Name => name;

	public void Add(Node child) => children.Add(child);

	public Node[] GetChildren() => [.. children];
}

public class Node2(string name) : Node(name)
{
	public float Rotation;
}

/// <summary>What marks a member a program's type tunes.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class TunedAttribute : Attribute;
