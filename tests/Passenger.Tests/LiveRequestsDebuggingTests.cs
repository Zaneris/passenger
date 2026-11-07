using System.Net.Http;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Endpoints;

namespace Passenger.Tests;

/// <summary>The Requests window follows a web app Debug launches as it follows Run's: LiveRequestsTests' shop, debugged, lists
/// the request it serves with its command. Alone, since it debugs through the window.</summary>
[Collection(Alone.Name)]
public sealed class LiveRequestsDebuggingTests
{
    [AvaloniaFact]
    public void A_debugged_apps_requests_are_followed_as_a_run_ones_are()
    {
        using HeldPort port = new();
        string folder = TestFolders.Create("requests-debug-");
        LiveRequestsTests.WriteShop(folder, port.Port);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Requests);
            Assert.True(window.SelectConfiguration("Shop: http"), $"No configuration Shop: http among {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Ui.Launched(window, window.DebugAsync(), "the shop to start under the debugger", TimeSpan.FromMinutes(2));
            port.LetGo(HeldPort.GateIn(folder));
            Ui.WaitFor(() => window.RequestLog.Following.StartsWith("Following", StringComparison.Ordinal) && window.RequestLog.Following.Contains("process", StringComparison.Ordinal),
                "the Requests window to follow the shop debugged", TimeSpan.FromMinutes(1));
            using (HttpClient client = new())
            {
                Assert.True(Waiting.Until(() =>
                {
                    try
                    {
                        return LiveRequestsTests.Get(client, $"http://127.0.0.1:{port.Port}/orders/2") == "Grace";
                    }
                    catch (HttpRequestException)
                    {
                        return false;
                    }
                }, TimeSpan.FromMinutes(1)), "The debugged shop never answered");
            }
            Ui.WaitFor(() => window.RequestLog.Requests.Any(r => r.Path == "/orders/2" && r.Status == 200 && r.Commands.Count == 1),
                "the debugged shop's request, listed with its command", TimeSpan.FromSeconds(30));
            LiveRequest request = window.RequestLog.Requests.Single(r => r.Path == "/orders/2");
            Assert.Equal("GET /orders/{id:int}", request.Route is { } route ? RouteRequests.Title(route) : null);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
            window.Close();
        }
    }
}
