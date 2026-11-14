using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Maui;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Controls.Xaml.Diagnostics;
using AvaloniaWindow = Avalonia.Controls.Window;
using ContentPage = Microsoft.Maui.Controls.ContentPage;
using MauiApplication = Microsoft.Maui.Controls.Application;
using ResourceDictionary = Microsoft.Maui.Controls.ResourceDictionary;

namespace Passenger.MauiPreview;

/// <summary>Draws the file each request brings: App.xaml's text loaded onto the project's App (again only when its text,
/// platform or idiom changes), the file's text loaded onto its x:Class made without its constructor (its
/// InitializeComponent and whatever it asks of the app's services never run) by MAUI's runtime loader with design
/// properties on, its handler's Avalonia control laid out in a headless window of the screen's size and rendered at the
/// request's scale to a PNG, with each of the file's elements' boxes by its line and column in the XAML.</summary>
internal sealed partial class Previewer(Assembly project, Type? appType, IMauiContext context, TextWriter protocol)
{
    /// <summary>The largest side a frame is drawn at, in pixels.</summary>
    private const int Largest = 8192;

    /// <summary>How many rounds of the dispatcher's jobs and the render timer a frame is given to settle (images load in
    /// their own job).</summary>
    private const int Settling = 5;

    private const string Xaml2009 = "http://schemas.microsoft.com/winfx/2009/xaml";
    private const string Xaml2006 = "http://schemas.microsoft.com/winfx/2006/xaml";

    private (string Xaml, string Platform, string Idiom)? applicationLoaded;
    private string? applicationProblem;

    /// <summary>A failure of the project's code while the page was laid out or drawn (a converter, a handler).</summary>
    private Exception? failed;

    /// <summary>The newest request's page drawn and answered.</summary>
    public void Draw(MauiPreviewRequest request)
    {
        MauiPreviewReply reply;
        try
        {
            reply = Drawn(request);
        }
        catch (Exception error)
        {
            // The project's code failing as its page is made or drawn is the preview's error, never the previewer's end.
            Console.Error.WriteLine(error);
            reply = Error(request.Sequence, error);
        }
        protocol.WriteLine(JsonSerializer.Serialize(reply, MauiPreviewJson.Options));
    }

    private MauiPreviewReply Drawn(MauiPreviewRequest request)
    {
        PreviewDevice.Use(request.Platform, request.Idiom);
        bool dark = request.Theme == MauiPreviewThemes.Dark;
        Avalonia.Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        MauiApplication application = Application(request);
        application.UserAppTheme = dark ? AppTheme.Dark : AppTheme.Light;
        if (Root(request.Xaml) is not { } root)
        {
            return Nothing(request.Sequence, "A file with no root has nothing to draw");
        }
        if (root is MauiApplication or ResourceDictionary)
        {
            return Nothing(request.Sequence, root is MauiApplication
                ? "App.xaml's resources are drawn with every page of the app"
                : "A resource dictionary is drawn with the pages that use it");
        }
        if (root is not IView view)
        {
            return Nothing(request.Sequence, $"A {root.GetType().Name} has nothing to draw");
        }
        double scale = Math.Clamp(request.Scale, 0.25, 4);
        double width = Math.Clamp(request.Width, 1, Largest / scale);
        double height = Math.Clamp(request.Height, 1, Largest / scale);
        failed = null;
        IElementHandler handler = view.ToHandler(context);
        Control control = (handler as IViewHandler)?.ContainerView as Control ?? (Control)handler.PlatformView!;
        AvaloniaWindow window = new()
        {
            Content = control,
            Width = width,
            Height = height,
            WindowDecorations = WindowDecorations.None,
            CanResize = false,
        };
        try
        {
            window.SetRenderScaling(scale);
            window.Show();
            for (int round = 0; round < Settling; round++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            if (failed is { } failure)
            {
                return Error(request.Sequence, failure);
            }
            List<MauiPreviewBox> boxes = [];
            if (root is IVisualTreeElement tree)
            {
                Walk(tree, window, boxes);
            }
            PixelSize pixels = new(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            using RenderTargetBitmap bitmap = new(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            using MemoryStream png = new();
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
            return new MauiPreviewReply
            {
                Kind = MauiPreviewKinds.Frame,
                Sequence = request.Sequence,
                Png = png.ToArray(),
                Scale = scale,
                Elements = boxes,
                Notes = applicationProblem is { } problem ? [problem] : null,
            };
        }
        finally
        {
            window.Close();
            handler.DisconnectHandler();
        }
    }

    /// <summary>A failure of the project's code in the dispatcher's jobs as the page is laid out, kept as the frame's error.</summary>
    public void Failed(Exception error) => failed ??= error;

    /// <summary>The project's App with App.xaml's text as it stands loaded onto it, its resources what the page is drawn
    /// with; loaded again only when the text, platform or idiom changes (its OnPlatform and OnIdiom are read as it loads).
    /// A text that does not load leaves the last that did, and says so with the frame.</summary>
    private MauiApplication Application(MauiPreviewRequest request)
    {
        MauiApplication application = MauiApplication.Current ?? (MauiApplication)Uninitialized(appType ?? typeof(MauiApplication));
        if (request.Application is not { } source || applicationLoaded == (source.Xaml, request.Platform, request.Idiom))
        {
            return application;
        }
        applicationLoaded = (source.Xaml, request.Platform, request.Idiom);
        applicationProblem = null;
        try
        {
            Load(application, source.Xaml);
        }
        catch (Exception error) when (error is XamlParseException or TargetInvocationException or XmlException or InvalidOperationException)
        {
            Exception cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            applicationProblem = $"Drawn without App.xaml's resources, which did not load: {Message(cause)}";
        }
        return application;
    }

    /// <summary>The file's root: its x:Class made without its constructor, or MAUI's own type of the root's name where it
    /// names no class of the project's, the text loaded onto it; null for a file with no root.</summary>
    private object? Root(string xaml)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit };
        string? rootName = null;
        string? xClass = null;
        using (XmlReader reader = XmlReader.Create(new StringReader(xaml), settings))
        {
            if (reader.MoveToContent() == XmlNodeType.Element)
            {
                rootName = reader.LocalName;
                xClass = reader.GetAttribute("Class", Xaml2009) ?? reader.GetAttribute("Class", Xaml2006);
            }
        }
        if (rootName is null)
        {
            return null;
        }
        Type? type = (xClass is null ? null : project.GetType(xClass)) ?? typeof(ContentPage).Assembly.GetType("Microsoft.Maui.Controls." + rootName);
        if (type is null)
        {
            throw new XamlParseException($"Type {rootName} not found: the file's x:Class is not in the project's last build");
        }
        object root = typeof(MauiApplication).IsAssignableFrom(type) ? MauiApplication.Current ?? Uninitialized(type) : Uninitialized(type);
        if (root is MauiApplication)
        {
            return root;
        }
        Load(root, xaml);
        return root;
    }

    /// <summary>A class made as its nearest base of MAUI's own makes it: its own constructor never run.</summary>
    private static object Uninitialized(Type type)
    {
        object made = RuntimeHelpers.GetUninitializedObject(type);
        Type? maui = type;
        while (maui is not null && maui.Assembly != typeof(ContentPage).Assembly)
        {
            maui = maui.BaseType;
        }
        ConstructorInfo constructor = maui?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
            ?? throw new InvalidOperationException($"{type.Name} derives from no class of MAUI's that can be made without arguments");
        constructor.Invoke(made, null);
        return made;
    }

    /// <summary>The text loaded onto an object by MAUI's runtime loader with its design properties (d:Text,
    /// d:BindingContext) honoured, as a designer asks, the project's assembly where its namespaces are mapped.</summary>
    private void Load(object root, string xaml)
    {
        MethodInfo? design = typeof(Extensions).Assembly.GetType("Microsoft.Maui.Controls.Xaml.XamlLoader")?
            .GetMethod("Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(object), typeof(string), typeof(Assembly), typeof(bool)]);
        if (design is not null)
        {
            design.Invoke(null, [root, xaml, project, true]);
            return;
        }
        typeof(Extensions).GetMethods().Single(m => m.Name == "LoadFromXaml" && m.GetParameters()[1].ParameterType == typeof(string))
            .MakeGenericMethod(root.GetType()).Invoke(null, [root, xaml]);
    }

    /// <summary>Each element MAUI made from the XAML, with its line and column there and its box in the window.</summary>
    private static void Walk(IVisualTreeElement element, AvaloniaWindow window, List<MauiPreviewBox> boxes)
    {
        if (VisualDiagnostics.GetSourceInfo(element) is { } source && element is IView { Handler: { } handler })
        {
            Control? control = (handler as IViewHandler)?.ContainerView as Control ?? handler.PlatformView as Control;
            if (control is not null && control.TranslatePoint(new Point(0, 0), window) is { } at)
            {
                boxes.Add(new MauiPreviewBox(source.LineNumber, source.LinePosition, at.X, at.Y, control.Bounds.Width, control.Bounds.Height));
            }
        }
        foreach (IVisualTreeElement child in element.GetVisualChildren())
        {
            Walk(child, window, boxes);
        }
    }

    /// <summary>An assembly's types that load here: a platform's build has types of its platform (an Android activity)
    /// that no plain .NET has, which no page needs.</summary>
    internal static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partly)
        {
            return partly.Types.OfType<Type>();
        }
    }

    private static MauiPreviewReply Nothing(int sequence, string why) => new() { Kind = MauiPreviewKinds.Nothing, Sequence = sequence, Message = why };

    /// <summary>An error's answer: its type and message, with its line and column where the XAML's reader gave them.</summary>
    private static MauiPreviewReply Error(int sequence, Exception error)
    {
        Exception cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
        (int Line, int Column)? at = cause switch
        {
            XamlParseException { XmlInfo: { } info } when info.HasLineInfo() => (info.LineNumber, info.LinePosition),
            XmlException xml when xml.LineNumber > 0 => (xml.LineNumber, xml.LinePosition),
            _ => null,
        };
        return new MauiPreviewReply
        {
            Kind = MauiPreviewKinds.Error,
            Sequence = sequence,
            Message = $"{cause.GetType().Name}: {Message(cause)}",
            Line = at?.Line,
            Column = at?.Column,
        };
    }

    /// <summary>An error's message without the place MAUI's loader writes before it, or an XML reader after it, which the
    /// answer gives apart.</summary>
    private static string Message(Exception error) => PlaceSuffix().Replace(PlacePrefix().Replace(error.Message, string.Empty), string.Empty);

    [GeneratedRegex(@"^Position \d+:\d+\. ")]
    private static partial Regex PlacePrefix();

    [GeneratedRegex(@"\s*Line \d+, position \d+\.$")]
    private static partial Regex PlaceSuffix();
}
