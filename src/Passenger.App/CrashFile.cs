using Passenger.Core;

namespace Passenger.App;

/// <summary>An exception that ends Passenger, from its start on (in <c>App.Initialize</c>, a font or a theme failing before
/// the log's handlers are in, or later), kept in the log folder's <c>crash.log</c> beside <c>passenger.log</c>: standard
/// error, where the runtime says it, is nowhere a start from the Dock, the Start menu or an application menu shows. The next
/// start with a window lists it once among the IDE's errors, said in the status bar, its whole text written to the log, and
/// removes the file.</summary>
public static class CrashFile
{
    public const string Name = "crash.log";

    public static string PathOf => Path.Combine(IdeLog.Folder, Name);

    /// <summary>Keeps every exception that would end the process from here on, on whichever thread it comes.</summary>
    public static void Watch() => AppDomain.CurrentDomain.UnhandledException += (_, e) => Keep(e.ExceptionObject);

    private static void Keep(object thrown)
    {
        string path = PathOf;
        try
        {
            Directory.CreateDirectory(IdeLog.Folder);
            string text = CrashText.Of(thrown, DateTime.Now, Environment.ProcessId, $"{Product.Version} ({Product.Commit ?? "no commit"})");
            AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(text));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"passenger: what ended Passenger could not be kept in {path}: {error.Message}");
        }
    }

    /// <summary>The crash kept since the last start, said once (<see cref="IdeErrors.Recall"/>) and its file removed, the
    /// log keeping its text.</summary>
    public static Task SayLastAsync() => BackgroundWork.Run(() =>
    {
        string path = PathOf;
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            DateTime written = File.GetLastWriteTime(path);
            string text = File.ReadAllText(path);
            File.Delete(path);
            if (CrashText.Read(text, written) is { } crash)
            {
                IdeErrors.Recall(new IdeError(crash.At, crash.Kind, crash.Message, crash.Details));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"The crash kept in {path} could not be read: {error.Message}");
        }
    });
}
