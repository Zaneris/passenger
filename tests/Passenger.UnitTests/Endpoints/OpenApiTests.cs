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
}
