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

    [Fact]
    public void A_signal_with_an_endpoint_of_its_own_never_gets_the_key_the_rest_being_told_apart()
    {
        OtlpEnvironment told = Told((OtelVariables.TracesEndpoint, "https://collector.example/v1/traces"))!;
        Assert.True(told.Told.Keys.All(variable => variable is not (OtelVariables.ExporterHeaders or OtelVariables.ExporterEndpoint or OtelVariables.TracesHeaders or OtelVariables.TracesEndpoint)),
            $"The common headers reach a signal's own endpoint, so the key would go to the collector: {string.Join(", ", told.Told.Keys)}");
        Assert.Equal(Grpc, told.Told[OtelVariables.LogsEndpoint]);
        Assert.Equal("x-otlp-api-key=key", told.Told[OtelVariables.LogsHeaders]);
        Assert.Equal(Grpc, told.Told[OtelVariables.MetricsEndpoint]);
        Assert.Equal("x-otlp-api-key=key", told.Told[OtelVariables.MetricsHeaders]);
        Assert.Equal([OtelVariables.TracesEndpoint], told.Elsewhere);
        Assert.Equal(["logs", "metrics"], told.Sent);
    }

    [Fact]
    public void Signals_told_apart_over_HTTP_are_given_their_whole_address_as_a_signals_endpoint_is_read()
    {
        OtlpEnvironment told = Told((OtelVariables.LogsEndpoint, "https://collector.example/v1/logs"), (OtelVariables.ExporterProtocol, "http/protobuf"))!;
        Assert.Equal($"{Http}/v1/traces", told.Told[OtelVariables.TracesEndpoint]);
        Assert.Equal($"{Http}/v1/metrics", told.Told[OtelVariables.MetricsEndpoint]);
    }

    [Fact]
    public void The_common_headers_stay_the_programs_own_where_a_signal_goes_elsewhere_and_the_rest_carry_them_with_the_key()
    {
        OtlpEnvironment told = Told((OtelVariables.MetricsEndpoint, "https://collector.example/v1/metrics"), (OtelVariables.ExporterHeaders, "tenant=shop"))!;
        Assert.False(told.Told.ContainsKey(OtelVariables.ExporterHeaders), "The key was added to the headers the metrics' own endpoint is sent");
        Assert.Equal("tenant=shop,x-otlp-api-key=key", told.Told[OtelVariables.LogsHeaders]);
        Assert.Equal("tenant=shop,x-otlp-api-key=key", told.Told[OtelVariables.TracesHeaders]);
    }

    [Fact]
    public void A_program_sending_every_signal_elsewhere_is_left_to_it_each_endpoint_named()
    {
        (string, string)[] own = [(OtelVariables.LogsEndpoint, "http://a/"), (OtelVariables.TracesEndpoint, "http://b/"), (OtelVariables.MetricsEndpoint, "http://c/")];
        Assert.Null(Told(own));
        Assert.Equal([OtelVariables.LogsEndpoint, OtelVariables.TracesEndpoint, OtelVariables.MetricsEndpoint],
            OtlpEnvironment.NoneComes(own.ToDictionary(set => set.Item1, set => set.Item2)));
    }

    [Fact]
    public void A_blank_endpoint_is_none_as_OpenTelemetry_reads_it()
    {
        Assert.Equal(Grpc, Told((OtelVariables.ExporterEndpoint, " "))!.Told[OtelVariables.ExporterEndpoint]);
        Assert.Empty(Told((OtelVariables.TracesEndpoint, string.Empty))!.Elsewhere);
    }

    [Fact]
    public void A_signals_own_headers_replace_the_common_ones_so_coming_to_the_dashboard_they_carry_the_key_too()
    {
        OtlpEnvironment told = Told((OtelVariables.TracesHeaders, "tenant=shop"))!;
        Assert.Equal(Grpc, told.Told[OtelVariables.ExporterEndpoint]);
        Assert.Equal("tenant=shop,x-otlp-api-key=key", told.Told[OtelVariables.TracesHeaders]);
        Assert.Equal("x-otlp-api-key=key", told.Told[OtelVariables.ExporterHeaders]);
    }

    [Fact]
    public void A_signal_over_a_protocol_of_its_own_is_told_that_protocols_endpoint()
    {
        OtlpEnvironment told = Told((OtelVariables.TracesProtocol, "http/protobuf"))!;
        Assert.Equal($"{Http}/v1/traces", told.Told[OtelVariables.TracesEndpoint]);
        Assert.Equal(Grpc, told.Told[OtelVariables.LogsEndpoint]);
        Assert.False(told.Told.ContainsKey(OtelVariables.ExporterEndpoint), "One endpoint can't serve two protocols");
        Assert.Empty(told.Elsewhere);
    }

    [Fact]
    public void What_a_program_inherits_counts_as_what_it_sets_its_own_over_it()
    {
        KeyValuePair<string, string>[] inherited = [new(OtelVariables.ExporterEndpoint, "http://collector:4318"), new(OtelVariables.ExporterProtocol, "http/protobuf")];
        Assert.Equal([OtelVariables.ExporterEndpoint], OtlpEnvironment.NoneComes(OtlpEnvironment.Effective(inherited, new Dictionary<string, string>())));

        IReadOnlyDictionary<string, string> onlyProtocol = OtlpEnvironment.Effective(inherited[1..], new Dictionary<string, string>());
        Assert.Equal(Http, OtlpEnvironment.Of(Grpc, Http, "key", "Web", onlyProtocol)!.Told[OtelVariables.ExporterEndpoint]);

        IReadOnlyDictionary<string, string> over = OtlpEnvironment.Effective(inherited, new Dictionary<string, string> { ["otel_exporter_otlp_endpoint"] = string.Empty });
        Assert.Empty(OtlpEnvironment.NoneComes(over));
    }
}
