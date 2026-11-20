using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GenerateHandlers;

/// <summary>
/// Generates S3's REST-XML protocol from its service model. S3 names an operation by its HTTP method,
/// its path (the service root, a bucket or an object) and the markers in its query string, and
/// spreads a request over the path, the query string, headers and the body. This writes:
/// <list type="bullet">
/// <item><c>Models/S3Models.g.cs</c>: a class per structure.</item>
/// <item><c>S3XmlSerializers.g.cs</c>: <c>Read_X</c> (an XML element into a model, refusing elements the
/// model doesn't have as S3's schema does) and <c>Write_X</c> per structure.</item>
/// <item><c>S3WireProtocol.g.cs</c>: the router (method, path and query to operation), and the
/// dispatcher, which binds each request member from where the model puts it, calls the client,
/// and writes each response member to its header or into the body.</item>
/// <item><c>S3ClientBase.g.cs</c>: a virtual method per operation answering <c>NotImplemented</c>, which
/// <c>InternalS3Client</c> overrides operation by operation.</item>
/// </list>
/// </summary>
internal sealed class RestXmlCodeGenerator
{
    private const string Namespace = "InMemoryAws.S3.Model";

    /// <summary>
    /// Operations left out of the router: deprecated operations that share their method, path and
    /// query with the operation that replaced them, ListDirectoryBuckets (the same <c>GET /</c> as
    /// ListBuckets, told apart by its host) and WriteGetObjectResponse (S3 Object Lambda).
    /// </summary>
    private static readonly HashSet<string> Unrouted = new(StringComparer.Ordinal)
    {
        "GetBucketLifecycle", "PutBucketLifecycle", "GetBucketNotification", "PutBucketNotification",
        "ListDirectoryBuckets", "WriteGetObjectResponse",
    };

    /// <summary>Class names that would shadow a common type, renamed.</summary>
    private static readonly Dictionary<string, string> RenamedClasses = new(StringComparer.Ordinal)
    {
        ["Object"] = "S3Object",
        ["Error"] = "S3Error",
    };

    /// <summary>
    /// The root element of a response body S3 writes from an output's own members (an output with
    /// no payload member): the model doesn't record it, and SDKs don't read it.
    /// </summary>
    private static readonly Dictionary<string, string> OutputRoots = new(StringComparer.Ordinal)
    {
        ["CompleteMultipartUploadOutput"] = "CompleteMultipartUploadResult",
        ["CreateMultipartUploadOutput"] = "InitiateMultipartUploadResult",
        ["CreateSessionOutput"] = "CreateSessionResult",
        ["DeleteObjectsOutput"] = "DeleteResult",
        ["GetBucketAccelerateConfigurationOutput"] = "AccelerateConfiguration",
        ["GetBucketAclOutput"] = "AccessControlPolicy",
        ["GetBucketCorsOutput"] = "CORSConfiguration",
        ["GetBucketLifecycleOutput"] = "LifecycleConfiguration",
        ["GetBucketLifecycleConfigurationOutput"] = "LifecycleConfiguration",
        ["GetBucketLocationOutput"] = "LocationConstraint",
        ["GetBucketLoggingOutput"] = "BucketLoggingStatus",
        ["NotificationConfigurationDeprecated"] = "NotificationConfiguration",
        ["NotificationConfiguration"] = "NotificationConfiguration",
        ["GetBucketRequestPaymentOutput"] = "RequestPaymentConfiguration",
        ["GetBucketTaggingOutput"] = "Tagging",
        ["GetBucketVersioningOutput"] = "VersioningConfiguration",
        ["GetBucketWebsiteOutput"] = "WebsiteConfiguration",
        ["GetObjectAclOutput"] = "AccessControlPolicy",
        ["GetObjectAttributesOutput"] = "GetObjectAttributesResponse",
        ["GetObjectTaggingOutput"] = "Tagging",
        ["ListBucketAnalyticsConfigurationsOutput"] = "ListBucketAnalyticsConfigurationResult",
        ["ListBucketIntelligentTieringConfigurationsOutput"] = "ListBucketIntelligentTieringConfigurationsOutput",
        ["ListBucketInventoryConfigurationsOutput"] = "ListInventoryConfigurationsResult",
        ["ListBucketMetricsConfigurationsOutput"] = "ListMetricsConfigurationsResult",
        ["ListBucketsOutput"] = "ListAllMyBucketsResult",
        ["ListDirectoryBucketsOutput"] = "ListAllMyDirectoryBucketsResult",
        ["ListMultipartUploadsOutput"] = "ListMultipartUploadsResult",
        ["ListObjectAnnotationsOutput"] = "ListObjectAnnotationsResult",
        ["ListObjectVersionsOutput"] = "ListVersionsResult",
        ["ListObjectsOutput"] = "ListBucketResult",
        ["ListObjectsV2Output"] = "ListBucketResult",
        ["ListPartsOutput"] = "ListPartsResult",
        ["PutObjectAnnotationOutput"] = "PutObjectAnnotationResult",
    };

    private readonly JsonElement _shapes;
    private readonly List<(string Name, JsonElement Operation)> _operations;
    private readonly SortedSet<string> _structures = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _readable = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _writable = new(StringComparer.Ordinal);

    public RestXmlCodeGenerator(JsonElement root)
    {
        _shapes = root.GetProperty("shapes");
        _operations = root.GetProperty("operations").EnumerateObject()
            .Select(o => (o.Name, o.Value))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var (_, operation) in _operations)
        {
            if (InputShape(operation) is { } input)
            {
                Collect(input, _structures);
                foreach (var (_, member) in Members(Shape(input)))
                {
                    if (!member.TryGetProperty("location", out _))
                    {
                        Collect(ShapeOf(member), _readable);
                    }
                }
            }

            if (OutputShape(operation) is { } output)
            {
                Collect(output, _structures);
                foreach (var (_, member) in Members(Shape(output)))
                {
                    if (!member.TryGetProperty("location", out _))
                    {
                        Collect(ShapeOf(member), _writable);
                    }
                }
            }
        }

        // A configuration S3 stores and answers is read from one request and written into another response.
        foreach (var name in _readable.Concat(_writable).ToList())
        {
            _structures.Add(name);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Model helpers
    // ------------------------------------------------------------------------------------------

    private JsonElement Shape(string name) => _shapes.GetProperty(name);

    private string TypeOf(string name) => Shape(name).GetProperty("type").GetString()!;

    private static string ShapeOf(JsonElement member) => member.GetProperty("shape").GetString()!;

    private static string? InputShape(JsonElement operation) =>
        operation.TryGetProperty("input", out var input) ? input.GetProperty("shape").GetString() : null;

    private static string? OutputShape(JsonElement operation) =>
        operation.TryGetProperty("output", out var output) ? output.GetProperty("shape").GetString() : null;

    private static IEnumerable<(string Name, JsonElement Member)> Members(JsonElement structure) =>
        structure.TryGetProperty("members", out var members)
            ? members.EnumerateObject().Select(m => (m.Name, m.Value))
            : [];

    private static HashSet<string> Required(JsonElement structure) =>
        structure.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private bool IsEventStream(string shapeName) =>
        TypeOf(shapeName) == "structure" && Flag(Shape(shapeName), "eventstream");

    private void Collect(string name, SortedSet<string> into)
    {
        var shape = Shape(name);
        switch (TypeOf(name))
        {
            case "structure":
                if (Flag(shape, "exception") || Flag(shape, "eventstream") || !into.Add(name))
                {
                    return;
                }

                foreach (var (_, member) in Members(shape))
                {
                    Collect(ShapeOf(member), into);
                }

                break;
            case "list":
                Collect(ShapeOf(shape.GetProperty("member")), into);
                break;
            case "map":
                Collect(ShapeOf(shape.GetProperty("key")), into);
                Collect(ShapeOf(shape.GetProperty("value")), into);
                break;
        }
    }

    private static string ClassName(string shapeName) =>
        RenamedClasses.TryGetValue(shapeName, out var renamed) ? renamed : shapeName;

    private static string PropertyName(string className, string memberName)
    {
        var name = char.ToUpperInvariant(memberName[0]) + memberName[1..];
        return string.Equals(name, className, StringComparison.Ordinal) ? name + "Value" : name;
    }

    /// <summary>The C# type of a member of the shape (always nullable).</summary>
    private string CsType(string shapeName, bool streaming = false)
    {
        var shape = Shape(shapeName);
        return TypeOf(shapeName) switch
        {
            "string" => "string",
            "integer" => "int?",
            "long" => "long?",
            "boolean" => "bool?",
            "double" => "double?",
            "float" => "float?",
            "timestamp" => "DateTime?",
            "blob" => streaming ? "ReadOnlyMemory<byte>?" : "byte[]",
            "list" => $"List<{ElementType(ShapeOf(shape.GetProperty("member")))}>",
            "map" => $"Dictionary<{ElementType(ShapeOf(shape.GetProperty("key")))}, {ElementType(ShapeOf(shape.GetProperty("value")))}>",
            "structure" => $"global::{Namespace}.{ClassName(shapeName)}",
            var other => throw new NotSupportedException($"Shape type {other} ({shapeName})"),
        };
    }

    /// <summary>The C# type of a list item or map value (not nullable).</summary>
    private string ElementType(string shapeName) => CsType(shapeName).TrimEnd('?');

    private static string Literal(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => c.ToString(),
            });
        }

        return text.Append('"').ToString();
    }

    private static string Header()
    {
        return "// <auto-generated/>\n// Generated by tools/GenerateHandlers (RestXmlCodeGenerator) from the S3 service model.\n#nullable enable\n";
    }

    // ------------------------------------------------------------------------------------------
    // Models
    // ------------------------------------------------------------------------------------------

    public string GenerateModels()
    {
        var code = new StringBuilder(Header());
        code.AppendLine();
        code.AppendLine($"namespace {Namespace};");
        code.AppendLine();

        foreach (var shapeName in _structures)
        {
            var className = ClassName(shapeName);
            code.AppendLine($"internal sealed partial class {className}");
            code.AppendLine("{");
            foreach (var (name, member) in Members(Shape(shapeName)))
            {
                if (IsEventStream(ShapeOf(member)))
                {
                    // SelectObjectContent's event stream: answered NotImplemented.
                    continue;
                }

                var type = CsType(ShapeOf(member), Flag(member, "streaming"));
                if (!type.EndsWith('?'))
                {
                    type += "?";
                }

                code.AppendLine($"    public {type} {PropertyName(className, name)} {{ get; set; }}");
            }

            code.AppendLine("}");
            code.AppendLine();
        }

        // Operations without an output answer an empty one.
        foreach (var (name, operation) in _operations)
        {
            if (OutputShape(operation) is null)
            {
                code.AppendLine($"internal sealed class {name}Output {{ }}");
                code.AppendLine();
            }
        }

        return code.ToString();
    }

    // ------------------------------------------------------------------------------------------
    // XML serializers
    // ------------------------------------------------------------------------------------------

    public string GenerateSerializers()
    {
        var code = new StringBuilder(Header());
        code.AppendLine();
        code.AppendLine("using System.Xml.Linq;");
        code.AppendLine($"using {Namespace};");
        code.AppendLine();
        code.AppendLine("namespace InMemoryAws.Http.Handlers;");
        code.AppendLine();
        code.AppendLine("internal static partial class S3XmlSerializers");
        code.AppendLine("{");

        foreach (var shapeName in _readable.Where(s => TypeOf(s) == "structure"))
        {
            GenerateReader(code, shapeName);
        }

        foreach (var shapeName in _writable.Where(s => TypeOf(s) == "structure"))
        {
            GenerateWriter(code, shapeName);
        }

        code.AppendLine("}");
        return code.ToString();
    }

    /// <summary>The element name of a member: its <c>locationName</c>, else its name.</summary>
    private static string ElementName(string memberName, JsonElement member) => Str(member, "locationName") ?? memberName;

    private bool IsFlattened(JsonElement member) =>
        Flag(member, "flattened") || Flag(Shape(ShapeOf(member)), "flattened");

    /// <summary>The element name of a non-flattened list's items.</summary>
    private string ItemName(string listShape) =>
        Str(Shape(listShape).GetProperty("member"), "locationName") ?? "member";

    private void GenerateReader(StringBuilder code, string shapeName)
    {
        var className = ClassName(shapeName);
        var shape = Shape(shapeName);
        code.AppendLine($"    internal static {className} Read_{shapeName}(XElement element)");
        code.AppendLine("    {");
        code.AppendLine($"        var value = new {className}();");

        var attributes = Members(shape).Where(m => Flag(m.Member, "xmlAttribute")).ToList();
        foreach (var (name, member) in attributes)
        {
            var local = ElementName(name, member);
            local = local.Contains(':', StringComparison.Ordinal) ? local[(local.IndexOf(':', StringComparison.Ordinal) + 1)..] : local;
            code.AppendLine($"        value.{PropertyName(className, name)} = S3Xml.Attribute(element, {Literal(local)});");
        }

        code.AppendLine("        foreach (var child in element.Elements())");
        code.AppendLine("        {");
        code.AppendLine("            switch (child.Name.LocalName)");
        code.AppendLine("            {");
        var elementMembers = Members(shape).Where(m => !Flag(m.Member, "xmlAttribute")).ToList();
        foreach (var (name, member) in elementMembers)
        {
            if (Flag(member, "deprecated")
                && elementMembers.Any(other => other.Name != name && ElementName(other.Name, other.Member) == ElementName(name, member)))
            {
                // A deprecated member that shares its element name with its replacement: read as the replacement.
                continue;
            }

            var memberShape = ShapeOf(member);
            var property = $"value.{PropertyName(className, name)}";
            if (TypeOf(memberShape) == "list")
            {
                var itemShape = ShapeOf(Shape(memberShape).GetProperty("member"));
                if (IsFlattened(member))
                {
                    code.AppendLine($"                case {Literal(ElementName(name, member))}:");
                    code.AppendLine($"                    ({property} ??= []).Add({ReadScalarOrStructure(itemShape, "child")});");
                    code.AppendLine("                    break;");
                }
                else
                {
                    code.AppendLine($"                case {Literal(ElementName(name, member))}:");
                    code.AppendLine($"                    {property} ??= [];");
                    code.AppendLine("                    foreach (var item in child.Elements())");
                    code.AppendLine("                    {");
                    code.AppendLine($"                        S3Xml.ExpectName(item, {Literal(ItemName(memberShape))});");
                    code.AppendLine($"                        {property}.Add({ReadScalarOrStructure(itemShape, "item")});");
                    code.AppendLine("                    }");
                    code.AppendLine();
                    code.AppendLine("                    break;");
                }
            }
            else if (TypeOf(memberShape) == "map")
            {
                throw new NotSupportedException($"A map in an XML body ({shapeName}.{name})");
            }
            else
            {
                code.AppendLine($"                case {Literal(ElementName(name, member))}:");
                code.AppendLine($"                    {property} = {ReadScalarOrStructure(memberShape, "child")};");
                code.AppendLine("                    break;");
            }
        }

        code.AppendLine("                default:");
        code.AppendLine("                    throw S3Xml.UnexpectedElement(child);");
        code.AppendLine("            }");
        code.AppendLine("        }");
        code.AppendLine();
        code.AppendLine("        return value;");
        code.AppendLine("    }");
        code.AppendLine();
    }

    private string ReadScalarOrStructure(string shapeName, string element) => TypeOf(shapeName) switch
    {
        "structure" => $"Read_{shapeName}({element})",
        "string" => $"S3Xml.String({element})",
        "integer" => $"S3Xml.Int({element})",
        "long" => $"S3Xml.Long({element})",
        "boolean" => $"S3Xml.Bool({element})",
        "double" or "float" => $"S3Xml.Double({element})",
        "timestamp" => $"S3Xml.Timestamp({element})",
        "blob" => $"S3Xml.Blob({element})",
        var other => throw new NotSupportedException($"{other} in an XML list ({shapeName})"),
    };

    private void GenerateWriter(StringBuilder code, string shapeName)
    {
        var className = ClassName(shapeName);
        var shape = Shape(shapeName);
        code.AppendLine($"    internal static void Write_{shapeName}(S3XmlWriter writer, string name, {className} value)");
        code.AppendLine("    {");
        code.AppendLine("        writer.StartElement(name);");
        if (shape.TryGetProperty("xmlNamespace", out var ns) && Str(ns, "prefix") is { } prefix)
        {
            code.AppendLine($"        writer.Attribute({Literal("xmlns:" + prefix)}, {Literal(Str(ns, "uri")!)});");
        }

        foreach (var (name, member) in Members(shape).Where(m => Flag(m.Member, "xmlAttribute")))
        {
            var property = $"value.{PropertyName(className, name)}";
            code.AppendLine($"        if ({property} is not null)");
            code.AppendLine("        {");
            code.AppendLine($"            writer.Attribute({Literal(ElementName(name, member))}, {property});");
            code.AppendLine("        }");
            code.AppendLine();
        }

        foreach (var (name, member) in Members(shape).Where(m => !Flag(m.Member, "xmlAttribute") && !m.Member.TryGetProperty("location", out _)))
        {
            WriteMember(code, name, member, $"value.{PropertyName(className, name)}", "        ");
        }

        code.AppendLine("        writer.EndElement();");
        code.AppendLine("    }");
        code.AppendLine();
    }

    /// <summary>Writes one body member of a structure, when it is set.</summary>
    private void WriteMember(StringBuilder code, string name, JsonElement member, string property, string indent, string prefix = "")
    {
        var memberShape = ShapeOf(member);
        var element = Literal(ElementName(name, member));
        code.AppendLine($"{indent}if ({property} is not null)");
        code.AppendLine($"{indent}{{");
        if (TypeOf(memberShape) == "list")
        {
            var itemShape = ShapeOf(Shape(memberShape).GetProperty("member"));
            if (IsFlattened(member))
            {
                code.AppendLine($"{indent}    foreach (var item in {property})");
                code.AppendLine($"{indent}    {{");
                code.AppendLine($"{indent}        {WriteScalarOrStructure(itemShape, element, "item", prefix)}");
                code.AppendLine($"{indent}    }}");
            }
            else
            {
                code.AppendLine($"{indent}    writer.StartElement({element});");
                code.AppendLine($"{indent}    foreach (var item in {property})");
                code.AppendLine($"{indent}    {{");
                code.AppendLine($"{indent}        {WriteScalarOrStructure(itemShape, Literal(ItemName(memberShape)), "item", prefix)}");
                code.AppendLine($"{indent}    }}");
                code.AppendLine();
                code.AppendLine($"{indent}    writer.EndElement();");
            }
        }
        else
        {
            var value = TypeOf(memberShape) is "structure" or "string" or "blob" ? property : $"{property}.Value";
            code.AppendLine($"{indent}    {WriteScalarOrStructure(memberShape, element, value, prefix)}");
        }

        code.AppendLine($"{indent}}}");
        code.AppendLine();
    }

    private string WriteScalarOrStructure(string shapeName, string element, string value, string prefix = "") => TypeOf(shapeName) switch
    {
        "structure" => $"{prefix}Write_{shapeName}(writer, {element}, {value});",
        "string" => $"writer.Element({element}, {value});",
        "integer" or "long" or "double" or "float" => $"writer.Element({element}, S3Wire.FormatNumber({value}));",
        "boolean" => $"writer.Element({element}, {value} ? \"true\" : \"false\");",
        "timestamp" => $"writer.Element({element}, S3Wire.FormatTimestamp({value}, {Literal(Str(Shape(shapeName), "timestampFormat") ?? "iso8601")}));",
        "blob" => $"writer.Element({element}, Convert.ToBase64String({value}));",
        var other => throw new NotSupportedException($"{other} in XML ({shapeName})"),
    };

    // ------------------------------------------------------------------------------------------
    // Client base
    // ------------------------------------------------------------------------------------------

    public string GenerateClientBase()
    {
        var code = new StringBuilder(Header());
        code.AppendLine();
        code.AppendLine($"using {Namespace};");
        code.AppendLine();
        code.AppendLine("namespace InMemoryAws.S3;");
        code.AppendLine();
        code.AppendLine("/// <summary>");
        code.AppendLine("/// Every S3 operation, each answering <c>NotImplemented</c> until <see cref=\"InternalS3Client\"/>");
        code.AppendLine("/// overrides it.");
        code.AppendLine("/// </summary>");
        code.AppendLine("internal abstract class S3ClientBase");
        code.AppendLine("{");
        foreach (var (name, operation) in _operations)
        {
            var input = InputShape(operation)!;
            var output = OutputShape(operation) is { } o ? ClassName(o) : $"{name}Output";
            code.AppendLine($"    public virtual Task<{output}> {name}Async({ClassName(input)} request, CancellationToken cancellationToken) =>");
            code.AppendLine($"        throw S3Errors.NotImplemented({Literal(name)});");
            code.AppendLine();
        }

        code.AppendLine("}");
        return code.ToString();
    }

    // ------------------------------------------------------------------------------------------
    // Router and dispatcher
    // ------------------------------------------------------------------------------------------

    private sealed record Route(
        string Operation,
        string Method,
        string Path,
        List<(string Name, string? Value)> Literals,
        List<string> RequiredQuery,
        List<string> RequiredHeaders);

    private List<Route> Routes()
    {
        var routes = new List<Route>();
        foreach (var (name, operation) in _operations)
        {
            if (Unrouted.Contains(name))
            {
                continue;
            }

            var http = operation.GetProperty("http");
            var uri = http.GetProperty("requestUri").GetString()!;
            var question = uri.IndexOf('?', StringComparison.Ordinal);
            var path = question < 0 ? uri : uri[..question];
            var literals = new List<(string, string?)>();
            if (question >= 0)
            {
                foreach (var part in uri[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=', StringComparison.Ordinal);
                    literals.Add(eq < 0 ? (part, null) : (part[..eq], part[(eq + 1)..]));
                }
            }

            var kind = path switch
            {
                "/" => "Service",
                "/{Bucket}" => "Bucket",
                "/{Bucket}/{Key+}" => "Object",
                _ => throw new NotSupportedException($"Path {path} ({name})"),
            };

            var requiredQuery = new List<string>();
            var requiredHeaders = new List<string>();
            if (InputShape(operation) is { } input)
            {
                var shape = Shape(input);
                var required = Required(shape);
                foreach (var (memberName, member) in Members(shape))
                {
                    if (!required.Contains(memberName))
                    {
                        continue;
                    }

                    switch (Str(member, "location"))
                    {
                        case "querystring":
                            requiredQuery.Add(Str(member, "locationName")!);
                            break;
                        case "header":
                            requiredHeaders.Add(Str(member, "locationName")!);
                            break;
                    }
                }
            }

            routes.Add(new Route(name, http.GetProperty("method").GetString()!, kind, literals, requiredQuery, requiredHeaders));
        }

        return routes;
    }

    public string GenerateWireProtocol()
    {
        var code = new StringBuilder(Header());
        code.AppendLine();
        code.AppendLine("using InMemoryAws.Http.Handlers;");
        code.AppendLine("using InMemoryAws.S3;");
        code.AppendLine($"using {Namespace};");
        code.AppendLine();
        code.AppendLine("namespace InMemoryAws.Http;");
        code.AppendLine();
        code.AppendLine("internal static partial class S3WireProtocol");
        code.AppendLine("{");
        GenerateRouter(code);
        GenerateDispatcher(code);
        code.AppendLine("}");
        return code.ToString();
    }

    /// <summary>
    /// The router: among the operations for the request's method and path, the one that matches
    /// the most query markers (<c>?tagging</c>, <c>?list-type=2</c>); among those, the one with the
    /// most required query parameters and headers present (UploadPart's <c>partNumber</c> and
    /// <c>uploadId</c>, CopyObject's <c>x-amz-copy-source</c>); among those, the one missing the fewest
    /// (so <c>GET ?inventory</c> without an <c>id</c> lists rather than gets). An operation with no markers is
    /// only chosen when all its required query parameters and headers are present.
    /// </summary>
    private void GenerateRouter(StringBuilder code)
    {
        code.AppendLine("    /// <summary>The operation a request is for, by method, path and query; null when none matches.</summary>");
        code.AppendLine("    internal static string? Route(S3WireRequest request)");
        code.AppendLine("    {");
        code.AppendLine("        string? best = null;");
        code.AppendLine("        var bestLiterals = -1;");
        code.AppendLine("        var bestRequired = -1;");
        code.AppendLine("        var bestMissing = int.MaxValue;");
        code.AppendLine("        int literals, required, missing;");
        foreach (var group in Routes().GroupBy(r => (r.Method, r.Path)))
        {
            code.AppendLine($"        if (request.Method == {Literal(group.Key.Method)} && request.PathKind == S3PathKind.{group.Key.Path})");
            code.AppendLine("        {");
            foreach (var route in group)
            {
                var literalConditions = route.Literals
                    .Select(l => l.Value is null ? $"request.HasQuery({Literal(l.Name)})" : $"request.Query({Literal(l.Name)}) == {Literal(l.Value)}")
                    .ToList();
                var requiredConditions = route.RequiredQuery.Select(q => $"request.HasQuery({Literal(q)})")
                    .Concat(route.RequiredHeaders.Select(h => $"request.Header({Literal(h)}) is not null"))
                    .ToList();
                code.AppendLine($"            // {route.Operation}");
                code.AppendLine($"            literals = {route.Literals.Count};");
                code.AppendLine($"            required = {(requiredConditions.Count == 0 ? "0" : string.Join(" + ", requiredConditions.Select(c => $"({c} ? 1 : 0)")))};");
                code.AppendLine($"            missing = {requiredConditions.Count} - required;");
                var eligible = new List<string>();
                if (literalConditions.Count > 0)
                {
                    eligible.Add(string.Join(" && ", literalConditions));
                }
                else if (requiredConditions.Count > 0)
                {
                    eligible.Add($"required == {requiredConditions.Count}");
                }

                eligible.Add("(literals > bestLiterals || (literals == bestLiterals && (required > bestRequired || (required == bestRequired && missing < bestMissing))))");
                code.AppendLine($"            if ({string.Join(" && ", eligible)})");
                code.AppendLine("            {");
                code.AppendLine($"                best = {Literal(route.Operation)};");
                code.AppendLine("                bestLiterals = literals;");
                code.AppendLine("                bestRequired = required;");
                code.AppendLine("                bestMissing = missing;");
                code.AppendLine("            }");
                code.AppendLine();
            }

            code.AppendLine("        }");
            code.AppendLine();
        }

        code.AppendLine("        return best;");
        code.AppendLine("    }");
        code.AppendLine();
    }

    private void GenerateDispatcher(StringBuilder code)
    {
        code.AppendLine("    /// <summary>Whether the model has an operation of this name.</summary>");
        code.AppendLine("    internal static bool IsOperation(string operation) => operation switch");
        code.AppendLine("    {");
        var names = _operations.Select(o => o.Name).ToList();
        for (var i = 0; i < names.Count; i += 4)
        {
            var chunk = string.Join(" or ", names.Skip(i).Take(4).Select(Literal));
            code.AppendLine(i == 0 ? $"        {chunk}" : $"            or {chunk}");
        }

        code.AppendLine("            => true,");
        code.AppendLine("        _ => false,");
        code.AppendLine("    };");
        code.AppendLine();
        code.AppendLine("    /// <summary>");
        code.AppendLine("    /// Binds the request's members from its path, query, headers and body, runs the operation on");
        code.AppendLine("    /// <paramref name=\"client\"/>, and writes the output's members to the response's headers and body.");
        code.AppendLine("    /// </summary>");
        code.AppendLine("    private static async ValueTask DispatchAsync(string operation, S3WireRequest request, S3ClientBase client, S3WireResponse response, CancellationToken cancellationToken)");
        code.AppendLine("    {");
        code.AppendLine("        switch (operation)");
        code.AppendLine("        {");
        foreach (var (name, operation) in _operations)
        {
            code.AppendLine($"            case {Literal(name)}:");
            code.AppendLine("            {");
            var input = InputShape(operation)!;
            var output = OutputShape(operation);
            if (output is not null && IsEventStreamOutput(output))
            {
                code.AppendLine($"                throw S3Errors.NotImplemented({Literal(name)});");
                code.AppendLine("            }");
                continue;
            }

            GenerateBinding(code, input);
            code.AppendLine($"                var output = await client.{name}Async(input, cancellationToken).ConfigureAwait(false);");
            var status = operation.GetProperty("http").TryGetProperty("responseCode", out var responseCode) ? responseCode.GetInt32() : 200;
            code.AppendLine($"                response.Status = {status.ToString(CultureInfo.InvariantCulture)};");
            if (output is not null)
            {
                GenerateOutput(code, output);
            }
            else
            {
                code.AppendLine("                _ = output;");
            }

            code.AppendLine("                return;");
            code.AppendLine("            }");
        }

        code.AppendLine("            default:");
        code.AppendLine("                throw S3Errors.NotImplemented(operation);");
        code.AppendLine("        }");
        code.AppendLine("    }");
    }

    private bool IsEventStreamOutput(string output) =>
        Members(Shape(output)).Any(m => IsEventStream(ShapeOf(m.Member)));

    private void GenerateBinding(StringBuilder code, string inputShape)
    {
        var className = ClassName(inputShape);
        var shape = Shape(inputShape);
        code.AppendLine($"                var input = new {className}();");
        var payload = Str(shape, "payload");
        var bodyMembers = new List<string>();
        foreach (var (name, member) in Members(shape))
        {
            var property = $"input.{PropertyName(className, name)}";
            var memberShape = ShapeOf(member);
            var location = Str(member, "location");
            var locationName = Str(member, "locationName") ?? name;
            switch (location)
            {
                case "uri":
                    code.AppendLine(locationName switch
                    {
                        "Bucket" => $"                {property} = request.Bucket;",
                        "Key" => $"                {property} = request.Key;",
                        _ => throw new NotSupportedException($"URI label {locationName}"),
                    });
                    break;
                case "querystring":
                    code.AppendLine($"                {property} = {ParseExpression(memberShape, $"request.Query({Literal(locationName)})", Literal(locationName), "iso8601")};");
                    break;
                case "header":
                    code.AppendLine($"                {property} = {ParseExpression(memberShape, $"request.Header({Literal(locationName)})", Literal(locationName), "rfc822")};");
                    break;
                case "headers":
                    code.AppendLine($"                {property} = request.HeadersWithPrefix({Literal(locationName)});");
                    break;
                case null when name == payload:
                    switch (TypeOf(memberShape))
                    {
                        case "blob":
                            code.AppendLine($"                {property} = request.Body;");
                            break;
                        case "string":
                            code.AppendLine($"                {property} = request.BodyText();");
                            break;
                        case "structure":
                            code.AppendLine($"                if (request.LoadXml() is {{ }} root)");
                            code.AppendLine("                {");
                            code.AppendLine($"                    {property} = S3XmlSerializers.Read_{memberShape}(root);");
                            code.AppendLine("                }");
                            break;
                        default:
                            throw new NotSupportedException($"Payload of type {TypeOf(memberShape)} ({inputShape})");
                    }

                    break;
                case null:
                    bodyMembers.Add(name);
                    break;
                default:
                    throw new NotSupportedException($"Location {location} ({inputShape}.{name})");
            }
        }

        if (bodyMembers.Count > 0)
        {
            // Only SelectObjectContent, whose eventstream output is answered NotImplemented.
            code.AppendLine($"                // Body members ({string.Join(", ", bodyMembers)}) are not bound.");
        }
    }

    private string ParseExpression(string shapeName, string text, string name, string defaultTimestampFormat) => TypeOf(shapeName) switch
    {
        "string" => text,
        "integer" => $"S3Wire.ParseInt({text}, {name})",
        "long" => $"S3Wire.ParseLong({text}, {name})",
        "boolean" => $"S3Wire.ParseBool({text}, {name})",
        "timestamp" => $"S3Wire.ParseTimestamp({text}, {Literal(Str(Shape(shapeName), "timestampFormat") ?? defaultTimestampFormat)})",
        "list" => $"S3Wire.ParseList({text})",
        var other => throw new NotSupportedException($"{other} in a header or query ({shapeName})"),
    };

    private void GenerateOutput(StringBuilder code, string outputShape)
    {
        var className = ClassName(outputShape);
        var shape = Shape(outputShape);
        var payload = Str(shape, "payload");
        var bodyMembers = new List<(string Name, JsonElement Member)>();
        foreach (var (name, member) in Members(shape))
        {
            var property = $"output.{PropertyName(className, name)}";
            var memberShape = ShapeOf(member);
            var location = Str(member, "location");
            var locationName = Str(member, "locationName") ?? name;
            switch (location)
            {
                case "header":
                    code.AppendLine($"                if ({property} is not null)");
                    code.AppendLine("                {");
                    code.AppendLine($"                    response.SetHeader({Literal(locationName)}, {FormatExpression(memberShape, property)});");
                    code.AppendLine("                }");
                    code.AppendLine();
                    break;
                case "headers":
                    code.AppendLine($"                response.SetHeaders({Literal(locationName)}, {property});");
                    break;
                case "statusCode":
                    code.AppendLine($"                response.Status = {property} ?? response.Status;");
                    break;
                case null when name == payload:
                    switch (TypeOf(memberShape))
                    {
                        case "blob":
                            code.AppendLine($"                response.Payload = {property};");
                            break;
                        case "string":
                            code.AppendLine($"                response.SetText({property});");
                            break;
                        case "structure":
                            code.AppendLine($"                if ({property} is not null)");
                            code.AppendLine("                {");
                            code.AppendLine($"                    S3XmlSerializers.Write_{memberShape}(response.StartXml(), {Literal(Str(member, "locationName") ?? memberShape)}, {property});");
                            code.AppendLine("                }");
                            break;
                        default:
                            throw new NotSupportedException($"Payload of type {TypeOf(memberShape)} ({outputShape})");
                    }

                    break;
                case null:
                    bodyMembers.Add((name, member));
                    break;
                default:
                    throw new NotSupportedException($"Location {location} ({outputShape}.{name})");
            }
        }

        if (bodyMembers.Count == 0)
        {
            return;
        }

        var root = OutputRoots.TryGetValue(outputShape, out var known) ? known : outputShape;
        if (outputShape == "GetBucketLocationOutput")
        {
            // The location is the root element's text: <LocationConstraint>eu-west-1</LocationConstraint>.
            code.AppendLine("                response.StartXml().Element(\"LocationConstraint\", output.LocationConstraint ?? string.Empty);");
            return;
        }

        code.AppendLine("                {");
        code.AppendLine("                    var writer = response.StartXml();");
        code.AppendLine($"                    writer.StartElement({Literal(root)});");
        foreach (var (name, member) in bodyMembers)
        {
            WriteMember(code, name, member, $"output.{PropertyName(className, name)}", "                    ", "S3XmlSerializers.");
        }

        code.AppendLine("                    writer.EndElement();");
        code.AppendLine("                }");
    }

    private string FormatExpression(string shapeName, string value) => TypeOf(shapeName) switch
    {
        "string" => value,
        "integer" or "long" or "double" or "float" => $"S3Wire.FormatNumber({value}.Value)",
        "boolean" => $"({value}.Value ? \"true\" : \"false\")",
        "timestamp" => $"S3Wire.FormatTimestamp({value}.Value, {Literal(Str(Shape(shapeName), "timestampFormat") ?? "rfc822")})",
        "list" => $"string.Join(\",\", {value})",
        var other => throw new NotSupportedException($"{other} in a header ({shapeName})"),
    };
}
