using System.Text.Json;
using Passenger.App.Endpoints;

namespace Passenger.UnitTests.Endpoints;

/// <summary>An app's OpenAPI document as the Endpoints window reads it: its operations and the one a request calls, its
/// schemas with their references followed, a value held to a schema with each break said at the member that breaks it, a
/// body made from a schema, and the .http requests it makes for what a file does not call yet; OpenAPI 3.1 as .NET 10's
/// MapOpenApi writes it (numbers a string may stand for, with a pattern), 3.0's nullable and Swagger 2.0's definitions.</summary>
public sealed class OpenApiTests
{
    private const string Shop = """
        {
          "openapi": "3.1.1",
          "info": { "title": "Shop | v1", "version": "1.0.0" },
          "paths": {
            "/items/{id}": {
              "get": {
                "operationId": "GetItem",
                "parameters": [ { "name": "id", "in": "path", "required": true,
                  "schema": { "type": [ "integer", "string" ], "format": "int32", "pattern": "^-?(?:0|[1-9]\\d*)$" } } ],
                "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } } } }
              }
            },
            "/items": {
              "get": {
                "operationId": "ListItems",
                "responses": { "200": { "description": "OK", "content": { "application/json": {
                  "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Item" } } } } } }
              },
              "post": {
                "operationId": "AddItem",
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } } },
                "responses": { "201": { "description": "Created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } } } }
              }
            }
          },
          "components": {
            "schemas": {
              "Item": {
                "required": [ "name", "price" ],
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "price": { "type": [ "number", "string" ], "format": "double", "pattern": "^-?(?:0|[1-9]\\d*)(?:\\.\\d+)?(?:[eE][+-]?\\d+)?$" },
                  "tags": { "type": [ "null", "array" ], "items": { "type": "string" } }
                }
              }
            }
          }
        }
        """;

    private static readonly OpenApiDocument Document = OpenApiDocument.Parse(Shop)!;

    private static IReadOnlyList<SchemaBreak> Checked(string operation, int status, string json)
    {
        OpenApiOperation found = Document.Operations.Single(o => o.OperationId == operation);
        using JsonDocument value = JsonDocument.Parse(json);
        return OpenApiSchemas.Check(Document, found.ResponseSchema(status)!.Value, value.RootElement);
    }

    /// <summary>A document's operations are read and its requests found, never reaching beyond the app's
    /// origin.</summary>
    [Fact]
    public void A_document_is_read_and_its_requests_reach_only_their_apps_origin() => Cases.All(
        (nameof(A_documents_operations_are_read_and_a_request_finds_the_one_its_method_and_path_call),
            A_documents_operations_are_read_and_a_request_finds_the_one_its_method_and_path_call),
        (nameof(A_template_with_text_around_a_parameter_takes_only_a_segment_with_that_text),
            A_template_with_text_around_a_parameter_takes_only_a_segment_with_that_text),
        (nameof(A_body_is_made_from_the_document_of_the_app_at_the_address_alone), A_body_is_made_from_the_document_of_the_app_at_the_address_alone),
        (nameof(A_path_lies_at_its_apps_origin_and_one_written_to_leave_it_lies_nowhere),
            A_path_lies_at_its_apps_origin_and_one_written_to_leave_it_lies_nowhere),
        (nameof(A_redirect_stays_with_an_app_at_its_origin_or_moving_to_https_on_its_host),
            A_redirect_stays_with_an_app_at_its_origin_or_moving_to_https_on_its_host),
        (nameof(A_document_named_by_a_reference_to_another_host_is_refused_as_a_url_of_another_machine_is),
            A_document_named_by_a_reference_to_another_host_is_refused_as_a_url_of_another_machine_is),
        (nameof(A_swagger_2_documents_body_parameter_response_schemas_and_definitions_are_read),
            A_swagger_2_documents_body_parameter_response_schemas_and_definitions_are_read));

    /// <summary>A response is held to its operation's schema, each break said where it is.</summary>
    [Fact]
    public void A_response_is_held_to_the_schema_its_operation_gives() => Cases.All(
        (nameof(A_response_is_held_to_the_schema_of_the_operation_its_literal_text_calls),
            A_response_is_held_to_the_schema_of_the_operation_its_literal_text_calls),
        (nameof(A_pattern_stopped_at_its_time_limit_neither_passes_nor_breaks_the_member_and_says_it_was_not_checked),
            A_pattern_stopped_at_its_time_limit_neither_passes_nor_breaks_the_member_and_says_it_was_not_checked),
        (nameof(Each_member_that_breaks_its_schema_is_said_where_it_is_and_what_was_expected),
            Each_member_that_breaks_its_schema_is_said_where_it_is_and_what_was_expected),
        (nameof(A_schema_referring_to_itself_through_a_choice_checks_a_body_40_levels_deep_to_its_last_member_judging_each_level_once),
            A_schema_referring_to_itself_through_a_choice_checks_a_body_40_levels_deep_to_its_last_member_judging_each_level_once));

    /// <summary>Requests and bodies are made from the document's schemas, leaving out what is read only.</summary>
    [Fact]
    public void Requests_and_bodies_are_made_from_the_documents_schemas() => Cases.All(
        (nameof(A_body_made_from_a_schema_has_each_member_of_its_first_type_its_references_followed),
            A_body_made_from_a_schema_has_each_member_of_its_first_type_its_references_followed),
        (nameof(A_body_made_from_a_schema_leaves_out_what_its_schema_marks_read_only_wherever_it_marks_it),
            A_body_made_from_a_schema_leaves_out_what_its_schema_marks_read_only_wherever_it_marks_it),
        (nameof(Requests_are_made_for_the_operations_a_file_does_not_call_yet_with_variables_and_bodies_from_the_schemas),
            Requests_are_made_for_the_operations_a_file_does_not_call_yet_with_variables_and_bodies_from_the_schemas));

    private void A_documents_operations_are_read_and_a_request_finds_the_one_its_method_and_path_call()
    {
        Assert.Equal("Shop | v1", Document.Title);
        Assert.Equal(["GET /items/{id}", "GET /items", "POST /items"], Document.Operations.Select(o => o.Title));
        Assert.Equal("GetItem", Document.OperationFor("GET", "/items/7")?.OperationId);
        Assert.Equal("GetItem", Document.OperationFor("get", "/Items/{{id}}?full=true")?.OperationId);
        Assert.Equal("ListItems", Document.OperationFor("GET", "/items/")?.OperationId);
        Assert.Equal("AddItem", Document.OperationFor("POST", "/items")?.OperationId);
        Assert.Null(Document.OperationFor("DELETE", "/items"));
        Assert.Null(Document.OperationFor("GET", "/items/7/tags"));
        Assert.Equal("id", Assert.Single(Document.Operations[0].Parameters).Name);
    }

    private const string Files = """
        {
          "openapi": "3.1.1",
          "paths": {
            "/files/{id}.json": { "get": { "operationId": "Json", "responses": { "200": { "description": "OK", "content": { "application/json": {
              "schema": { "type": "object", "required": [ "json" ] } } } } } } },
            "/files/{id}.xml": { "get": { "operationId": "Xml", "responses": { "200": { "description": "OK", "content": { "application/json": {
              "schema": { "type": "object", "required": [ "xml" ] } } } } } } },
            "/files/{id}": { "get": { "operationId": "Plain", "responses": { "200": { "description": "OK" } } } },
            "/files/new": { "get": { "operationId": "New", "responses": { "200": { "description": "OK" } } } },
            "/reports/{year}-{month}": { "get": { "operationId": "Report", "responses": { "200": { "description": "OK" } } } },
            "/v{version}/items": { "get": { "operationId": "Versioned", "responses": { "200": { "description": "OK" } } } }
          }
        }
        """;

    /// <summary>A template's literal text in a segment beside its <c>{name}</c> is held to as any other literal: each
    /// <c>{name}</c> takes at least a character, and of two templates that fit, the one with more text of its own is called,
    /// whichever the document lists first.</summary>
    private void A_template_with_text_around_a_parameter_takes_only_a_segment_with_that_text()
    {
        OpenApiDocument given = OpenApiDocument.Parse(Files)!;
        Assert.Equal("Json", given.OperationFor("GET", "/files/a.json")?.OperationId);
        Assert.Equal("Xml", given.OperationFor("GET", "/files/a.xml")?.OperationId);
        Assert.Equal("Xml", given.OperationFor("GET", "/FILES/a.XML")?.OperationId);
        Assert.Equal("Plain", given.OperationFor("GET", "/files/a.txt")?.OperationId);
        Assert.Equal("Plain", given.OperationFor("GET", "/files/.json")?.OperationId);
        Assert.Equal("New", given.OperationFor("GET", "/files/new")?.OperationId);
        Assert.Equal("Report", given.OperationFor("GET", "/reports/2026-11")?.OperationId);
        Assert.Null(given.OperationFor("GET", "/reports/2026"));
        Assert.Null(given.OperationFor("GET", "/reports/-11"));
        Assert.Equal("Versioned", given.OperationFor("GET", "/v2/items")?.OperationId);
        Assert.Null(given.OperationFor("GET", "/x2/items"));
    }

    private void A_response_is_held_to_the_schema_of_the_operation_its_literal_text_calls()
    {
        OpenApiDocument given = OpenApiDocument.Parse(Files)!;
        using JsonDocument answer = JsonDocument.Parse("""{ "xml": 1 }""");
        Assert.Empty(OpenApiSchemas.Check(given, given.OperationFor("GET", "/files/a.xml")!.ResponseSchema(200)!.Value, answer.RootElement));
        Assert.Equal([new SchemaBreak("/json", "is missing, though its schema requires it")],
            OpenApiSchemas.Check(given, given.OperationFor("GET", "/files/a.json")!.ResponseSchema(200)!.Value, answer.RootElement));
    }

    private static OpenApiDocument Orders(string member) => OpenApiDocument.Parse($$"""
        { "openapi": "3.1.1", "paths": { "/orders": { "post": { "requestBody": { "content": { "application/json": { "schema": {
          "type": "object", "properties": { "{{member}}": { "type": "string" } } } } } }, "responses": { "200": { "description": "OK" } } } } } }
        """)!;

    /// <summary>Two apps serving one route give a body each from its own document, however late the other came; another
    /// folder's documents hold neither, and an app that now gives none leaves none of what it gave.</summary>
    private void A_body_is_made_from_the_document_of_the_app_at_the_address_alone()
    {
        AppDocuments folder = new();
        Uri first = new("http://localhost:1111/orders");
        Uri second = new("http://localhost:2222/");
        folder.Took(first, Orders("customer"));
        Assert.Contains("customer", OpenApiRequests.BodyFor(folder.Of(first), "POST", "/orders"), StringComparison.Ordinal);
        folder.Took(second, Orders("supplier"));
        Assert.Contains("customer", OpenApiRequests.BodyFor(folder.Of(new Uri("http://LOCALHOST:1111")), "POST", "/orders"), StringComparison.Ordinal);
        Assert.Contains("supplier", OpenApiRequests.BodyFor(folder.Of(second), "POST", "/orders"), StringComparison.Ordinal);
        Assert.Null(folder.Of(new Uri("http://localhost:3333/orders")));
        Assert.Null(new AppDocuments().Of(first));
        Assert.Null(OpenApiRequests.BodyFor(folder.Of(first), "GET", "/orders"));
        Assert.Null(OpenApiRequests.BodyFor(folder.Of(first), "POST", "/invoices"));
        folder.Took(first, null);
        Assert.Null(folder.Of(first));
        Assert.NotNull(folder.Of(second));
    }

    /// <summary>A path lies at its app's origin however it is written, or nowhere: a reference to another host, or to
    /// another port of this one, is asked of no one.</summary>
    private void A_path_lies_at_its_apps_origin_and_one_written_to_leave_it_lies_nowhere()
    {
        Uri app = new("http://127.0.0.1:5000");
        Assert.Equal("http://127.0.0.1:5000/docs/v1.json?full=1", OpenApiAddresses.At(app, "/docs/v1.json?full=1")?.AbsoluteUri);
        Assert.Null(OpenApiAddresses.At(app, "//10.0.0.8:5000/spec.json"));
        Assert.Null(OpenApiAddresses.At(app, "//127.0.0.1:5001/spec.json"));
        Assert.Null(OpenApiAddresses.At(app, "http://10.0.0.8:5000/spec.json"));
        Assert.Null(OpenApiAddresses.At(app, @"\\10.0.0.8\spec.json"));
    }

    /// <summary>A redirect stays with an app at its origin, or as it moves a caller from HTTP to its secure address on the
    /// same host; another host or port, or a move off HTTPS, is another server's.</summary>
    private void A_redirect_stays_with_an_app_at_its_origin_or_moving_to_https_on_its_host()
    {
        Uri app = new("http://localhost:5000/openapi/v1.json");
        Assert.True(OpenApiAddresses.Stays(app, new Uri("http://LOCALHOST:5000/moved.json")));
        Assert.True(OpenApiAddresses.Stays(app, new Uri("https://localhost:7001/openapi/v1.json")));
        Assert.False(OpenApiAddresses.Stays(app, new Uri("http://localhost:5001/openapi/v1.json")));
        Assert.False(OpenApiAddresses.Stays(app, new Uri("http://10.0.0.8:5000/openapi/v1.json")));
        Assert.False(OpenApiAddresses.Stays(app, new Uri("https://10.0.0.8:7001/openapi/v1.json")));
        Assert.False(OpenApiAddresses.Stays(new Uri("https://localhost:7001/"), new Uri("http://localhost:5000/")));
        Assert.False(OpenApiAddresses.Stays(new Uri("https://localhost:7001/"), new Uri("https://localhost:7002/")));
    }

    private void A_document_named_by_a_reference_to_another_host_is_refused_as_a_url_of_another_machine_is()
    {
        Assert.Equal("/spec.json", OpenApiAddresses.Named("/spec.json").Document);
        Assert.Equal("http://127.0.0.1:5000/spec.json", OpenApiAddresses.Named("http://127.0.0.1:5000/spec.json").Document);
        Assert.Null(OpenApiAddresses.Named("//10.0.0.8:5000/spec.json").Document);
        Assert.Null(OpenApiAddresses.Named("http://10.0.0.8:5000/spec.json").Document);
        Assert.Equal("https://shop.dev.localhost:7001/spec.json", OpenApiAddresses.Named("https://shop.dev.localhost:7001/spec.json").Document);
        Assert.Null(OpenApiAddresses.Named("http://localhost.example.com:5000/spec.json").Document);
    }

    /// <summary>A member's pattern stopped at its time limit (held there in place of a slow machine) neither passes nor breaks
    /// it: it is said not checked, in the one wording, beside the breaks the rest of the check finds.</summary>
    private void A_pattern_stopped_at_its_time_limit_neither_passes_nor_breaks_the_member_and_says_it_was_not_checked()
    {
        const string Coded = "^[A-Z]{3}-[0-9]+$(?#held1984-code)";
        OpenApiDocument given = OpenApiDocument.Parse($$"""
            { "openapi": "3.1.1", "paths": { "/parts": { "get": { "operationId": "GetPart", "responses": { "200": { "description": "OK",
              "content": { "application/json": { "schema": { "type": "object", "properties": {
                "code": { "type": "string", "pattern": "{{Coded}}" }, "count": { "type": "integer" } } } } } } } } } } }
            """)!;
        using JsonDocument answer = JsonDocument.Parse("""{ "code": "abc", "count": "two" }""");
        JsonElement schema = given.Operations.Single().ResponseSchema(200)!.Value;
        Assert.Contains(new SchemaBreak("/code", "the string \"abc\" does not match its pattern ^[A-Z]{3}-[0-9]+$(?#held1984-code)"), OpenApiSchemas.Check(given, schema, answer.RootElement));
        using (Passenger.Text.TextSearch.HoldPastLimit("held1984-code"))
        {
            Assert.Equal(
                [
                    new SchemaBreak("/count", "a whole number was expected, not the string \"two\""),
                    new SchemaBreak("/code", $"the string \"abc\" was not checked against its pattern ^[A-Z]{{3}}-[0-9]+$(?#held1984-code): {Passenger.Text.TextSearch.RegexStopped}") { Unjudged = true },
                ],
                OpenApiSchemas.Check(given, schema, answer.RootElement));
        }
    }

    private void Each_member_that_breaks_its_schema_is_said_where_it_is_and_what_was_expected()
    {
        Assert.Equal(
            [
                new SchemaBreak("/name", "a string was expected, not the number 42"),
                new SchemaBreak("/price", "the string \"cheap\" does not match its pattern ^-?(?:0|[1-9]\\d*)(?:\\.\\d+)?(?:[eE][+-]?\\d+)?$"),
                new SchemaBreak("/tags/1", "a string was expected, not true"),
            ],
            Checked("GetItem", 200, """{ "name": 42, "price": "cheap", "tags": [ "hot", true ] }"""));
        Assert.Equal([new SchemaBreak("/price", "is missing, though its schema requires it")], Checked("GetItem", 200, """{ "name": "tea" }"""));
        Assert.Equal([new SchemaBreak("/1/name", "a string was expected, not null")], Checked("ListItems", 200, """[ { "name": "tea", "price": 1 }, { "name": null, "price": 2 } ]"""));
        Assert.Equal([new SchemaBreak("/", "an array was expected, not an object")], Checked("ListItems", 200, """{ "items": [] }"""));
        Assert.Equal([new SchemaBreak("/tags", "an array or null was expected, not the string \"hot\"")], Checked("GetItem", 200, """{ "name": "a", "price": 1, "tags": "hot" }"""));
    }

    private void A_body_made_from_a_schema_has_each_member_of_its_first_type_its_references_followed()
    {
        Assert.Equal("""{"name":"string","price":0,"tags":["string"]}""",
            OpenApiSchemas.Sample(Document, Document.Operations.Single(o => o.OperationId == "AddItem").RequestSchema!.Value)!.ToJsonString());
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "openapi": "3.0.1", "paths": {}, "components": { "schemas": {
              "Order": { "type": "object", "properties": { "at": { "type": "string", "format": "date-time" }, "state": { "type": "string", "enum": [ "new", "paid" ] },
                "qty": { "type": "integer", "example": 3 }, "next": { "$ref": "#/components/schemas/Order" } } } } } }
            """)!;
        using JsonDocument order = JsonDocument.Parse("""{ "$ref": "#/components/schemas/Order" }""");
        // A schema that refers to itself stops where it would go round again.
        Assert.Equal("""{"at":"2000-01-01T00:00:00Z","state":"new","qty":3,"next":null}""", OpenApiSchemas.Sample(given, order.RootElement)!.ToJsonString());
    }

    /// <summary>A request never sends what a server sets: a member marked readOnly stays out of a body, however the schema
    /// marks it, on the member, through its $ref, beside it (3.1), in its allOf or in a part beside the one that declares it,
    /// at any depth, an array's items and an example the schema gives too, required or not.</summary>
    private void A_body_made_from_a_schema_leaves_out_what_its_schema_marks_read_only_wherever_it_marks_it()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "openapi": "3.1.1", "paths": {}, "components": { "schemas": {
              "Text": { "type": "string" },
              "Stamp": { "type": "string", "readOnly": true },
              "OnMember": { "type": "object", "required": [ "id" ], "properties": { "id": { "type": "integer", "readOnly": true }, "name": { "type": "string" } } },
              "ThroughReference": { "type": "object", "properties": { "at": { "$ref": "#/components/schemas/Stamp" }, "name": { "type": "string" } } },
              "BesideReference": { "type": "object", "properties": { "at": { "$ref": "#/components/schemas/Text", "readOnly": true }, "name": { "type": "string" } } },
              "InMembersAllOf": { "type": "object", "properties": { "at": { "allOf": [ { "$ref": "#/components/schemas/Text" }, { "readOnly": true } ] }, "name": { "type": "string" } } },
              "Base": { "type": "object", "properties": { "id": { "type": "integer", "readOnly": true }, "name": { "type": "string" } } },
              "InParts": { "allOf": [ { "$ref": "#/components/schemas/Base" }, { "properties": { "note": { "type": "string" } } } ] },
              "Plain": { "type": "object", "properties": { "id": { "type": "integer" }, "name": { "type": "string" } } },
              "BesidePart": { "allOf": [ { "$ref": "#/components/schemas/Plain" }, { "properties": { "id": { "readOnly": true } } } ] },
              "Nested": { "type": "object", "properties": { "owner": { "$ref": "#/components/schemas/OnMember" }, "items": { "type": "array", "items": { "$ref": "#/components/schemas/InParts" } } } },
              "WithExample": { "type": "object", "example": { "id": 7, "name": "tea", "owner": { "id": 8, "name": "ann" }, "items": [ { "id": 9, "name": "cup" } ] },
                "properties": { "id": { "type": "integer", "readOnly": true }, "name": { "type": "string" }, "owner": { "$ref": "#/components/schemas/OnMember" },
                  "items": { "type": "array", "items": { "$ref": "#/components/schemas/OnMember" } } } },
              "Chosen": { "oneOf": [ { "$ref": "#/components/schemas/Plain" }, { "$ref": "#/components/schemas/OnMember" } ] },
              "ChosenAlone": { "oneOf": [ { "$ref": "#/components/schemas/OnMember" }, { "$ref": "#/components/schemas/Plain" } ] } } } }
            """)!;
        string Sampled(string schema)
        {
            using JsonDocument held = JsonDocument.Parse($$"""{ "$ref": "#/components/schemas/{{schema}}" }""");
            return OpenApiSchemas.Sample(given, held.RootElement)!.ToJsonString();
        }
        Assert.Equal("""{"name":"string"}""", Sampled("OnMember"));
        Assert.Equal("""{"name":"string"}""", Sampled("ThroughReference"));
        Assert.Equal("""{"name":"string"}""", Sampled("BesideReference"));
        Assert.Equal("""{"name":"string"}""", Sampled("InMembersAllOf"));
        Assert.Equal("""{"name":"string","note":"string"}""", Sampled("InParts"));
        Assert.Equal("""{"name":"string"}""", Sampled("BesidePart"));
        Assert.Equal("""{"owner":{"name":"string"},"items":[{"name":"string","note":"string"}]}""", Sampled("Nested"));
        Assert.Equal("""{"name":"tea","owner":{"name":"ann"},"items":[{"name":"cup"}]}""", Sampled("WithExample"));
        // What is read-only in an option the body did not take stays: it holds of that option's bodies alone.
        Assert.Equal("""{"id":0,"name":"string"}""", Sampled("Chosen"));
        Assert.Equal("""{"name":"string"}""", Sampled("ChosenAlone"));
    }

    /// <summary>A schema whose choice's options both refer back to it (a tree's node) is checked against a body 40 levels deep
    /// to its last member, each option of each level judged once: a body twice as deep costs about twice as much to check,
    /// where judging each option's subtree anew doubles the cost with every level.</summary>
    private void A_schema_referring_to_itself_through_a_choice_checks_a_body_40_levels_deep_to_its_last_member_judging_each_level_once()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "openapi": "3.1.1", "paths": {}, "components": { "schemas": {
              "Node": { "anyOf": [ { "$ref": "#/components/schemas/Strict" }, { "$ref": "#/components/schemas/Loose" } ] },
              "Strict": { "type": "object", "properties": { "next": { "$ref": "#/components/schemas/Node" }, "leaf": { "type": "string" } } },
              "Loose": { "type": "object", "properties": { "next": { "$ref": "#/components/schemas/Node" }, "leaf": { "type": [ "string", "null" ] } } } } } }
            """)!;
        using JsonDocument node = JsonDocument.Parse("""{ "$ref": "#/components/schemas/Node" }""");
        (IReadOnlyList<SchemaBreak> Breaks, long Bytes) Checked(int depth, string leaf)
        {
            using JsonDocument body = JsonDocument.Parse(string.Concat(Enumerable.Repeat("""{ "next": """, depth)) + $$"""{ "leaf": {{leaf}} }""" + new string('}', depth));
            long before = GC.GetAllocatedBytesForCurrentThread();
            IReadOnlyList<SchemaBreak> breaks = OpenApiSchemas.Check(given, node.RootElement, body.RootElement);
            return (breaks, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        // A first check readies what only the first pays for.
        Checked(10, "42");
        long shallow = Checked(10, "42").Bytes;
        long deep = Checked(20, "42").Bytes;
        Assert.True(deep < shallow * 8, $"A body 20 levels deep took {deep} bytes to check where one 10 levels deep took {shallow}, so a deeper body's check grows past what its size explains and stalls the window's background work");
        Assert.Equal([new SchemaBreak("/", "an object is none of the 2 kinds its schema's anyOf allows")], Checked(40, "42").Breaks);
        Assert.Empty(Checked(40, "\"tea\"").Breaks);
    }

    private void A_swagger_2_documents_body_parameter_response_schemas_and_definitions_are_read()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "swagger": "2.0", "paths": { "/pets": { "post": { "operationId": "AddPet",
                "parameters": [ { "name": "pet", "in": "body", "schema": { "$ref": "#/definitions/Pet" } } ],
                "responses": { "200": { "description": "OK", "schema": { "$ref": "#/definitions/Pet" } } } } } },
              "definitions": { "Pet": { "type": "object", "required": [ "name" ], "properties": { "name": { "type": "string" } } } } }
            """)!;
        OpenApiOperation add = Assert.Single(given.Operations);
        Assert.Equal("""{"name":"string"}""", OpenApiSchemas.Sample(given, add.RequestSchema!.Value)!.ToJsonString());
        using JsonDocument nameless = JsonDocument.Parse("{}");
        Assert.Equal([new SchemaBreak("/name", "is missing, though its schema requires it")], OpenApiSchemas.Check(given, add.ResponseSchema(200)!.Value, nameless.RootElement));
    }

    private void Requests_are_made_for_the_operations_a_file_does_not_call_yet_with_variables_and_bodies_from_the_schemas()
    {
        const string Existing = "@Web_HostAddress = http://localhost:5123\n\nGET {{Web_HostAddress}}/items\nAccept: application/json\n\n###\n";
        IReadOnlyList<OpenApiOperation> uncalled = OpenApiRequests.Uncalled(Document, Existing);
        Assert.Equal(["GetItem", "AddItem"], uncalled.Select(o => o.OperationId));
        string written = OpenApiRequests.Entries(Document, uncalled, Existing, "Web_HostAddress", "http://localhost:5123");
        Assert.Equal("""
            # OpenAPI: GetItem
            @id = 0
            GET {{Web_HostAddress}}/items/{{id}}
            Accept: application/json

            ###
            # OpenAPI: AddItem
            POST {{Web_HostAddress}}/items
            Accept: application/json
            Content-Type: application/json

            {
              "name": "string",
              "price": 0,
              "tags": [
                "string"
              ]
            }

            ###

            """.Replace("\r\n", "\n", StringComparison.Ordinal), written);
        Assert.Empty(OpenApiRequests.Uncalled(Document, Existing + written));
        // A file made for them begins with the project's address.
        Assert.StartsWith("@Web_HostAddress = http://localhost:5123\n\n# OpenAPI: GetItem\n", OpenApiRequests.Entries(Document, uncalled, string.Empty, "Web_HostAddress", "http://localhost:5123"),
            StringComparison.Ordinal);
    }

}
