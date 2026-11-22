using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A launch's variables that load code into a program as it starts, taken only where each file one names lies in a
/// folder of the person's own, so an instrumentation the person installed (OpenTelemetry's sets a profiler, its startup hook,
/// additional deps and shared store) is taken, and a file in a folder another user may write is not.</summary>
public sealed class LoadingVariablesTests : IDisposable
{
    private readonly string own = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
    private readonly string shared = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose()
    {
        Directory.Delete(own, recursive: true);
        Directory.Delete(shared, recursive: true);
    }

    [Fact]
    public void A_launchs_code_loading_variables_are_taken_only_from_the_persons_own_folders() => Cases.AllApart<LoadingVariablesTests>(
        ("an instrumentation installed in a folder of the persons own is taken", test => test.An_instrumentation_installed_in_a_folder_of_the_persons_own_is_taken()),
        ("a file outside the persons own folders is refused naming the variable and the file", test => test.A_file_outside_the_persons_own_folders_is_refused_naming_the_variable_and_the_file()),
        ("a link from a folder of the persons own to another is followed and refused", test => test.A_link_from_a_folder_of_the_persons_own_to_another_is_followed_and_refused()),
        ("a path that is not whole and a diagnostic port are held to the same", test => test.A_path_that_is_not_whole_and_a_diagnostic_port_are_held_to_the_same()),
        ("the systems own loader variables are held where its loader reads them", test => test.The_systems_own_loader_variables_are_held_where_its_loader_reads_them()));

    private void An_instrumentation_installed_in_a_folder_of_the_persons_own_is_taken()
    {
        string home = Path.Join(own, ".otel-dotnet-auto");
        Directory.CreateDirectory(Path.Join(home, "net"));
        char separator = Path.PathSeparator;
        (string, string)[] instrumented =
        [
            ("CORECLR_ENABLE_PROFILING", "1"),
            ("CORECLR_PROFILER", "{918728DD-259F-4A6A-AC2B-B85E1B658318}"),
            ("CORECLR_PROFILER_PATH", Path.Join(home, "OpenTelemetry.AutoInstrumentation.Native.so")),
            (RuntimeVariables.StartupHooks, Path.Join(home, "net", "OpenTelemetry.AutoInstrumentation.StartupHook.dll") + separator + "App.Hooks"),
            ("DOTNET_ADDITIONAL_DEPS", Path.Join(home, "AdditionalDeps")),
            ("DOTNET_SHARED_STORE", Path.Join(home, "store")),
            ("OTEL_DOTNET_AUTO_HOME", home),
            ("PATH", shared),
        ];
        Assert.Null(LoadingVariables.WhyNot(instrumented, [own]));
    }

    private void A_file_outside_the_persons_own_folders_is_refused_naming_the_variable_and_the_file()
    {
        string hook = Path.Join(shared, "hook.dll");
        string? why = LoadingVariables.WhyNot([(OtelVariables.ServiceName, "api"), (RuntimeVariables.StartupHooks, hook)], [own]);
        Assert.Equal($"{RuntimeVariables.StartupHooks} loads {hook}, outside the folders only the person and the system write, where another user could have put it", why);
        Assert.NotNull(LoadingVariables.WhyNot([("CORECLR_PROFILER_PATH_64", Path.Join(shared, "profiler.so"))], [own]));
        Assert.NotNull(LoadingVariables.WhyNot([("dotnet_profiler_path", Path.Join(shared, "profiler.so"))], [own]));
        Assert.NotNull(LoadingVariables.WhyNot([("CORECLR_NOTIFICATION_PROFILERS", $"{Path.Join(shared, "p.so")}={{0F1E2D3C-0000-0000-0000-000000000000}}")], [own]));
        Assert.NotNull(LoadingVariables.WhyNot([("DOTNET_ADDITIONAL_DEPS", Path.Join(own, "deps") + Path.PathSeparator + Path.Join(shared, "deps"))], [own]));
    }

    private void A_link_from_a_folder_of_the_persons_own_to_another_is_followed_and_refused()
    {
        string hook = Path.Join(shared, "hook.dll");
        File.WriteAllBytes(hook, [1]);
        string link = Path.Join(own, "hook.dll");
        try
        {
            File.CreateSymbolicLink(link, hook);
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows lets this user make no symlink: {refused.Message}");
        }
        Assert.Contains($"loads {hook},", LoadingVariables.WhyNot([(RuntimeVariables.StartupHooks, link)], [own]), StringComparison.Ordinal);
    }

    private void A_path_that_is_not_whole_and_a_diagnostic_port_are_held_to_the_same()
    {
        Assert.Contains("no full path", LoadingVariables.WhyNot([("CORECLR_PROFILER_PATH", "profiler.so")], [own]), StringComparison.Ordinal);
        Assert.NotNull(LoadingVariables.WhyNot([(RuntimeVariables.DiagnosticPorts, Path.Join(shared, "monitor.sock") + ",connect,nosuspend")], [own]));
        Assert.True(LoadingVariables.WhyNot([(RuntimeVariables.DiagnosticPorts, Path.Join(own, "monitor.sock") + ",connect,nosuspend")], [own]) is null != OperatingSystem.IsWindows(),
            "A diagnostic port in a folder of the person's own was refused off Windows, or a named pipe taken on Windows");
    }

    private void The_systems_own_loader_variables_are_held_where_its_loader_reads_them()
    {
        string library = Path.Join(shared, "lib.so");
        string? preloaded = LoadingVariables.WhyNot([("LD_PRELOAD", $"{Path.Join(own, "a.so")} {library}")], [own]);
        string? inserted = LoadingVariables.WhyNot([("DYLD_INSERT_LIBRARIES", $"{Path.Join(own, "a.dylib")}:{library}")], [own]);
        Assert.True(OperatingSystem.IsLinux() ? preloaded is not null : preloaded is null, $"LD_PRELOAD of a shared library was answered {preloaded ?? "taken"}");
        Assert.True(OperatingSystem.IsMacOS() ? inserted is not null : inserted is null, $"DYLD_INSERT_LIBRARIES of a shared library was answered {inserted ?? "taken"}");
    }
}
