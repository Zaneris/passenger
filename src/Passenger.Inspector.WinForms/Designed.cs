using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Forms;
using Passenger.Core;
using Passenger.WinFormsDesigner;

namespace Passenger.Inspector.WinForms;

/// <summary>A form's designer code as Hot Reload gives it (the statements its edit changed, the components it no longer
/// makes), run in each form of its class the app has open, on the app's UI thread, as its InitializeComponent runs them, so
/// the form shows the edit without being made again; what could not run is said at its line, the rest applied.</summary>
internal static class Designed
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>What Hot Reload made in each open form, by its field's name, which later edits name it by: a form whose class
    /// had no such field when it was compiled keeps it here alone.</summary>
    private static readonly ConditionalWeakTable<Control, Dictionary<string, object>> Kept = [];

    public static XamlReloaded Apply(DesignedChange change)
    {
        List<DesignerStatement> statements = JsonSerializer.Deserialize(change.Statements, FormDesignWire.Statements)
            ?? throw new Refusal("The designer code's statements did not read");
        List<DesignerStatement> unhooked = change.Unhooked is { } off ? JsonSerializer.Deserialize(off, FormDesignWire.Statements)
            ?? throw new Refusal("The designer code's hooks taken out did not read") : [];
        List<Control> roots = [.. Application.OpenForms.Cast<Form>().SelectMany(Within).Where(c => c.GetType().FullName == change.Class)];
        HashSet<XamlProblem> problems = [];
        foreach (Control root in roots)
        {
            Dictionary<string, object> made = Kept.GetValue(root, _ => new Dictionary<string, object>(StringComparer.Ordinal));
            Func<string, object?> named = name => name == "this" ? root : made.GetValueOrDefault(name) ?? root.GetType().GetField(name, Fields)?.GetValue(root);
            int dot = change.Class.LastIndexOf('.');
            Values values = new(new Types(dot > 0 ? change.Class[..dot] : null, change.Usings, root.GetType().Assembly), named);
            Statements run = new(root, made, values, named, new Live(root, values));
            root.SuspendLayout();
            try
            {
                for (int i = 0; i < statements.Count; i++)
                {
                    try
                    {
                        run.Run(statements[i]);
                    }
                    catch (Exception failed) when (failed is DesignerException or TargetInvocationException or ArgumentException or InvalidOperationException
                        or NotSupportedException or MissingMethodException or FormatException or InvalidCastException or OverflowException or NullReferenceException)
                    {
                        string why = (failed is TargetInvocationException { InnerException: { } inner } ? inner : failed).Message;
                        problems.Add(new XamlProblem($"{root.GetType().Name} did not take this line: {why}", i < change.Lines.Count ? change.Lines[i] : null, null, Error: false));
                    }
                }
                Statements unhooking = new(root, made, values, named, new Unhooking(root));
                foreach (DesignerStatement hook in unhooked)
                {
                    try
                    {
                        unhooking.Run(hook);
                    }
                    catch (Exception failed) when (failed is DesignerException or ArgumentException or InvalidOperationException)
                    {
                        problems.Add(new XamlProblem($"{root.GetType().Name} kept a handler the code no longer hooks: {failed.Message}", null, null, Error: false));
                    }
                }
                foreach (string gone in change.Gone)
                {
                    Gone(root, made, gone);
                }
            }
            finally
            {
                root.ResumeLayout(true);
                root.Invalidate(true);
            }
        }
        return new XamlReloaded([.. roots.Select(r => r.GetType().Name).Distinct()], null, [.. problems]);
    }

    /// <summary>A form and every control it holds, where a user control's class may be.</summary>
    private static IEnumerable<Control> Within(Control control) => [control, .. control.Controls.Cast<Control>().SelectMany(Within)];

    /// <summary>A component the code no longer makes taken out of the form and let go of.</summary>
    private static void Gone(Control root, Dictionary<string, object> made, string field)
    {
        FieldInfo? declared = root.GetType().GetField(field, Fields);
        if ((made.GetValueOrDefault(field) ?? declared?.GetValue(root)) is not IComponent component)
        {
            return;
        }
        if (component is Control control)
        {
            control.Parent?.Controls.Remove(control);
        }
        component.Dispose();
        made.Remove(field);
        if (declared is { IsInitOnly: false })
        {
            declared.SetValue(root, null);
        }
    }

    /// <summary>A running form as a hook its designer code no longer makes is taken off it: the handler, the form's own
    /// method, removed from the event; nothing is made.</summary>
    private sealed class Unhooking(Control root) : IStatementPlace
    {
        public object Made(Type type, string field, DesignerValue made) => throw new DesignerException("A hook taken off makes nothing");

        public void Hook(object on, EventDescriptor raised, string handler)
        {
            if (Delegate.CreateDelegate(raised.EventType, root, handler, ignoreCase: false, throwOnBindFailure: false) is { } called)
            {
                raised.RemoveEventHandler(on, called);
            }
        }

        public object Resources() => throw new DesignerException("A hook taken off makes nothing");
    }

    /// <summary>A running form as designer code runs in it: a component made as the code makes it and kept in its field where
    /// the form's class declares one, a handler the form's own method (one Hot Reload has added included), the resource
    /// manager the form's own, over the resources it was built with.</summary>
    private sealed class Live(Control root, Values values) : IStatementPlace
    {
        public object Made(Type type, string field, DesignerValue made)
        {
            object component = values.Of(made) ?? throw new DesignerException($"{field} was made null");
            if (root.GetType().GetField(field, Fields) is { IsInitOnly: false } declared && declared.FieldType.IsInstanceOfType(component))
            {
                declared.SetValue(root, component);
            }
            return component;
        }

        public void Hook(object on, EventDescriptor raised, string handler)
        {
            Delegate called = Delegate.CreateDelegate(raised.EventType, root, handler, ignoreCase: false, throwOnBindFailure: false)
                ?? throw new DesignerException($"{root.GetType().Name} has no method {handler} for {raised.Name}");
            // Hooked once, however many edits name it.
            raised.RemoveEventHandler(on, called);
            raised.AddEventHandler(on, called);
        }

        public object Resources() => new ComponentResourceManager(root.GetType());
    }
}
