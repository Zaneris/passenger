using Passenger.App.Telemetry;
using Passenger.Core;

namespace Passenger.UnitTests.Telemetry;

/// <summary>What tells a .NET program to send a dashboard its logs and traces, a project run's and a container run's
/// service's alike: the endpoint of the protocol it uses, the key beside its own headers, its records' name and batches
/// every second; what it sets itself (launchSettings.json, a configuration's environment, a compose file) kept, named as
/// kept, and a program that names its own endpoint left to it.</summary>
public sealed class OtlpEnvironmentTests
{
    private const string Grpc = "http://127.0.0.1:41001";
    private const string Http = "http://127.0.0.1:41002";

    private static OtlpEnvironment? Told(params (string Name, string Value)[] own) =>
        OtlpEnvironment.Of(Grpc, Http, "key", "Web", own.ToDictionary(set => set.Name, set => set.Value));

    [Fact]
    public void A_program_that_sets_nothing_is_told_the_grpc_endpoint_the_key_its_name_and_one_second_batches()
    {
        OtlpEnvironment told = Told()!;
        Assert.Equal(new Dictionary<string, string>
        {
            [OtelVariables.ExporterEndpoint] = Grpc,
            [OtelVariables.ExporterHeaders] = "x-otlp-api-key=key",
            [OtelVariables.ServiceName] = "Web",
            [OtelVariables.LogsDelay] = "1000",
            [OtelVariables.SpansDelay] = "1000",
            [OtelVariables.MetricsInterval] = "1000",
        }, told.Told);
        Assert.Empty(told.Kept);
    }

    [Fact]
    public void A_program_that_sends_over_http_is_told_the_http_endpoint()
    {
        Assert.Equal(Http, Told((OtelVariables.ExporterProtocol, "http/protobuf"))!.Told[OtelVariables.ExporterEndpoint]);
        Assert.Equal(Grpc, Told((OtelVariables.ExporterProtocol, "grpc"))!.Told[OtelVariables.ExporterEndpoint]);
    }

    [Fact]
    public void A_program_that_names_its_own_endpoint_is_left_to_it_whatever_the_letter_case_of_its_name()
    {
        Assert.Null(Told((OtelVariables.ExporterEndpoint, "http://collector:4317")));
        Assert.Null(Told(("otel_exporter_otlp_endpoint", "http://collector:4317")));
    }

    [Fact]
    public void A_programs_own_name_and_batching_are_kept_and_said_and_its_name_is_what_its_records_go_by()
    {
        (string, string)[] own = [(OtelVariables.ServiceName, "shop-web"), ("Otel_Bsp_Schedule_Delay", "5000")];
        OtlpEnvironment told = Told(own)!;
        Assert.Equal([OtelVariables.ServiceName, OtelVariables.SpansDelay], told.Kept);
        Assert.False(told.Told.ContainsKey(OtelVariables.ServiceName), "The program's own OTEL_SERVICE_NAME was told over");
        Assert.False(told.Told.ContainsKey(OtelVariables.SpansDelay), "The program's own span batching was told over");
        Assert.Equal("1000", told.Told[OtelVariables.LogsDelay]);
        Assert.Equal("shop-web", OtlpEnvironment.NameOf("Web", own.ToDictionary(set => set.Item1, set => set.Item2)));
        Assert.Equal("Web", OtlpEnvironment.NameOf("Web", new Dictionary<string, string>()));
    }

    [Fact]
    public void A_programs_own_headers_are_sent_with_the_key_after_them()
    {
        Assert.Equal("tenant=shop,x-otlp-api-key=key", Told((OtelVariables.ExporterHeaders, "tenant=shop"))!.Told[OtelVariables.ExporterHeaders]);
        Assert.Equal("x-otlp-api-key=key", Told((OtelVariables.ExporterHeaders, string.Empty))!.Told[OtelVariables.ExporterHeaders]);
    }
}
