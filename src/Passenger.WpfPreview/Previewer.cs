using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xaml;
using System.Xml;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.WpfPreview;

/// <summary>Draws the file each request brings, on the application's thread: App.xaml's resources made the application's
/// (again only when its text changes), the file loaded as loose XAML (<see cref="LooseXaml"/>), its design-time data
/// context set, its root put on a surface (<see cref="Surface"/>) in a window that is never shown, so it is laid out and
/// loaded as in the app, and the surface rendered to a PNG with each of the file's elements' boxes. Requests that come
/// while one is drawn wait, and only the newest is drawn.</summary>
internal sealed partial class Previewer(Assembly project, TextWriter protocol, PreviewRequests<WpfPreviewRequest> requests, Dispatcher dispatcher)
{
    private const string DesignNamespace = "clr-namespace:Passenger.WpfPreview.Design;assembly=Passenger.WpfPreview";

    private readonly object gate = new();
    private readonly XamlSchemaContext schema = System.Windows.Markup.XamlReader.GetWpfSchemaContext();
    private readonly string localAssembly = project.GetName().Name ?? string.Empty;
    private bool posted;
    private bool drawing;
    private int answering;
    private string? applicationText;
    private string? applicationProblem;
    private Surface? shown;

    /// <summary>A request read: drawn once the application's thread is free, unless a newer one comes first.</summary>
    public void Arrived()
    {
        lock (gate)
        {
            if (posted)
            {
                return;
            }
            posted = true;
        }
        dispatcher.InvokeAsync(Next);
    }

    /// <summary>The project's code failed outside a drawing (a control's handler, later): said as the newest request's
    /// error.</summary>
    public void Failed(Exception error) => PreviewWire.Answer(protocol, Error(answering, error));

    private void Next()
    {
        lock (gate)
        {
            posted = false;
            if (drawing)
            {
                // Run from the drawing's own wait for the window to settle: drawn once that drawing is done.
                return;
            }
        }
        if (requests.Take() is not { } request)
        {
            return;
        }
        drawing = true;
        answering = request.Sequence;
        PreviewReply reply;
        try
        {
            reply = Draw(request);
        }
        finally
        {
            drawing = false;
        }
        PreviewWire.Answer(protocol, reply);
        lock (gate)
        {
            if (!requests.Waiting || posted)
            {
                return;
            }
            posted = true;
        }
        dispatcher.InvokeAsync(Next);
    }

    private PreviewReply Draw(WpfPreviewRequest request)
    {
        LetGo();
        try
        {
            if (request.Application is { } application && application.Xaml != applicationText)
            {
                applicationText = application.Xaml;
                applicationProblem = LoadApplication(application);
            }
            else if (request.Application is null && applicationText is not null)
            {
                applicationText = null;
                applicationProblem = null;
                Application.Current.Resources = new ResourceDictionary();
            }
            LooseXaml loose = LooseXaml.Read(request.Xaml, schema, localAssembly);
            if (loose.RootNamespace == LooseXaml.Presentation && loose.RootName == "Application")
            {
                return Nothing(request, "App.xaml defines the application, which has nothing to draw: its resources are what the other files are drawn with");
            }
            if (loose.RootNamespace == LooseXaml.Presentation && loose.RootName == "ResourceDictionary")
            {
                return Nothing(request, "A resource dictionary has nothing to draw");
            }
            Uri baseUri = new(request.BaseUri);
            object root = Load(loose.Text, baseUri);
            List<PreviewNote> notes = [];
            if (applicationProblem is not null)
            {
                notes.Add(new PreviewNote(applicationProblem));
            }
            ApplyDesignContexts(root, loose, baseUri, notes);
            if (Surface.For(root, loose) is not { } surface)
            {
                return Nothing(request, $"A {root.GetType().Name} has nothing to draw");
            }
            shown = surface;
            surface.Place();
            Settle();
            double scale = Math.Clamp(request.Scale, 0.25, 4);
            surface.Lay(scale);
            Size size = surface.Size;
            if (size.Width < 1 || size.Height < 1)
            {
                return Nothing(request, "Nothing to draw: the root is 0 × 0 (sized to content with none, and no d:DesignWidth and d:DesignHeight)");
            }
            if (PreviewWire.TooLarge(size.Width, size.Height, scale) is { } large)
            {
                return Error(request.Sequence, large);
            }
            return new PreviewReply
            {
                Kind = PreviewKinds.Frame,
                Sequence = request.Sequence,
                Png = surface.Render(scale),
                Scale = scale,
                Elements = Boxes(surface, baseUri),
                Notes = notes.Count == 0 ? null : notes,
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Error(request.Sequence, error);
        }
    }

    /// <summary>App.xaml's resources as the application's, read as loose XAML from its <c>Application.Resources</c>; what
    /// kept them from loading, or null.</summary>
    private string? LoadApplication(PreviewSource application)
    {
        try
        {
            LooseXaml loose = LooseXaml.Read(application.Xaml, schema, localAssembly);
            XElement root = XDocument.Parse(loose.Text).Root!;
            string property = root.Name.LocalName + ".Resources";
            if (root.Elements().FirstOrDefault(e => e.Name.LocalName == property) is not { } resources)
            {
                Application.Current.Resources = new ResourceDictionary();
                return null;
            }
            List<XElement> items = [.. resources.Elements()];
            XElement dictionary = items is [{ Name.LocalName: "ResourceDictionary" } only]
                ? new XElement(only)
                : new XElement(XName.Get("ResourceDictionary", LooseXaml.Presentation), items);
            foreach (XAttribute declared in root.Attributes().Where(a => a.IsNamespaceDeclaration || a.Name.LocalName == "Ignorable"))
            {
                if (dictionary.Attribute(declared.Name) is null)
                {
                    dictionary.Add(new XAttribute(declared));
                }
            }
            Application.Current.Resources = (ResourceDictionary)Load(dictionary.ToString(SaveOptions.DisableFormatting), application.BaseUri is { } uri ? new Uri(uri) : null);
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Application.Current.Resources = new ResourceDictionary();
            // The line WPF names is in the dictionary's text made above (its whitespace gone), never App.xaml's: none is said.
            return $"App.xaml's resources did not load: {Describe(error).Message}";
        }
    }

    /// <summary>Loose XAML loaded as WPF loads it, the project's assembly being the one a namespace with no assembly names,
    /// and relative URIs resolving against the file's place in that assembly.</summary>
    private object Load(string text, Uri? baseUri)
    {
        XamlXmlReaderSettings settings = new()
        {
            BaseUri = baseUri,
            LocalAssembly = project,
            ProvideLineInfo = true,
            IgnoreUidsOnPropertyElements = true,
        };
        using XmlReader xml = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        using XamlXmlReader reader = new(xml, schema, settings);
        return System.Windows.Markup.XamlReader.Load(reader);
    }

    /// <summary>Each element's <c>d:DataContext</c> as its data context, the designer's prefix taken as the previewer's
    /// (<see cref="Design.DesignInstanceExtension"/>); one that does not load is noted and left out.</summary>
    private void ApplyDesignContexts(object root, LooseXaml loose, Uri baseUri, List<PreviewNote> notes)
    {
        if (loose.Contexts.Count == 0 || root is not DependencyObject top)
        {
            return;
        }
        List<(FrameworkElement Element, XamlSourceInfo Place)> placed = [];
        foreach (DependencyObject node in Logical(top))
        {
            if (node is FrameworkElement element && VisualDiagnostics.GetXamlSourceInfo(node) is { } place && Ours(place, baseUri))
            {
                placed.Add((element, place));
            }
        }
        foreach (DesignContext context in loose.Contexts)
        {
            FrameworkElement? target = placed.Where(p => p.Place.LineNumber == context.Line && Math.Abs(p.Place.LinePosition - context.Column) <= 1)
                .Select(p => p.Element).FirstOrDefault();
            if (target is null)
            {
                continue;
            }
            try
            {
                XElement snippet = new(XName.Get("FrameworkElement", LooseXaml.Presentation));
                foreach ((string prefix, string uri) in context.Namespaces)
                {
                    if (prefix is "xml" or "xmlns")
                    {
                        continue;
                    }
                    string mapped = uri == LooseXaml.Designer ? DesignNamespace : uri;
                    snippet.Add(prefix.Length == 0 ? new XAttribute("xmlns", mapped) : new XAttribute(XNamespace.Xmlns + prefix, mapped));
                }
                snippet.Add(new XAttribute("DataContext", context.Value));
                FrameworkElement made = (FrameworkElement)Load(snippet.ToString(SaveOptions.DisableFormatting), baseUri);
                if (made.DataContext is { } value)
                {
                    target.DataContext = value;
                }
                else if (BindingOperations.GetBindingBase(made, FrameworkElement.DataContextProperty) is { } binding)
                {
                    target.SetBinding(FrameworkElement.DataContextProperty, binding);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                notes.Add(new PreviewNote($"d:DataContext did not load: {Describe(error).Message}", context.Line));
            }
        }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject node)
    {
        yield return node;
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            foreach (DependencyObject below in Logical(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>Whether a place is in the file drawn: not another file's (a user control's own XAML, read from its BAML).</summary>
    private static bool Ours(XamlSourceInfo place, Uri baseUri) => place.SourceUri is null || place.SourceUri == baseUri;

    /// <summary>Lets the dispatcher run what the surface's layout, bindings and Loaded queued, before it is drawn.</summary>
    private void Settle() => dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);

    /// <summary>The boxes of the file's own elements as drawn: a window's (the whole frame, its title bar included), then
    /// every element of the visual tree the file made, parents before children, below the title bar. What a template or
    /// another file's XAML made inside them is theirs.</summary>
    private static List<PreviewBox> Boxes(Surface surface, Uri baseUri)
    {
        List<PreviewBox> boxes = [];
        if (surface.Loaded is Window && VisualDiagnostics.GetXamlSourceInfo(surface.Loaded) is { } top && Ours(top, baseUri))
        {
            boxes.Add(new PreviewBox(top.LineNumber, top.LinePosition, 0, 0, surface.Size.Width, surface.Size.Height));
        }
        Walk(surface.Drawn, surface.Drawn, surface.Top, baseUri, boxes);
        return boxes;
    }

    private static void Walk(Visual node, FrameworkElement root, double top, Uri baseUri, List<PreviewBox> boxes)
    {
        if (node != root && node is UIElement { IsVisible: true } element && VisualDiagnostics.GetXamlSourceInfo(node) is { } place
            && Ours(place, baseUri) && node is not FrameworkElement { TemplatedParent: not null })
        {
            Rect box = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            boxes.Add(new PreviewBox(place.LineNumber, place.LinePosition, box.X, top + box.Y, box.Width, box.Height));
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(node, i) is Visual child)
            {
                Walk(child, root, top, baseUri, boxes);
            }
        }
    }

    /// <summary>The last drawing let go.</summary>
    private void LetGo()
    {
        if (shown is not { } last)
        {
            return;
        }
        shown = null;
        last.Close();
    }

    private static PreviewReply Nothing(WpfPreviewRequest request, string why) =>
        new() { Kind = PreviewKinds.Nothing, Sequence = request.Sequence, Message = why };

    private static PreviewReply Error(int sequence, string message) =>
        new() { Kind = PreviewKinds.Error, Sequence = sequence, Message = message };

    private static PreviewReply Error(int sequence, Exception error)
    {
        (string message, int? line, int? column) = Describe(error);
        return new PreviewReply { Kind = PreviewKinds.Error, Sequence = sequence, Message = message, Line = line, Column = column };
    }

    /// <summary>What an exception says of the XAML, without the place its message repeats, with its line and column, and
    /// the reason inside a parser's "threw an exception".</summary>
    private static (string Message, int? Line, int? Column) Describe(Exception error)
    {
        int? line = null;
        int? column = null;
        switch (error)
        {
            case System.Windows.Markup.XamlParseException parse when parse.LineNumber > 0:
                line = parse.LineNumber;
                column = parse.LinePosition;
                break;
            case XamlException xaml when xaml.LineNumber > 0:
                line = xaml.LineNumber;
                column = xaml.LinePosition;
                break;
            case XmlException xml when xml.LineNumber > 0:
                line = xml.LineNumber;
                column = xml.LinePosition;
                break;
        }
        if (error is TargetInvocationException { InnerException: { } invoked })
        {
            error = invoked;
        }
        string message = Plain(error.Message);
        Exception? inner = error.InnerException;
        while (inner is TargetInvocationException { InnerException: { } deeper })
        {
            inner = deeper;
        }
        if (inner is not null && Plain(inner.Message) is { Length: > 0 } reason && !message.Contains(reason, StringComparison.Ordinal))
        {
            message = $"{message.TrimEnd('.')}: {reason}";
        }
        return (message, line, column);
    }

    /// <summary>A message without the quotes and place WPF's parser wraps it in, or the place an XML reader adds.</summary>
    private static string Plain(string message)
    {
        string first = message.Split('\n')[0].Trim();
        if (WpfPlace().Match(first) is { Success: true } wpf)
        {
            first = wpf.Groups["message"].Value;
        }
        return XmlPlace().Replace(first, string.Empty).Trim();
    }

    [GeneratedRegex(@"^'(?<message>.*)' Line number '\d+' and line position '\d+'\.?$")]
    private static partial Regex WpfPlace();

    [GeneratedRegex(@"\s*Line \d+, position \d+\.?$")]
    private static partial Regex XmlPlace();
}
