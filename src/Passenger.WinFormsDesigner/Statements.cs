using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>What running designer code asks of where it runs: the designer's surface, which sites each component and keeps
/// each handler's name, or a running app, where a component is made as the code makes it and a handler is the form's method.</summary>
internal interface IStatementPlace
{
    /// <summary>A component the code makes of a type for a field, from what it writes to make it.</summary>
    object Made(Type type, string field, DesignerValue made);

    /// <summary>A handler, a method of the form's by its name, hooked to an object's event.</summary>
    void Hook(object on, EventDescriptor raised, string handler);

    /// <summary>The form's resource manager as its code makes one.</summary>
    object Resources();
}

/// <summary>Designer code's statements run against a form's objects as its compiled InitializeComponent runs them: components
/// and the code's locals made, properties set along their dotted paths, methods called, handlers hooked. Each name the code
/// writes is the form's, a component's or a local's, as <paramref name="named"/> finds it.</summary>
internal sealed class Statements(Control root, Dictionary<string, object> made, Values values, Func<string, object?> named, IStatementPlace place)
{
    /// <summary>One statement run.</summary>
    public void Run(DesignerStatement statement)
    {
        switch (statement.Act)
        {
            case DesignerAct.Other when statement.Target.Length > 0 && statement.Value is { Kind: DesignerValueKinds.New, Type: { } manager }
                && typeof(System.Resources.ResourceManager).IsAssignableFrom(values.Type(manager)):
                made[statement.Target] = place.Resources();
                return;
            case DesignerAct.Create when statement.Target == "components":
                // The class's container is made once, by the form itself: the surface's own, or the app's.
                return;
            case DesignerAct.Create when statement.Value is { Kind: DesignerValueKinds.New, Type: { } written } created:
                made[statement.Target] = place.Made(values.Type(written), statement.Target, created);
                return;
            case DesignerAct.Assign:
                (object at, string last) = Reached(statement);
                if (TypeDescriptor.GetProperties(at)[last] is { } property)
                {
                    property.SetValue(at, values.Of(statement.Value!, property.PropertyType));
                    return;
                }
                PropertyInfo clr = at.GetType().GetProperty(last, BindingFlags.Public | BindingFlags.Instance) ?? throw new DesignerException($"{at.GetType().Name} has no {last}");
                clr.SetValue(at, values.Of(statement.Value!, clr.PropertyType));
                return;
            case DesignerAct.Call:
                (object on, string method) = Reached(statement);
                values.Call(on, method, statement.Arguments ?? []);
                return;
            case DesignerAct.Hook:
                (object hooked, string name) = Walked(Owner(statement), statement.Member!);
                EventDescriptor raised = TypeDescriptor.GetEvents(hooked)[name] ?? throw new DesignerException($"{hooked.GetType().Name} has no event {name}");
                string handler = statement.Value switch
                {
                    { Kind: DesignerValueKinds.Name, Name: { } group } => group[(group.LastIndexOf('.') + 1)..],
                    { Kind: DesignerValueKinds.New, Arguments: [{ Kind: DesignerValueKinds.Name, Name: { } group }] } => group[(group.LastIndexOf('.') + 1)..],
                    _ => throw new DesignerException($"The designer hooks only a method of the class to {name}"),
                };
                place.Hook(hooked, raised, handler);
                return;
            default:
                throw new DesignerException("The designer does not read this statement");
        }
    }

    private object Owner(DesignerStatement statement) =>
        statement.Target.Length == 0 ? root : named(statement.Target) ?? throw new DesignerException($"{statement.Target} is used before it is made");

    /// <summary>What a statement acts on and the member it names there: a component's, or the root's, or, for a path that
    /// starts at a local the code made (its resource manager), that local's.</summary>
    private (object At, string Last) Reached(DesignerStatement statement) =>
        statement.Target.Length == 0 && statement.Member!.IndexOf('.') is var dot and > 0 && made.TryGetValue(statement.Member[..dot], out object? local)
            ? Walked(local, statement.Member[(dot + 1)..])
            : Walked(Owner(statement), statement.Member!);

    /// <summary>An object's properties read along a dotted path but its last name, and that name.</summary>
    private static (object At, string Last) Walked(object owner, string path)
    {
        string[] names = path.Split('.');
        object at = owner;
        foreach (string name in names[..^1])
        {
            at = (TypeDescriptor.GetProperties(at)[name] is { } property ? property.GetValue(at) : at.GetType().GetProperty(name)?.GetValue(at))
                ?? throw new DesignerException($"{name} of {at.GetType().Name} is null");
        }
        return (at, names[^1]);
    }
}
