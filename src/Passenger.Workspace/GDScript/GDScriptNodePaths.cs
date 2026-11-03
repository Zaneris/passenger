namespace Passenger.Workspace.GDScript;

/// <summary>The node path a GDScript names at an offset, as Godot's parser reads it: a <c>$Path</c> or <c>%Unique</c>, or a
/// string or NodePath literal given to get_node, get_node_or_null or has_node. Pure.</summary>
public static class GDScriptNodePaths
{
    /// <summary>The path written around an offset of the script's text, or null where none is.</summary>
    public static string? At(GDScriptTree tree, int offset)
    {
        foreach (GDScriptMember member in tree.Members)
        {
            if (member.Start <= offset && offset <= member.End && In(member.Node, offset - member.Offset) is { } path)
            {
                return path;
            }
        }
        return null;
    }

    private static string? In(GDScriptNode node, int at)
    {
        switch (node)
        {
            case GDScriptGetNode get when get.Start <= at && at <= get.End:
                return get.Path;
            case GDScriptCall { FunctionName: "get_node" or "get_node_or_null" or "has_node", Arguments: [GDScriptLiteral { Value: string path } given, ..] }
                when given.Start <= at && at <= given.End:
                return path;
        }
        foreach (GDScriptNode child in node.Children)
        {
            if (In(child, at) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
