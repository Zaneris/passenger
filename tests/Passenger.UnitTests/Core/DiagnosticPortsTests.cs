using System.Reflection;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;
using Passenger.Profiler;

namespace Passenger.UnitTests.Core;

/// <summary>A diagnostics client reaches a program's port by the socket's name (<see cref="DiagnosticPorts"/>): the client's
/// own search by a process id lists the whole temporary folder at each connection, so a folder of many files makes every
/// profile, live reading and held start wait on it, and takes the newest socket of the id, one a killed program left.</summary>
public sealed class DiagnosticPortsTests
{
    /// <summary>Read from the built code of Core, the profiler and every Passenger assembly they reach, a lambda's and a
    /// state machine's included: no client is made by a process id but in DiagnosticPorts, which does where no socket is named.</summary>
    [Fact]
    public void A_diagnostics_client_is_made_by_a_process_id_only_where_DiagnosticPorts_finds_no_socket_by_its_name()
    {
        ConstructorInfo byId = typeof(DiagnosticsClient).GetConstructor([typeof(int)])!;
        List<string> made = [];
        int homes = 0;
        foreach (Assembly assembly in Reached(typeof(DiagnosticPorts).Assembly, typeof(SessionStarts).Assembly))
        {
            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodBase method in BuiltCalls.Declared(type))
                {
                    foreach ((MethodBase called, _) in BuiltCalls.Of(method))
                    {
                        if (!called.Equals(byId))
                        {
                            continue;
                        }
                        if (BuiltCalls.Outermost(type) == typeof(DiagnosticPorts))
                        {
                            homes++;
                        }
                        else
                        {
                            made.Add($"{type.FullName}.{method.Name}");
                        }
                    }
                }
            }
        }
        Assert.True(homes > 0, "DiagnosticPorts' own client by a process id was not found, so the built code's calls were not read");
        Assert.True(made.Count == 0, "These make a diagnostics client by a process id, whose every connection lists the whole temporary folder (0.8 s at "
            + "600,000 files) and may reach a killed program's socket of the same id: take one from DiagnosticPorts.ClientAsync:\n" + string.Join('\n', made));
    }

    /// <summary>These assemblies and every Passenger assembly they reach through their references.</summary>
    private static IEnumerable<Assembly> Reached(params Assembly[] from)
    {
        Dictionary<string, Assembly> reached = new(StringComparer.Ordinal);
        Queue<Assembly> next = new(from);
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
