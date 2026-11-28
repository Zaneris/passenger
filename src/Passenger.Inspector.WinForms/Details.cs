using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>One control or item looked at closely: the class whose designer code makes it, its layout, and each property the
/// Properties grid lists with its value and where the value came from: set on it (its code or its designer's), inherited
/// from the control around it (an ambient property: its font, colours, cursor, right to left), or its default. Passenger
/// reads the lines that set each from the designer code.</summary>
internal static class Details
{
    /// <summary>The properties a control takes from the one around it while it sets none of its own.</summary>
    private static readonly HashSet<string> Ambient = new(StringComparer.Ordinal)
    {
        nameof(Control.BackColor), nameof(Control.ForeColor), nameof(Control.Font), nameof(Control.Cursor), nameof(Control.RightToLeft),
    };

    public static ElementDetails Of(Inspector inspector, Component component)
    {
        Type type = component.GetType();
        DesignedIn? designed = Shown.DesignedOf(component);
        return new ElementDetails(
            inspector.IdOf(component),
            Words.TypeName(type),
            type.FullName ?? type.Name,
            Shown.NameOf(component),
            [],
            States(component),
            null,
            designed is null ? "Made in code: no designer code names it" : null,
            null,
            Layout(component),
            Properties(component),
            [])
        {
            Designed = designed,
        };
    }

    /// <summary>What a component's state says as Avalonia's pseudo-classes would: :focus, :focus-within, :disabled, :checked,
    /// :selected, :pointerover.</summary>
    private static List<string> States(Component component)
    {
        List<string> states = [];
        if (component is Control control)
        {
            Add(control.Focused, ":focus");
            Add(control.ContainsFocus && !control.Focused, ":focus-within");
            Add(control.IsHandleCreated && control.RectangleToScreen(control.ClientRectangle).Contains(Cursor.Position), ":pointerover");
        }
        Add(!Shown.IsEnabled(component), ":disabled");
        Add(component is CheckBox { Checked: true } or RadioButton { Checked: true } or ToolStripButton { Checked: true } or ToolStripMenuItem { Checked: true }, ":checked");
        Add(component is ToolStripItem { Selected: true } || (component is TabPage page && page.Parent is TabControl tabs && tabs.SelectedTab == page), ":selected");
        return states;

        void Add(bool on, string state)
        {
            if (on)
            {
                states.Add(state);
            }
        }
    }

    private static LayoutInfo Layout(Component component)
    {
        Box? inWindow = Shown.TopOf(component) is { } top && Shown.BoxIn(component, top) is { } at ? Shown.Boxed(at) : null;
        return component switch
        {
            Control control => new LayoutInfo(
                Shown.Boxed(control.Bounds),
                inWindow,
                control.PreferredSize.Width,
                control.PreferredSize.Height,
                Edges(control.Margin),
                null,
                Edges(control.Padding),
                Across(control.Dock, control.Anchor),
                Down(control.Dock, control.Anchor)),
            ToolStripItem item => new LayoutInfo(
                Shown.Boxed(item.Bounds),
                inWindow,
                item.GetPreferredSize(Size.Empty).Width,
                item.GetPreferredSize(Size.Empty).Height,
                Edges(item.Margin),
                null,
                Edges(item.Padding),
                "Left",
                "Top"),
            _ => new LayoutInfo(new Box(0, 0, 0, 0), inWindow, 0, 0, new Edges(0, 0, 0, 0), null, null, "Left", "Top"),
        };
    }

    private static Edges Edges(Padding padding) => new(padding.Left, padding.Top, padding.Right, padding.Bottom);

    /// <summary>How a control stands across its container, by its docking, else its anchors: as XAML says alignment.</summary>
    private static string Across(DockStyle dock, AnchorStyles anchor) => dock switch
    {
        DockStyle.Fill or DockStyle.Top or DockStyle.Bottom => "Stretch",
        DockStyle.Left => "Left",
        DockStyle.Right => "Right",
        _ when anchor.HasFlag(AnchorStyles.Left) && anchor.HasFlag(AnchorStyles.Right) => "Stretch",
        _ when anchor.HasFlag(AnchorStyles.Right) => "Right",
        _ => "Left",
    };

    /// <summary>How a control stands up and down its container, by its docking, else its anchors.</summary>
    private static string Down(DockStyle dock, AnchorStyles anchor) => dock switch
    {
        DockStyle.Fill or DockStyle.Left or DockStyle.Right => "Stretch",
        DockStyle.Top => "Top",
        DockStyle.Bottom => "Bottom",
        _ when anchor.HasFlag(AnchorStyles.Top) && anchor.HasFlag(AnchorStyles.Bottom) => "Stretch",
        _ when anchor.HasFlag(AnchorStyles.Bottom) => "Bottom",
        _ => "Top",
    };

    /// <summary>Each property the Properties grid lists, by name, with its value and where it came from.</summary>
    private static List<PropertyValue> Properties(Component component)
    {
        List<PropertyValue> values = [];
        foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component, [BrowsableAttribute.Yes]).Sort())
        {
            object? value = Shown.Read(property, component);
            (string source, string? detail) = SourceOf(component, property);
            values.Add(new PropertyValue(property.Name, Words.TypeName(property.ComponentType), false, Words.TypeName(property.PropertyType),
                Shown.Format(value, property.Converter), source, detail, null));
        }
        return values;
    }

    /// <summary>Where a property's value came from: set on the component, inherited from the nearest control around it that
    /// sets it (an ambient property it leaves unset), or its default.</summary>
    private static (string Source, string? Detail) SourceOf(Component component, PropertyDescriptor property)
    {
        if (Sets(property, component))
        {
            return (ValueSources.Local, null);
        }
        if (component is Control control && Ambient.Contains(property.Name))
        {
            for (Control? around = control.Parent; around is not null; around = around.Parent)
            {
                if (TypeDescriptor.GetProperties(around)[property.Name] is { } held && Sets(held, around))
                {
                    return (ValueSources.Inherited, Words.InheritedFrom(Shown.Called(around), ValueSources.Local));
                }
            }
        }
        return (ValueSources.Default, null);
    }

    /// <summary>Whether a component sets a property itself, as its designer would write it: what its ShouldSerialize or default
    /// value says; for a property whose content the designer writes (a collection, a button's flat look), whether it holds
    /// any item, or sets any part.</summary>
    private static bool Sets(PropertyDescriptor property, object owner)
    {
        try
        {
            if (property.SerializationVisibility != DesignerSerializationVisibility.Content)
            {
                return property.ShouldSerializeValue(owner);
            }
            return property.GetValue(owner) switch
            {
                null => false,
                System.Collections.ICollection items => items.Count > 0,
                { } content => TypeDescriptor.GetProperties(content).Cast<PropertyDescriptor>().Any(part => part.ShouldSerializeValue(content)),
            };
        }
        catch (Exception error)
        {
            // The app's own ShouldSerialize, whose failure leaves the value's source unknown.
            InspectorTrace.Step($"{property.Name}'s ShouldSerializeValue failed: {error}");
            return false;
        }
    }
}
