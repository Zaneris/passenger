using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>The Toolbox's items: Windows Forms' own, in the groups its designers have always listed them under (the rest it
/// offers under More Controls), then the components of the project's assembly and of each assembly it references that
/// builds on Windows Forms, a group each; every one public (the project's own internal ones too), made by a constructor of no
/// arguments, offered by its <c>[ToolboxItem]</c> and seen at design time, with its <c>[ToolboxBitmap]</c>'s picture.</summary>
internal static class Toolbox
{
    private static readonly (string Group, string[] Names)[] Groups =
    [
        ("Common Controls", ["Button", "CheckBox", "CheckedListBox", "ComboBox", "DateTimePicker", "Label", "LinkLabel", "ListBox", "ListView", "MaskedTextBox",
            "MonthCalendar", "NotifyIcon", "NumericUpDown", "PictureBox", "ProgressBar", "RadioButton", "RichTextBox", "TextBox", "ToolTip", "TreeView", "WebBrowser"]),
        ("Containers", ["FlowLayoutPanel", "GroupBox", "Panel", "SplitContainer", "TabControl", "TableLayoutPanel"]),
        ("Menus & Toolbars", ["ContextMenuStrip", "MenuStrip", "StatusStrip", "ToolStrip", "ToolStripContainer"]),
        ("Data", ["BindingNavigator", "BindingSource", "DataGridView"]),
        ("Components", ["ErrorProvider", "HelpProvider", "ImageList", "Timer"]),
        ("Printing", ["PageSetupDialog", "PrintDialog", "PrintPreviewControl", "PrintPreviewDialog"]),
        ("Dialogs", ["ColorDialog", "FolderBrowserDialog", "FontDialog", "OpenFileDialog", "SaveFileDialog"]),
    ];

    /// <summary>Every item, Windows Forms' first; <paramref name="designed"/> (the class on the surface) is left out.</summary>
    public static List<ToolboxEntry> Listed(Assembly? project, Type designed)
    {
        Assembly forms = typeof(Form).Assembly;
        Dictionary<string, Type> framework = forms.GetExportedTypes().Where(Offered).ToDictionary(t => t.Name, StringComparer.Ordinal);
        List<ToolboxEntry> listed = [];
        foreach ((string group, string[] names) in Groups)
        {
            foreach (string name in names)
            {
                if (framework.Remove(name, out Type? type))
                {
                    listed.Add(Entry(type, group));
                }
            }
        }
        listed.AddRange(framework.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => Entry(t, "More Controls")));
        foreach (Assembly assembly in Builds(project))
        {
            IEnumerable<Type> types;
            try
            {
                types = assembly == project ? assembly.GetTypes() : assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException partly)
            {
                types = partly.Types.OfType<Type>();
            }
            string group = $"{assembly.GetName().Name} Components";
            listed.AddRange(types.Where(t => t != designed && !typeof(Form).IsAssignableFrom(t) && Offered(t)).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => Entry(t, group)));
        }
        return listed;
    }

    /// <summary>The project's assembly and those it references that build on Windows Forms and are none of .NET's own.</summary>
    private static IEnumerable<Assembly> Builds(Assembly? project)
    {
        if (project is null)
        {
            yield break;
        }
        yield return project;
        string framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string desktop = Path.GetDirectoryName(typeof(Form).Assembly.Location)!;
        foreach (AssemblyName referenced in project.GetReferencedAssemblies())
        {
            Assembly? taken = null;
            try
            {
                taken = Assembly.Load(referenced);
            }
            catch (Exception missing) when (missing is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Console.Error.WriteLine($"{referenced.Name} did not load for the Toolbox: {missing.Message}");
            }
            if (taken is { IsDynamic: false } && Path.GetDirectoryName(taken.Location) is { } folder && folder != framework && folder != desktop
                && taken.GetReferencedAssemblies().Any(r => r.Name == "System.Windows.Forms"))
            {
                yield return taken;
            }
        }
    }

    /// <summary>Whether a type is a Toolbox item: a component made by a constructor of no arguments, not abstract nor generic,
    /// offered by its <c>[ToolboxItem]</c> and seen at design time.</summary>
    private static bool Offered(Type type)
    {
        if (!typeof(IComponent).IsAssignableFrom(type) || type.IsAbstract || type.IsGenericTypeDefinition || type.IsNested && !type.IsNestedPublic
            || type.GetConstructor(Type.EmptyTypes) is null)
        {
            return false;
        }
        AttributeCollection attributes = TypeDescriptor.GetAttributes(type);
        return !ToolboxItemAttribute.None.Equals(attributes[typeof(ToolboxItemAttribute)])
            && attributes[typeof(DesignTimeVisibleAttribute)] is not DesignTimeVisibleAttribute { Visible: false }
            && attributes[typeof(BrowsableAttribute)] is not BrowsableAttribute { Browsable: false };
    }

    private static ToolboxEntry Entry(Type type, string group) => new(type.Name, type.AssemblyQualifiedName ?? type.FullName!, group)
    {
        Png = Picture(type),
        Description = TypeDescriptor.GetAttributes(type)[typeof(DescriptionAttribute)] is DescriptionAttribute { Description: { Length: > 0 } said } ? said : null,
    };

    /// <summary>A type's <c>[ToolboxBitmap]</c> as a PNG; none where it gives none.</summary>
    private static byte[]? Picture(Type type)
    {
        try
        {
            if (TypeDescriptor.GetAttributes(type)[typeof(ToolboxBitmapAttribute)] is not ToolboxBitmapAttribute given || given.GetImage(type, large: false) is not { } image)
            {
                return null;
            }
            using (image)
            {
                using MemoryStream png = new();
                image.Save(png, ImageFormat.Png);
                return png.ToArray();
            }
        }
        catch (Exception unread) when (unread is ArgumentException or ExternalException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{type.Name}'s Toolbox picture did not read: {unread.Message}");
            return null;
        }
    }

    /// <summary>The Toolbox item that makes a type, as the Toolbox makes it: its <c>[ToolboxItem]</c>'s own item where that type
    /// is found, else a plain one, which sizes a label to its text as the one it names but .NET does not carry would.</summary>
    public static ToolboxItem ItemOf(Type type)
    {
        if (TypeDescriptor.GetAttributes(type)[typeof(ToolboxItemAttribute)] is ToolboxItemAttribute { ToolboxItemTypeName: { Length: > 0 } named } attribute)
        {
            Type? itemType = null;
            try
            {
                itemType = attribute.ToolboxItemType;
            }
            catch (Exception missing) when (missing is TypeLoadException or FileNotFoundException or ArgumentException)
            {
                Console.Error.WriteLine($"{type.Name}'s Toolbox item {named} is not found: {missing.Message}");
            }
            if (itemType is not null && itemType != typeof(ToolboxItem) && Activator.CreateInstance(itemType, type) is ToolboxItem own)
            {
                return own;
            }
            if (named.Contains("AutoSizeToolboxItem", StringComparison.Ordinal))
            {
                return new AutoSized(type);
            }
        }
        return new ToolboxItem(type);
    }

    /// <summary>A control that sizes itself to its text from the Toolbox: a label's AutoSize set as it is made.</summary>
    private sealed class AutoSized(Type type) : ToolboxItem(type)
    {
        protected override IComponent[]? CreateComponentsCore(IDesignerHost host, IDictionary defaultValues)
        {
            IComponent[]? made = base.CreateComponentsCore(host, defaultValues);
            foreach (IComponent component in made ?? [])
            {
                TypeDescriptor.GetProperties(component)["AutoSize"]?.SetValue(component, true);
            }
            return made;
        }
    }
}
