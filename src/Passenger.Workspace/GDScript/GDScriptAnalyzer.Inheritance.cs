namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of a class's base written by name (resolve_class_inheritance), its classes resolved
/// in Godot's order (the script's, then each inner class's as it comes, a class named as a base before the one naming it):
/// an engine singleton or a singleton autoload of no script extended, a scope's member that is no class, a base or nested
/// type not found, a nested type taken of no script class, and a base reaching back to its class (from Godot 4.8 a class's
/// own member too).</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>Each class's base errors; whether a class's base failed with one of them said, which stops Godot's reading.</summary>
    private bool ExtendsErrors(GDScriptTree tree, string path, GDScriptType self, GDScriptAnalysis analysis)
    {
        Dictionary<GDScriptClass, (GDScriptType Type, GDScriptClass? Outer)> classes = new() { [tree.Class] = (self, null) };
        List<GDScriptClass> order = [tree.Class];
        foreach (GDScriptType inner in InnerClasses(tree.Members.Select(m => (m.Node, m.Offset)), path))
        {
            List<GDScriptType> holders = Enclosing(self, tree.Members.Select(m => (m.Node, m.Offset)), inner.Class!);
            classes[inner.Class!] = (inner, holders.Count > 0 ? holders[^1].Class ?? tree.Class : tree.Class);
            order.Add(inner.Class!);
        }
        Dictionary<GDScriptClass, bool?> state = [];
        Dictionary<GDScriptClass, GDScriptType?> based = [];
        bool newer = Engine?.Version >= new Version(4, 8);
        bool said = false;
        foreach (GDScriptClass node in order)
        {
            // A script whose own base fails has no inner class read.
            if (!Resolve(node) && node == tree.Class)
            {
                break;
            }
        }
        return said;

        // Whether a class's base resolves, said where it does not; one resolving gives no answer of its own.
        bool Resolve(GDScriptClass node)
        {
            if (state.TryGetValue(node, out bool? done))
            {
                return done == true;
            }
            state[node] = null;
            bool resolved = Base(node, new Context(analysis, classes[node].Type, classes[node].Type.Offset, null, null));
            state[node] = resolved;
            return resolved;
        }

        bool Base(GDScriptClass node, Context at)
        {
            if (node.ExtendsPath is not null || node.Extends is not [{ } first, ..] chain)
            {
                based[node] = null;
                return true;
            }
            GDScriptType? found = First(node, first, at);
            if (found is null)
            {
                return false;
            }
            if (found.IsVariant)
            {
                based[node] = null;
                return true;
            }
            for (int i = 1; i < chain.Count; i++)
            {
                if (found.Kind != GDScriptTypeKind.Class)
                {
                    return Fails(at, chain[i], $"Cannot get nested types for extension from non-GDScript type \"{found.Instance}\".");
                }
                (GDScriptType? nested, bool present) = NestedMember(found, chain[i].Name);
                if (nested is null)
                {
                    return Fails(at, chain[i], present ? $"Identifier \"{chain[i].Name}\" is not a preloaded script or class." : $"Could not find nested type \"{chain[i].Name}\".");
                }
                if (newer && nested.Class == node)
                {
                    return Fails(at, chain[i], i == chain.Count - 1 ? "Cyclic inheritance." : "Classes can not inherit from their own members.");
                }
                found = nested;
            }
            // A class whose base is resolving has none yet, so the chain stops there.
            for (GDScriptClass? reached = found.Class; reached is not null && classes.ContainsKey(reached); reached = based.GetValueOrDefault(reached)?.Class)
            {
                if (reached == node)
                {
                    return Fails(at, node, "Cyclic inheritance.");
                }
                if (state.GetValueOrDefault(reached) is null)
                {
                    break;
                }
            }
            based[node] = found;
            return true;
        }

        // A base's first name as Godot finds it: a class_name, a singleton autoload, an engine class, then the scope's classes.
        GDScriptType? First(GDScriptClass node, GDScriptIdentifier first, Context at)
        {
            string name = first.Name;
            if (project.ClassPath(name) is { } global)
            {
                return Tree(global) is { } globalTree ? ScriptClass(globalTree, global) : GDScriptType.Variant;
            }
            if (project.Autoload(name) is { } autoload)
            {
                string script = project.Resolved(autoload);
                return !script.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ? Failed(at, first, $"Singleton {name} is not a GDScript.")
                    : Tree(script) is { } autoloadTree ? ScriptClass(autoloadTree, script) : GDScriptType.Variant;
            }
            if (Engine?.Classes.ContainsKey(name) == true)
            {
                return Engine.Singletons.ContainsKey(name) ? Failed(at, first, $"Cannot inherit native class \"{name}\" because it is an engine singleton.") : GDScriptType.Native(name);
            }
            List<GDScriptClass> scope = [];
            Add(node);
            if (newer)
            {
                scope.Remove(node);
            }
            foreach (GDScriptClass look in scope)
            {
                GDScriptType lookType = classes.TryGetValue(look, out (GDScriptType Type, GDScriptClass? Outer) known) ? known.Type : GDScriptType.Variant;
                if (look.Identifier?.Name == name)
                {
                    return !classes.ContainsKey(look) || state.ContainsKey(look) || Resolve(look) ? lookType : null;
                }
                if (!OwnNames(lookType).TryGetValue(name, out (GDScriptNode Node, int Offset) member))
                {
                    continue;
                }
                switch (member.Node)
                {
                    case GDScriptClass inner:
                        // Godot resolves a class member's own base as it reads it, whatever comes of that.
                        if (classes.ContainsKey(inner) && !state.ContainsKey(inner))
                        {
                            Resolve(inner);
                        }
                        if (newer && inner == node)
                        {
                            return Failed(at, first, node.Extends.Count == 1 ? "Cyclic inheritance." : "Classes can not inherit from their own members.");
                        }
                        return InnerClass(lookType, name);
                    case GDScriptConstant constant:
                        GDScriptType held = MemberType(constant, lookType, member.Offset);
                        if (held is { Kind: GDScriptTypeKind.Class, Meta: true })
                        {
                            return newer && held.Class == node ? Failed(at, first, node.Extends.Count == 1 ? "Cyclic inheritance." : "Classes can not inherit from their own members.")
                                : held with { Meta = false };
                        }
                        return held is { Kind: GDScriptTypeKind.Builtin or GDScriptTypeKind.Enum } or { Kind: GDScriptTypeKind.Native, Meta: false }
                            ? Failed(at, first, $"Constant \"{name}\" is not a preloaded script or class.") : Unsure();
                    default:
                        return Member(lookType, name) is { } other ? Failed(at, first, $"Cannot use {MemberWords(other)} \"{name}\" in extends chain.") : Unsure();
                }
            }
            return Failed(at, first, $"Could not find base class \"{name}\".");

            // A class and the classes its scope reaches through, as Godot's get_class_node_current_scope_classes lists them.
            void Add(GDScriptClass look)
            {
                if (scope.Contains(look))
                {
                    return;
                }
                scope.Add(look);
                if (look != node && based.GetValueOrDefault(look)?.Class is { } baseClass && classes.ContainsKey(baseClass))
                {
                    Add(baseClass);
                }
                if (classes.TryGetValue(look, out (GDScriptType Type, GDScriptClass? Outer) known) && known.Outer is { } outer)
                {
                    Add(outer);
                }
            }
        }

        GDScriptType? Failed(Context at, GDScriptNode place, string words)
        {
            Fails(at, place, words);
            return null;
        }

        bool Fails(Context at, GDScriptNode place, string words)
        {
            Error(at, place, words);
            said = true;
            return false;
        }

        // A base this reading is unsure of, which says nothing; Knows then stops the reading.
        static GDScriptType? Unsure() => null;
    }

    /// <summary>A script class's member a nested base names, as Godot's reduce_identifier_from_base finds it on the class
    /// and the script classes it extends, then their engine class: a class, or a constant holding one; null with whether a
    /// member of that name that is no class was found (an instance's alone is not).</summary>
    private (GDScriptType? Class, bool Present) NestedMember(GDScriptType holder, string name)
    {
        GDScriptType? at = holder.Instance;
        for (int depth = 0; at is { Kind: GDScriptTypeKind.Class } && depth < 64; depth++, at = BaseOf(at))
        {
            if (InnerClass(at, name) is { Kind: GDScriptTypeKind.Class } inner)
            {
                return (inner with { Meta = false }, true);
            }
            if (ScopedConstantType(at, name) is { Kind: GDScriptTypeKind.Class } constant)
            {
                return (constant with { Meta = false }, true);
            }
            if (OwnNames(at).ContainsKey(name))
            {
                return (null, !InstanceOnly(at, name));
            }
        }
        return (null, Member(holder with { Meta = true }, name) is not null);
    }
}
