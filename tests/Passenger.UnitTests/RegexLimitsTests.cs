using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.UnitTests;

/// <summary>A unit test's answer is the same however busy the machine: no regex it runs stops for the wall clock. Every
/// regex time limit in Passenger's code is one of TextSearch's, and here, from the tests' first moment, none holds.</summary>
public sealed class RegexLimitsTests
{
    [ModuleInitializer]
    internal static void Lift() => TextSearch.LiftTimeLimits();

    /// <summary>The limits lifted, a regex's search runs to its end and one made from a pattern given holds to no time.</summary>
    [Fact]
    public void A_regex_here_runs_to_its_end_however_long_the_machine_keeps_it_waiting()
    {
        Assert.True(TextSearch.AtOnce == Regex.InfiniteMatchTimeout && TextSearch.Apart == Regex.InfiniteMatchTimeout,
            $"A regex here stops past {TextSearch.AtOnce} on the window's thread and {TextSearch.Apart} off it: on a busy machine a unit test of it fails for no fault of the code");
        SearchResult found = TextSearch.FindAll(TextSnapshot.From("a b a"), new SearchQuery("a", Regex: true), TextSearch.AtOnce);
        Assert.True(found is { Stopped: false, Matches.Count: 2 }, $"A regex's search with the limits lifted came to {found.Matches.Count} matches, stopped: {found.Stopped}");
        Assert.Equal(Regex.InfiniteMatchTimeout, TextSearch.Written("(?<!b)a", RegexOptions.None, TextSearch.Apart).MatchTimeout);
    }

    /// <summary>Every regex Passenger's code or these tests give a time limit takes TextSearch's, AtOnce or Apart, which the
    /// tests lift: read from the built code of this assembly and every Passenger assembly it loads, TextSearch's own aside,
    /// a call's last argument (its limit) being what the instruction before it reads.</summary>
    [Fact]
    public void Every_regex_time_limit_in_the_code_the_unit_tests_run_is_TextSearchs()
    {
        MemberInfo[] liftable = [typeof(TextSearch).GetProperty(nameof(TextSearch.AtOnce))!.GetMethod!, typeof(TextSearch).GetProperty(nameof(TextSearch.Apart))!.GetMethod!,
            typeof(Regex).GetField(nameof(Regex.InfiniteMatchTimeout))!];
        List<string> limited = [];
        List<string> own = [];
        int read = 0;
        foreach (Assembly assembly in Loaded())
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (BuiltCalls.Outermost(type) == typeof(TextSearch))
                {
                    continue;
                }
                const BindingFlags Own = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (MemberInfo member in type.GetMembers(Own))
                {
                    if (member.GetCustomAttribute<GeneratedRegexAttribute>() is { MatchTimeoutMilliseconds: not Timeout.Infinite } generated)
                    {
                        own.Add($"{type.FullName}.{member.Name}: [GeneratedRegex] with {generated.MatchTimeoutMilliseconds} ms");
                    }
                }
                foreach (MethodBase method in BuiltCalls.Declared(type))
                {
                    foreach ((MethodBase called, MemberInfo? before) in BuiltCalls.Of(method))
                    {
                        if (called.DeclaringType != typeof(Regex) || !called.IsPublic || called.GetParameters() is not [.., { ParameterType: var last }] || last != typeof(TimeSpan))
                        {
                            continue;
                        }
                        read++;
                        if (before is null || !liftable.Any(member => member.Equals(before)))
                        {
                            own.Add($"{type.FullName}.{method.Name}: Regex.{called.Name} with a limit read from {before?.Name ?? "a local or an argument"}");
                        }
                        else
                        {
                            limited.Add($"{type.FullName}.{method.Name}");
                        }
                    }
                }
            }
        }
        Assert.True(read > 0 && limited.Count > 0, "No regex was found given a time limit, so the built code's calls were not read");
        Assert.True(own.Count == 0, "A regex here holds to a time limit of its own, which a busy machine runs past, so a unit test of it fails for no fault "
            + "of the code: give it TextSearch.AtOnce on the window's thread or TextSearch.Apart off it, which the unit tests lift:\n" + string.Join('\n', own));
    }

    /// <summary>This assembly and every Passenger assembly it reaches through its references.</summary>
    private static IEnumerable<Assembly> Loaded()
    {
        Dictionary<string, Assembly> reached = new(StringComparer.Ordinal);
        Queue<Assembly> next = new([typeof(RegexLimitsTests).Assembly]);
        while (next.TryDequeue(out Assembly? assembly))
        {
            if (!reached.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }
            foreach (AssemblyName name in assembly.GetReferencedAssemblies().Where(name => name.Name!.StartsWith("Passenger.", StringComparison.Ordinal)))
            {
                next.Enqueue(Assembly.Load(name));
            }
        }
        return reached.Values;
    }
}
