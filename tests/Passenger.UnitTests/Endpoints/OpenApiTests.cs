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

    [Fact]
    public void A_documents_operations_are_read_and_a_request_finds_the_one_its_method_and_path_call()
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
    [Fact]
    public void A_template_with_text_around_a_parameter_takes_only_a_segment_with_that_text()
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

    /// <summary>A request's <c>{{variable}}</c> may hold anything, the text beside it none the less held to: a request made
    /// from a template is shaped as that template, and calls it over any other a variable could also spell.</summary>
    [Fact]
    public void A_requests_variable_takes_any_text_and_the_text_beside_it_still_chooses_the_operation()
    {
        OpenApiDocument given = OpenApiDocument.Parse(Files)!;
        Assert.Equal("Xml", given.OperationFor("GET", "/files/{{id}}.xml")?.OperationId);
        Assert.Equal("Plain", given.OperationFor("GET", "/files/{{id}}")?.OperationId);
        Assert.Equal("Report", given.OperationFor("GET", "/reports/{{year}}-{{month}}")?.OperationId);
        Assert.Equal("Report", given.OperationFor("GET", "/reports/{{period}}")?.OperationId);
        Assert.Equal("Plain", given.OperationFor("GET", "/files/{{id}}.txt")?.OperationId);
        Assert.Null(given.OperationFor("GET", "/reports/{{year}}/x"));
    }

    [Fact]
    public void Requests_are_made_for_the_templates_a_file_does_not_call_each_told_from_the_rest_by_its_text()
    {
        OpenApiDocument given = OpenApiDocument.Parse(Files)!;
        const string Existing = "@host = http://localhost:5123\n\nGET {{host}}/files/{{id}}.xml\n\n###\nGET {{host}}/files/{{id}}\n\n###\n";
        Assert.Equal(["Json", "New", "Report", "Versioned"], OpenApiRequests.Uncalled(given, Existing).Select(o => o.OperationId));
        string written = OpenApiRequests.Entries(given, OpenApiRequests.Uncalled(given, Existing), Existing, "host", "http://localhost:5123");
        Assert.Contains("GET {{host}}/files/{{id}}.json\n", written, StringComparison.Ordinal);
        Assert.Empty(OpenApiRequests.Uncalled(given, Existing + written));
    }

    [Fact]
    public void A_response_is_held_to_the_schema_of_the_operation_its_literal_text_calls()
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
    [Fact]
    public void A_body_is_made_from_the_document_of_the_app_at_the_address_alone()
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
    [Fact]
    public void A_path_lies_at_its_apps_origin_and_one_written_to_leave_it_lies_nowhere()
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
    [Fact]
    public void A_redirect_stays_with_an_app_at_its_origin_or_moving_to_https_on_its_host()
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

    [Fact]
    public void A_document_named_by_a_reference_to_another_host_is_refused_as_a_url_of_another_machine_is()
    {
        Assert.Equal("/spec.json", OpenApiAddresses.Named("/spec.json").Document);
        Assert.Equal("http://127.0.0.1:5000/spec.json", OpenApiAddresses.Named("http://127.0.0.1:5000/spec.json").Document);
        Assert.Null(OpenApiAddresses.Named("//10.0.0.8:5000/spec.json").Document);
        Assert.Null(OpenApiAddresses.Named("http://10.0.0.8:5000/spec.json").Document);
    }

    [Fact]
    public void Text_that_is_no_OpenAPI_document_is_read_as_none()
    {
        Assert.Null(OpenApiDocument.Parse("{ \"paths\": {} }"));
        Assert.Null(OpenApiDocument.Parse("<html>"));
        Assert.Null(OpenApiDocument.Parse("[1, 2]"));
    }

    [Fact]
    public void A_response_that_keeps_to_its_schema_breaks_nothing_a_number_given_as_its_pattern_allows_too()
    {
        Assert.Empty(Checked("GetItem", 200, """{ "name": "tea", "price": 2.5, "tags": null }"""));
        Assert.Empty(Checked("GetItem", 200, """{ "name": "tea", "price": "2.5", "tags": [ "hot" ] }"""));
        Assert.Empty(Checked("ListItems", 200, """[]"""));
    }

    [Fact]
    public void Each_member_that_breaks_its_schema_is_said_where_it_is_and_what_was_expected()
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

    [Fact]
    public void A_response_whose_status_the_document_gives_no_json_body_is_held_to_nothing()
    {
        Assert.Null(Document.Operations.Single(o => o.OperationId == "GetItem").ResponseSchema(404));
        Assert.NotNull(Document.Operations.Single(o => o.OperationId == "AddItem").ResponseSchema(201));
    }

    [Fact]
    public void A_body_made_from_a_schema_has_each_member_of_its_first_type_its_references_followed()
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
    [Fact]
    public void A_body_made_from_a_schema_leaves_out_what_its_schema_marks_read_only_wherever_it_marks_it()
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

    /// <summary>A form's and a multipart body's fields are made from a schema too, so what a server sets is no field of either.</summary>
    [Fact]
    public void A_form_or_multipart_request_written_for_an_operation_has_no_field_its_schema_marks_read_only()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "openapi": "3.1.1", "paths": { "/forms": { "post": { "operationId": "Form", "requestBody": { "content": { "application/x-www-form-urlencoded": { "schema": { "$ref": "#/components/schemas/Upload" } } } },
                "responses": { "204": { "description": "None" } } } },
              "/files": { "post": { "operationId": "File", "requestBody": { "content": { "multipart/form-data": { "schema": { "$ref": "#/components/schemas/Derived" } } } },
                "responses": { "204": { "description": "None" } } } } },
              "components": { "schemas": {
                "Upload": { "type": "object", "properties": { "id": { "type": "integer", "readOnly": true }, "title": { "type": "string" } } },
                "Derived": { "allOf": [ { "$ref": "#/components/schemas/Upload" } ] } } } }
            """)!;
        string written = OpenApiRequests.Entries(given, given.Operations, string.Empty, "host", null);
        Assert.Contains("title=string\n", written, StringComparison.Ordinal);
        Assert.Contains("name=\"title\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("id=", written, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"id\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_openapi_3_0_nullable_member_takes_null_and_a_closed_object_refuses_members_it_does_not_have()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "openapi": "3.0.1", "paths": {}, "components": { "schemas": {
              "Note": { "type": "object", "additionalProperties": false, "properties": { "text": { "type": "string", "nullable": true } } } } } }
            """)!;
        using JsonDocument note = JsonDocument.Parse("""{ "$ref": "#/components/schemas/Note" }""");
        using JsonDocument good = JsonDocument.Parse("""{ "text": null }""");
        using JsonDocument extra = JsonDocument.Parse("""{ "text": "a", "color": "red" }""");
        Assert.Empty(OpenApiSchemas.Check(given, note.RootElement, good.RootElement));
        Assert.Equal([new SchemaBreak("/color", "is not a member its schema has")], OpenApiSchemas.Check(given, note.RootElement, extra.RootElement));
    }

    /// <summary>A oneOf holds a value that keeps to exactly one of its options: one keeping to two breaks it, said with the
    /// two it keeps to (by the schema a $ref names, else by its place), where an anyOf takes it.</summary>
    [Fact]
    public void A_oneOf_holds_a_value_keeping_to_one_option_alone_and_says_which_two_one_keeps_to()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "openapi": "3.1.1", "paths": {}, "components": { "schemas": {
              "Pet": { "oneOf": [ { "$ref": "#/components/schemas/Cat" }, { "$ref": "#/components/schemas/Dog" } ] },
              "Cat": { "type": "object", "required": [ "meows" ], "properties": { "meows": { "type": "boolean" } } },
              "Dog": { "type": "object", "required": [ "barks" ], "properties": { "barks": { "type": "boolean" } } } } } }
            """)!;
        IReadOnlyList<SchemaBreak> Checked(string schema, string json)
        {
            using JsonDocument held = JsonDocument.Parse(schema);
            using JsonDocument value = JsonDocument.Parse(json);
            return OpenApiSchemas.Check(given, held.RootElement, value.RootElement);
        }
        const string Pet = """{ "$ref": "#/components/schemas/Pet" }""";
        Assert.Empty(Checked(Pet, """{ "meows": true }"""));
        Assert.Equal([new SchemaBreak("/", "an object fits both Cat and Dog of its schema's oneOf, which allows one alone")],
            Checked(Pet, """{ "meows": true, "barks": false }"""));
        Assert.Equal([new SchemaBreak("/", "an object is none of the 2 kinds its schema's oneOf allows")], Checked(Pet, """{ "purrs": true }"""));
        const string Numbers = """{ "oneOf": [ { "type": "integer" }, { "type": "number" } ] }""";
        Assert.Empty(Checked(Numbers, "2.5"));
        Assert.Equal([new SchemaBreak("/", "the number 3 fits both option 1 and option 2 of its schema's oneOf, which allows one alone")], Checked(Numbers, "3"));
        Assert.Empty(Checked("""{ "anyOf": [ { "type": "integer" }, { "type": "number" } ] }""", "3"));
    }

    /// <summary>A required member marked write-only is a request's alone, so a response is not asked for it, wherever its
    /// schema marks it: on the member, through its reference, beside it, in an allOf, or in another part of an allOf than the
    /// one requiring it. A read-only member stays required, a write-only one that comes is still held to its type, and the
    /// same name in an object beneath keeps its own schema's word.</summary>
    [Fact]
    public void A_required_write_only_member_is_not_asked_of_a_response_wherever_its_schema_marks_it()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            { "openapi": "3.1.1", "paths": {}, "components": { "schemas": {
              "Text": { "type": "string" },
              "Secret": { "type": "string", "writeOnly": true },
              "Account": { "type": "object", "required": [ "id", "password" ], "properties": {
                "id": { "type": "integer", "readOnly": true }, "password": { "type": "string", "writeOnly": true } } },
              "ByReference": { "type": "object", "required": [ "password" ], "properties": { "password": { "$ref": "#/components/schemas/Secret" } } },
              "BesideReference": { "type": "object", "required": [ "password" ], "properties": { "password": { "$ref": "#/components/schemas/Text", "writeOnly": true } } },
              "InAllOf": { "type": "object", "required": [ "password" ], "properties": { "password": { "allOf": [ { "$ref": "#/components/schemas/Text" }, { "writeOnly": true } ] } } },
              "Base": { "type": "object", "properties": { "password": { "type": "string", "writeOnly": true } } },
              "Derived": { "allOf": [ { "$ref": "#/components/schemas/Base" }, { "required": [ "password" ] } ] },
              "RequiredOver": { "required": [ "password" ], "allOf": [ { "$ref": "#/components/schemas/Base" } ] },
              "Holder": { "type": "object", "required": [ "password", "inner" ], "properties": { "password": { "type": "string", "writeOnly": true },
                "inner": { "type": "object", "required": [ "password" ], "properties": { "password": { "type": "string" } } } } },
              "Loop": { "required": [ "name" ], "allOf": [ { "$ref": "#/components/schemas/Loop" }, { "$ref": "#/components/schemas/Loop" } ] } } } }
            """)!;
        IReadOnlyList<SchemaBreak> Checked(string schema, string json)
        {
            using JsonDocument held = JsonDocument.Parse($$"""{ "$ref": "#/components/schemas/{{schema}}" }""");
            using JsonDocument value = JsonDocument.Parse(json);
            return OpenApiSchemas.Check(given, held.RootElement, value.RootElement);
        }
        Assert.Empty(Checked("Account", """{ "id": 1 }"""));
        Assert.Equal([new SchemaBreak("/id", "is missing, though its schema requires it")], Checked("Account", "{}"));
        Assert.Equal([new SchemaBreak("/password", "a string was expected, not the number 5")], Checked("Account", """{ "id": 1, "password": 5 }"""));
        Assert.Empty(Checked("ByReference", "{}"));
        Assert.Empty(Checked("BesideReference", "{}"));
        Assert.Empty(Checked("InAllOf", "{}"));
        Assert.Empty(Checked("Derived", "{}"));
        Assert.Empty(Checked("RequiredOver", "{}"));
        Assert.Equal([new SchemaBreak("/inner/password", "is missing, though its schema requires it")], Checked("Holder", """{ "inner": {} }"""));
        Assert.Equal([new SchemaBreak("/name", "is missing, though its schema requires it")], Checked("Loop", "{}"));
    }

    /// <summary>A schema whose choice's options both refer back to it (a tree's node) is checked against a body 40 levels deep
    /// to its last member, each option of each level judged once: a body twice as deep costs about twice as much to check,
    /// where judging each option's subtree anew doubles the cost with every level.</summary>
    [Fact]
    public void A_schema_referring_to_itself_through_a_choice_checks_a_body_40_levels_deep_to_its_last_member_judging_each_level_once()
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

    [Fact]
    public void A_swagger_2_documents_body_parameter_response_schemas_and_definitions_are_read()
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

    [Fact]
    public void Requests_are_made_for_the_operations_a_file_does_not_call_yet_with_variables_and_bodies_from_the_schemas()
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

    /// <summary>Each request made works as written: a required header is sent, an array query parameter is written as its
    /// item, a form body is made from the form's schema, and a later operation's parameter of a name an earlier one set to
    /// another kind of value has a variable of its own, never the earlier's (a long id given a GUID).</summary>
    [Fact]
    public void The_requests_made_send_required_headers_array_items_form_bodies_and_their_own_values_for_names_alike()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            {
              "openapi": "3.1.1",
              "info": { "title": "Orders", "version": "1.0.0" },
              "paths": {
                "/search": {
                  "get": {
                    "operationId": "Search",
                    "parameters": [
                      { "name": "q", "in": "query", "required": true, "schema": { "type": "string" } },
                      { "name": "X-Tenant", "in": "header", "required": true, "schema": { "type": "string" } },
                      { "name": "tags", "in": "query", "required": true, "schema": { "type": "array", "items": { "type": "string" } } }
                    ],
                    "responses": { "200": { "description": "OK" } }
                  }
                },
                "/orders/{id}/lines": {
                  "get": {
                    "operationId": "Lines",
                    "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" } } ],
                    "responses": { "200": { "description": "OK" } }
                  }
                },
                "/orders/{id}": {
                  "delete": {
                    "operationId": "DeleteOrder",
                    "parameters": [ { "name": "id", "in": "path", "required": true,
                      "schema": { "type": [ "integer", "string" ], "format": "int64", "pattern": "^-?(?:0|[1-9]\\d*)$" } } ],
                    "responses": { "204": { "description": "No Content" } }
                  }
                },
                "/form": {
                  "post": {
                    "operationId": "Form",
                    "requestBody": { "required": true, "content": {
                      "multipart/form-data": { "schema": { "type": "object", "properties": { "name": { "type": "string" } } } },
                      "application/x-www-form-urlencoded": { "schema": { "type": "object",
                        "allOf": [ { "type": "object", "properties": { "name": { "type": "string" } } },
                                   { "type": "object", "properties": { "age": { "type": "integer" } } } ] } } } },
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              }
            }
            """)!;
        string written = OpenApiRequests.Entries(given, given.Operations, string.Empty, "Web_HostAddress", "http://localhost:5123");
        Assert.Equal("""
            @Web_HostAddress = http://localhost:5123

            # OpenAPI: Search
            @q = string
            @tags = string
            GET {{Web_HostAddress}}/search?q={{q}}&tags={{tags}}
            Accept: application/json
            X-Tenant: string

            ###
            # OpenAPI: Lines
            @id = 00000000-0000-0000-0000-000000000000
            GET {{Web_HostAddress}}/orders/{{id}}/lines
            Accept: application/json

            ###
            # OpenAPI: DeleteOrder
            @DeleteOrder_id = 0
            DELETE {{Web_HostAddress}}/orders/{{DeleteOrder_id}}
            Accept: application/json

            ###
            # OpenAPI: Form
            POST {{Web_HostAddress}}/form
            Accept: application/json
            Content-Type: application/x-www-form-urlencoded

            name=string&age=0

            ###

            """.Replace("\r\n", "\n", StringComparison.Ordinal), written);
    }

    /// <summary>An upload whose only body is multipart has a part a field, a file's a text file's part the person fills in,
    /// between its boundaries, so the request works as written.</summary>
    [Fact]
    public void An_uploads_request_has_a_multipart_body_of_a_part_a_field()
    {
        OpenApiDocument given = OpenApiDocument.Parse("""
            {
              "openapi": "3.1.1",
              "info": { "title": "Files", "version": "1.0.0" },
              "paths": {
                "/upload": {
                  "post": {
                    "operationId": "Upload",
                    "requestBody": { "required": true, "content": { "multipart/form-data": { "schema": { "type": "object", "required": [ "file" ],
                      "properties": { "file": { "$ref": "#/components/schemas/IFormFile" }, "note": { "type": "string" } } } } } },
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              },
              "components": { "schemas": { "IFormFile": { "type": "string", "format": "binary" } } }
            }
            """)!;
        string written = OpenApiRequests.Entries(given, given.Operations, string.Empty, "Web_HostAddress", "http://localhost:5123");
        Assert.Equal("""
            @Web_HostAddress = http://localhost:5123

            # OpenAPI: Upload
            POST {{Web_HostAddress}}/upload
            Accept: application/json
            Content-Type: multipart/form-data; boundary=passenger

            --passenger
            Content-Disposition: form-data; name="file"; filename="file.txt"
            Content-Type: text/plain

            The file's text
            --passenger
            Content-Disposition: form-data; name="note"

            string
            --passenger--

            ###

            """.Replace("\r\n", "\n", StringComparison.Ordinal), written);
    }
}
