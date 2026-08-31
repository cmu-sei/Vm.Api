// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The freshness check for the API client <c>vm.ui</c> has checked in: that the OpenAPI document the
/// running application serves still describes the surface <c>contracts/openapi-surface.json</c> records.
/// </summary>
/// <remarks>
/// <para>
/// <c>vm.ui/src/app/generated/vm-api</c> is a generated client that is committed rather than built.
/// <c>npm run swagger:gen</c> regenerates it against <c>localhost:4302</c>, and nothing runs that on any
/// schedule, in any pipeline, or as a condition of merging. So a DTO property renamed here changes the
/// JSON the API sends and changes nothing about the TypeScript interface the browser parses it into: both
/// repositories build, both test suites pass, and the field is <c>undefined</c> in production.
/// </para>
/// <para>
/// This test cannot see <c>vm.ui</c> - this repository's CI runs alone - so it does the half it can. It
/// pins the surface, which means a change that would make the checked-in client wrong cannot be merged
/// without either updating the snapshot or noticing why it moved. The other half, that the checked-in
/// client actually matches the pinned surface, is
/// <c>crucible-tests/playerVm/tests/contract/openapi-surface.spec.ts</c>, which can see both repositories.
/// </para>
/// <para>
/// The snapshot is a derived summary, not the document. The document is 170KB and most of it is XML doc
/// comments, so a snapshot of it reddens when a <c>&lt;summary&gt;</c> is reworded - and a test that
/// fails for reasons that do not matter gets regenerated without being read, which is the failure mode
/// that makes snapshot tests worthless. What is kept is what a generated client is built out of:
/// operation ids and tags, because they become method and service names; parameters, request bodies and
/// response types, because they become signatures; and schema properties with their types, nullability
/// and required flags, because they become interfaces.
/// </para>
/// </remarks>
public class OpenApiSurfaceTests(DatabaseFixture fixture, VmApiFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<VmApiFactory>
{
    private const string DocumentPath = "/swagger/v1/swagger.json";

    /// <summary>
    /// The whole point of the class. A diff here names the operation or the property that moved, and one
    /// of two things is true: the change was meant, and the snapshot is regenerated with
    /// <c>VMAPI_UPDATE_CONTRACTS=1</c> and <c>vm.ui</c>'s client regenerated with it; or it was not, and
    /// something has been renamed out from under every client of this API.
    /// </summary>
    [Fact]
    public async Task TheDocumentsSurface_IsTheOneTheSnapshotRecords()
    {
        await Contracts.AssertMatchesOrRewrite(
            Contracts.OpenApiSurfaceFileName, Surface(await Document()), Ct);
    }

    /// <summary>
    /// The snapshot is only worth anything if the document is the same every time it is generated. Swashbuckle
    /// walks controllers and model metadata to build it, and a dictionary iterated in hash order anywhere in
    /// that walk would give a snapshot that fails at random and gets deleted rather than read.
    /// </summary>
    [Fact]
    public async Task TheSurface_IsTheSameOnASecondFetch()
    {
        Assert.Equal(Surface(await Document()), Surface(await Document()));
    }

    /// <summary>
    /// Nothing in the document references a schema it does not define. A dangling <c>$ref</c> is not a
    /// documentation problem: the generator resolves refs to produce its models, so it either fails or
    /// emits a client with a type that does not exist.
    /// </summary>
    /// <remarks>
    /// Worth its own test because <c>ModelDocumentFilter</c> adds schemas to the document by hand for
    /// types no controller signature mentions, which is exactly the arrangement in which a rename leaves
    /// a reference behind.
    /// </remarks>
    [Fact]
    public async Task EverySchemaTheDocumentReferences_IsDefined()
    {
        var document = await Document();
        var defined = document["components"]["schemas"].AsObject().Select(x => x.Key).ToHashSet();

        Assert.All(References(document).Distinct(), x => Assert.Contains(x, defined));
    }

    #region The document

    private async Task<JsonObject> Document() =>
        JsonNode.Parse(await AnonymousClient.GetStringAsync(DocumentPath, Ct)).AsObject();

    /// <summary>Every schema name reached by a <c>$ref</c>, anywhere in the document.</summary>
    private static IEnumerable<string> References(JsonNode node) =>
        node switch
        {
            JsonObject o => o.SelectMany(x =>
                x.Key == "$ref" && x.Value is JsonValue
                    ? (IEnumerable<string>)[RefName(x.Value)]
                    : References(x.Value)),
            JsonArray a => a.SelectMany(References),
            _ => [],
        };

    #endregion

    #region The surface

    /// <summary>
    /// The document reduced to what a generated client is built out of, as indented JSON with every
    /// collection in a fixed order.
    /// </summary>
    private static string Surface(JsonObject document)
    {
        var operations = new JsonObject();

        foreach (var path in document["paths"].AsObject().OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            foreach (var method in path.Value.AsObject().OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                operations[$"{method.Key.ToUpperInvariant()} {path.Key}"] = Operation(method.Value.AsObject());
            }
        }

        var schemas = new JsonObject();

        foreach (var schema in document["components"]["schemas"].AsObject()
            .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            schemas[schema.Key] = Schema(schema.Value.AsObject());
        }

        return Contracts.Render(new JsonObject
        {
            ["openapi"] = document["openapi"].GetValue<string>(),
            ["operations"] = operations,
            ["schemas"] = schemas,
        });
    }

    /// <remarks>
    /// <c>tags</c> is here because <c>typescript-angular</c> groups operations into one injectable service
    /// per tag: renaming a tag renames a class the application imports by name, which is a compile error
    /// in <c>vm.ui</c> and nothing at all here.
    /// </remarks>
    private static JsonObject Operation(JsonObject operation)
    {
        var surface = new JsonObject
        {
            ["operationId"] = operation["operationId"]?.GetValue<string>(),
            ["tags"] = Strings(operation["tags"]),
            ["parameters"] = Strings(operation["parameters"]?.AsArray().Select(Parameter).Order()),
            ["requestBody"] = operation["requestBody"] is JsonObject body
                ? new JsonObject
                {
                    ["required"] = body["required"]?.GetValue<bool>() ?? false,
                    ["content"] = Content(body),
                }
                : null,
            ["responses"] = new JsonObject(),
        };

        foreach (var response in (operation["responses"]?.AsObject() ?? new JsonObject())
            .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            surface["responses"][response.Key] = Content(response.Value.AsObject());
        }

        return Compact(surface);
    }

    /// <summary>
    /// The media types a body or response carries and the type behind each. The media type is kept because
    /// the generator picks the first one it supports, so a body that stops offering
    /// <c>application/json</c> changes the client.
    /// </summary>
    private static JsonNode Content(JsonObject carrier)
    {
        if (carrier["content"] is not JsonObject content)
        {
            return null;
        }

        var surface = new JsonObject();

        foreach (var media in content.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            surface[media.Key] = Signature(media.Value["schema"]);
        }

        return surface;
    }

    private static string Parameter(JsonNode parameter) =>
        $"{parameter["in"].GetValue<string>()} {parameter["name"].GetValue<string>()}" +
        $"{((parameter["required"]?.GetValue<bool>() ?? false) ? string.Empty : "?")}" +
        $": {Signature(parameter["schema"])}";

    /// <remarks>
    /// A named object schema gets its properties listed one per line rather than inlined into its type,
    /// which is the whole reason the properties are broken out: a diff on this file should be one line per
    /// property that moved. An enum or a bare scalar has no properties and is its type alone.
    /// </remarks>
    private static JsonObject Schema(JsonObject schema) =>
        Compact(new JsonObject
        {
            ["type"] = schema["properties"] is JsonObject ? "object" : Signature(schema),
            ["required"] = Strings(schema["required"]),
            ["properties"] = schema["properties"] is JsonObject properties
                ? Properties(properties)
                : null,
        });

    /// <summary>
    /// The object without the keys nothing filled in. Applied one level deep and to the operation and
    /// schema objects only, so a response with no content stays as an explicit null - which is a fact
    /// about the operation rather than a gap in the summary.
    /// </summary>
    private static JsonObject Compact(JsonObject surface)
    {
        foreach (var empty in surface.Where(x => x.Value is null).Select(x => x.Key).ToArray())
        {
            surface.Remove(empty);
        }

        return surface;
    }

    private static JsonObject Properties(JsonObject properties)
    {
        var surface = new JsonObject();

        foreach (var property in properties.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            surface[property.Key] = Signature(property.Value);
        }

        return surface;
    }

    /// <summary>
    /// One schema node as a single string: the name of what it refers to, or its type with its format,
    /// nullability, enum values and - for an inline object - its own properties.
    /// </summary>
    /// <remarks>
    /// A string rather than a nested object because these are read in a diff, and a property whose type
    /// changed should be one changed line. Named schemas get the structured form in <see cref="Schema"/>;
    /// this is what their properties are rendered as, and it is deliberately complete enough that an
    /// inline anonymous object - which the file-upload operations use - cannot change unnoticed.
    /// </remarks>
    private static string Signature(JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return "any";
        }

        if (schema["$ref"] is JsonNode reference)
        {
            return RefName(reference);
        }

        string[] compositions = ["allOf", "oneOf", "anyOf"];

        foreach (var composition in compositions)
        {
            if (schema[composition] is JsonArray members)
            {
                return $"{composition}[{string.Join(", ", members.Select(Signature))}]";
            }
        }

        var nullable = (schema["nullable"]?.GetValue<bool>() ?? false) ? "?" : string.Empty;
        var type = schema["type"]?.GetValue<string>();

        if (type == "array")
        {
            return $"{Signature(schema["items"])}[]{nullable}";
        }

        if (schema["enum"] is JsonArray values)
        {
            return $"{type}({string.Join("|", values.Select(x => x.GetValue<string>()))}){nullable}";
        }

        if (schema["properties"] is JsonObject properties)
        {
            return "object{" +
                string.Join(", ", Properties(properties).Select(x => $"{x.Key}: {x.Value.GetValue<string>()}")) +
                "}" + nullable;
        }

        var format = schema["format"]?.GetValue<string>();

        return $"{type ?? "any"}{(format is null ? string.Empty : $"({format})")}{nullable}";
    }

    private static string RefName(JsonNode reference) =>
        reference.GetValue<string>().Split('/')[^1];

    private static JsonArray Strings(JsonNode node) =>
        node is JsonArray array ? Strings(array.Select(x => x.GetValue<string>())) : null;

    private static JsonArray Strings(IEnumerable<string> values) =>
        values is null ? null : [.. values.Select(x => (JsonNode)x)];

    #endregion
}
