using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GenerateHandlers;

/// <summary>
/// A JSON-protocol generator driven by the model's shapes, for services whose shapes recurse or
/// nest collections (DynamoDB's <c>AttributeValue</c> holds maps and lists of itself, and its batch
/// operations take maps of lists of maps). Unlike <see cref="JsonCodeGenerator"/>, it writes one
/// method per shape rather than inlining nested shapes, so it never recurses forever:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>{Service}Models.g.cs</c>: a class per structure. Collections and value-type members
/// are <see langword="null"/> when absent, so an empty list (<c>"L": []</c>) stays distinct from a
/// missing one, and a response writes exactly the members its handler set.</item>
/// <item><c>{Service}JsonSerializers.g.cs</c>: <c>Read_{Shape}</c>/<c>Write_{Shape}</c> per
/// structure, list and map, and <c>Validate_{Shape}</c>: the model's constraints (required, length,
/// range, pattern, enum), reported with AWS's <c>1 validation error detected: ...</c> clauses.</item>
/// <item><c>{Service}OperationDispatcher.g.cs</c>: one transport-neutral switch on the operation name
/// that deserializes, validates, calls the internal client and serializes.</item>
/// </list>
/// Shapes in <c>customShapes</c> map to a hand-written type with its own reader and writer.
/// </remarks>
internal sealed class ShapeJsonCodeGenerator
{
    private readonly string _serviceName;
    private readonly string _modelNamespace;
    private readonly string _clientType;
    private readonly JsonElement _shapes;
    private readonly JsonElement _operations;
    private readonly IReadOnlyDictionary<string, CustomShape> _customShapes;
    private readonly string? _requestChecks;
    private readonly IReadOnlyDictionary<string, string> _unmodeledMembers;
    private readonly Dictionary<string, string> _classNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _errorShapes = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _reachable = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _requestReachable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _patternFields = new(StringComparer.Ordinal);

    /// <summary>
    /// Shapes the service checks itself, with its own sentence instead of the model's clause
    /// (DynamoDB refuses an unknown <c>TableClass</c> as "Invalid table-class parameter provided").
    /// </summary>
    private static readonly HashSet<string> ServiceCheckedShapes = new(StringComparer.Ordinal) { "TableClass" };

    /// <summary>A shape served by a hand-written type.</summary>
    /// <param name="TypeName">The C# type.</param>
    /// <param name="Read">A method taking a <c>JsonElement</c> and returning the type.</param>
    /// <param name="Write">A method taking a <c>Utf8JsonWriter</c> and the value.</param>
    /// <param name="Render">A method rendering the value for a validation message, Java-style.</param>
    public sealed record CustomShape(string TypeName, string Read, string Write, string Render);

    /// <param name="requestChecks">
    /// A hand-written type whose <c>Check(request, violations)</c> overloads run after the model's
    /// constraints are checked, for the refusals the service words its own way; null for none.
    /// </param>
    /// <param name="unmodeledMembers">
    /// Per structure, the members the service's own objects carry beyond the model's, as their
    /// rendering in a validation message ends with them (<c>workloadProfileName=null</c>).
    /// </param>
    public ShapeJsonCodeGenerator(
        string serviceName,
        string modelNamespace,
        string clientType,
        JsonElement shapes,
        JsonElement operations,
        IReadOnlyDictionary<string, CustomShape> customShapes,
        string? requestChecks = null,
        IReadOnlyDictionary<string, string>? unmodeledMembers = null)
    {
        _requestChecks = requestChecks;
        _unmodeledMembers = unmodeledMembers ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _serviceName = serviceName;
        _modelNamespace = modelNamespace;
        _clientType = clientType;
        _shapes = shapes;
        _operations = operations;
        _customShapes = customShapes;

        foreach (var shape in shapes.EnumerateObject())
        {
            if (shape.Value.TryGetProperty("exception", out var exception) && exception.GetBoolean())
            {
                _errorShapes.Add(shape.Name);
            }
        }

        foreach (var operation in operations.EnumerateObject())
        {
            if (operation.Value.TryGetProperty("input", out var input))
            {
                var name = input.GetProperty("shape").GetString()!;
                // A shape two operations share keeps its own name (KinesisStreamingDestinationInput).
                _classNames[name] = _classNames.ContainsKey(name) ? name : $"{operation.Name}Request";
                Collect(name, _reachable);
                Collect(name, _requestReachable);
            }

            if (operation.Value.TryGetProperty("output", out var output))
            {
                var name = output.GetProperty("shape").GetString()!;
                _classNames[name] = _classNames.ContainsKey(name) ? name : $"{operation.Name}Response";
                Collect(name, _reachable);
            }
        }
    }

    private IEnumerable<(string Name, JsonElement Operation)> Operations() =>
        _operations.EnumerateObject()
            .Where(o => o.Value.TryGetProperty("input", out _))
            .Select(o => (o.Name, o.Value));

    private JsonElement Shape(string name) => _shapes.GetProperty(name);

    private string TypeOf(string shapeName) => Shape(shapeName).GetProperty("type").GetString()!;

    private void Collect(string shapeName, SortedSet<string> into)
    {
        if (_customShapes.ContainsKey(shapeName) || _errorShapes.Contains(shapeName))
        {
            return;
        }

        var shape = Shape(shapeName);
        switch (TypeOf(shapeName))
        {
            case "structure":
                if (!into.Add(shapeName))
                {
                    return;
                }

                foreach (var member in Members(shape))
                {
                    Collect(member.Shape, into);
                }

                break;
            case "list":
                if (into.Add(shapeName))
                {
                    Collect(ListMember(shape), into);
                }

                break;
            case "map":
                if (into.Add(shapeName))
                {
                    Collect(MapValue(shape), into);
                }

                break;
        }
    }

    private static IEnumerable<(string Name, string Shape)> Members(JsonElement structure) =>
        structure.TryGetProperty("members", out var members)
            ? members.EnumerateObject().Select(m => (m.Name, m.Value.GetProperty("shape").GetString()!))
            : [];

    private static HashSet<string> Required(JsonElement structure) =>
        structure.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

    private static string ListMember(JsonElement list) => list.GetProperty("member").GetProperty("shape").GetString()!;

    private static string MapKey(JsonElement map) => map.GetProperty("key").GetProperty("shape").GetString()!;

    private static string MapValue(JsonElement map) => map.GetProperty("value").GetProperty("shape").GetString()!;

    private string ClassName(string shapeName) =>
        _customShapes.TryGetValue(shapeName, out var custom) ? custom.TypeName
        : _classNames.TryGetValue(shapeName, out var name) ? name
        : shapeName;

    /// <summary>The C# type a value of the shape has (non-nullable).</summary>
    private string CsType(string shapeName)
    {
        if (_customShapes.TryGetValue(shapeName, out var custom))
        {
            return custom.TypeName;
        }

        var shape = Shape(shapeName);
        return TypeOf(shapeName) switch
        {
            "string" => "string",
            "integer" => "int",
            "long" => "long",
            "boolean" => "bool",
            "double" => "double",
            "float" => "float",
            "timestamp" => "DateTime",
            "blob" => "byte[]",
            "list" => $"List<{CsType(ListMember(shape))}>",
            "map" => $"Dictionary<{CsType(MapKey(shape))}, {CsType(MapValue(shape))}>",
            "structure" => $"global::{_modelNamespace}.{ClassName(shapeName)}",
            var other => throw new NotSupportedException($"Shape type {other} ({shapeName})"),
        };
    }

    private bool IsValueType(string shapeName) =>
        !_customShapes.ContainsKey(shapeName)
        && TypeOf(shapeName) is "integer" or "long" or "boolean" or "double" or "float" or "timestamp";

    private static string PropertyName(string className, string memberName)
    {
        var name = char.ToUpperInvariant(memberName[0]) + memberName[1..];
        return string.Equals(name, className, StringComparison.Ordinal) ? name + "Value" : name;
    }

    private static string LowerCamel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    // ------------------------------------------------------------------------------------------
    // Models
    // ------------------------------------------------------------------------------------------

    public string GenerateModels()
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine($"// Generated internal types for {_serviceName} from the AWS service model");
        code.AppendLine("#nullable enable");
        code.AppendLine();
        code.AppendLine($"namespace {_modelNamespace};");
        code.AppendLine();

        foreach (var shapeName in _reachable.Where(s => TypeOf(s) == "structure").OrderBy(ClassName, StringComparer.Ordinal))
        {
            var className = ClassName(shapeName);
            var shape = Shape(shapeName);
            code.AppendLine($"internal sealed partial class {className}");
            code.AppendLine("{");
            foreach (var (name, memberShape) in Members(shape))
            {
                code.AppendLine($"    public {CsType(memberShape)}? {PropertyName(className, name)} {{ get; set; }}");
            }

            code.AppendLine();
            code.AppendLine("    /// <summary>The value as AWS's Java services render it in a validation message.</summary>");
            code.AppendLine("    public override string ToString()");
            code.AppendLine("    {");
            code.AppendLine("        var text = new System.Text.StringBuilder();");
            code.AppendLine($"        text.Append(\"{className}(\");");
            var first = true;
            foreach (var (name, memberShape) in Members(shape))
            {
                var separator = first ? "" : ", ";
                first = false;
                code.AppendLine($"        text.Append(\"{separator}{LowerCamel(name)}=\");");
                var property = PropertyName(className, name);
                code.AppendLine($"        text.Append({RenderExpression(memberShape, property, nullable: true)});");
            }

            if (_unmodeledMembers.TryGetValue(shapeName, out var unmodeled))
            {
                code.AppendLine($"        text.Append(\"{(first ? "" : ", ")}{unmodeled}\");");
            }

            code.AppendLine("        return text.Append(')').ToString();");
            code.AppendLine("    }");
            code.AppendLine("}");
            code.AppendLine();
        }

        foreach (var (opName, operation) in Operations())
        {
            if (!operation.TryGetProperty("output", out _))
            {
                code.AppendLine($"internal sealed class {opName}Response {{ }}");
                code.AppendLine();
            }
        }

        return code.ToString();
    }

    /// <summary>An expression rendering a value of the shape as Java's <c>toString</c> would.</summary>
    private string RenderExpression(string shapeName, string valueExpression, bool nullable)
    {
        var call = _customShapes.TryGetValue(shapeName, out var custom)
            ? $"{custom.Render}({valueExpression})"
            : TypeOf(shapeName) switch
            {
                "string" => valueExpression,
                "boolean" => $"JavaRendering.Boolean({valueExpression}{(nullable ? ".Value" : "")})",
                "integer" or "long" or "double" or "float" => $"JavaRendering.Number({valueExpression}{(nullable ? ".Value" : "")})",
                "timestamp" => $"JavaRendering.Timestamp({valueExpression}{(nullable ? ".Value" : "")})",
                "blob" => $"JavaRendering.Blob({valueExpression})",
                "list" => $"JavaRendering.List({valueExpression}, item => {RenderExpression(ListMember(Shape(shapeName)), "item", nullable: false)})",
                "map" => $"JavaRendering.Map({valueExpression}, item => {RenderExpression(MapValue(Shape(shapeName)), "item", nullable: false)})",
                _ => $"{valueExpression}.ToString()",
            };

        return nullable ? $"{valueExpression} is null ? \"null\" : {call}" : call;
    }

    // ------------------------------------------------------------------------------------------
    // Serializers and validation
    // ------------------------------------------------------------------------------------------

    public string GenerateSerializers()
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine($"// Generated JSON protocol serializers and validation for {_serviceName}");
        code.AppendLine("#nullable enable");
        code.AppendLine("#pragma warning disable CS0618 // Type or member is obsolete");
        code.AppendLine();
        code.AppendLine("using System.Text.Json;");
        code.AppendLine("using LocalSqsSnsMessaging.Http.Serialization;");
        code.AppendLine($"using {_modelNamespace};");
        code.AppendLine();
        code.AppendLine("namespace LocalSqsSnsMessaging.Http.Handlers;");
        code.AppendLine();
        code.AppendLine($"internal static partial class {_serviceName}JsonSerializers");
        code.AppendLine("{");

        foreach (var shapeName in _reachable)
        {
            GenerateReader(code, shapeName);
            GenerateWriter(code, shapeName);
        }

        foreach (var shapeName in _requestReachable.Where(s => TypeOf(s) == "structure"))
        {
            GenerateValidator(code, shapeName);
        }

        foreach (var (pattern, field) in _patternFields.OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            // Compiled on first use, each on its own, so one pattern can't stop the others loading.
            // Anchored with \z: AWS matches the whole value, and $ would also match before a final newline.
            var backing = "_" + char.ToLowerInvariant(field[0]) + field[1..];
            code.AppendLine($"    private static System.Text.RegularExpressions.Regex? {backing};");
            code.AppendLine($"    private static System.Text.RegularExpressions.Regex {field} => {backing} ??= new({Literal("^(?:" + DotNetPattern(pattern) + @")\z")}, System.Text.RegularExpressions.RegexOptions.CultureInvariant);");
        }

        code.AppendLine("}");
        return code.ToString();
    }

    private void GenerateReader(StringBuilder code, string shapeName)
    {
        var shape = Shape(shapeName);
        var type = CsType(shapeName);
        code.AppendLine($"    internal static {type} Read_{shapeName}(JsonElement element)");
        code.AppendLine("    {");
        switch (TypeOf(shapeName))
        {
            case "structure":
                var className = ClassName(shapeName);
                code.AppendLine($"        AwsJsonReader.ExpectObject(element, \"{className}\");");
                code.AppendLine($"        var value = new global::{_modelNamespace}.{className}();");
                if (!Members(shape).Any())
                {
                    code.AppendLine("        return value;");
                    break;
                }

                code.AppendLine("        foreach (var property in element.EnumerateObject())");
                code.AppendLine("        {");
                code.AppendLine("            if (property.Value.ValueKind == JsonValueKind.Null)");
                code.AppendLine("            {");
                code.AppendLine("                continue;");
                code.AppendLine("            }");
                code.AppendLine();
                code.AppendLine("            switch (property.Name)");
                code.AppendLine("            {");
                foreach (var (name, memberShape) in Members(shape))
                {
                    code.AppendLine($"                case \"{name}\":");
                    code.AppendLine($"                    value.{PropertyName(className, name)} = {ReadExpression(memberShape, "property.Value")};");
                    code.AppendLine("                    break;");
                }

                code.AppendLine("            }");
                code.AppendLine("        }");
                code.AppendLine();
                code.AppendLine("        return value;");
                break;
            case "list":
                code.AppendLine("        AwsJsonReader.ExpectArray(element);");
                code.AppendLine($"        var list = new {type}(element.GetArrayLength());");
                code.AppendLine("        foreach (var item in element.EnumerateArray())");
                code.AppendLine("        {");
                code.AppendLine($"            list.Add({ReadExpression(ListMember(shape), "item")});");
                code.AppendLine("        }");
                code.AppendLine();
                code.AppendLine("        return list;");
                break;
            case "map":
                code.AppendLine("        AwsJsonReader.ExpectObject(element, \"map\");");
                code.AppendLine($"        var map = new {type}(StringComparer.Ordinal);");
                code.AppendLine("        foreach (var property in element.EnumerateObject())");
                code.AppendLine("        {");
                code.AppendLine($"            map[property.Name] = {ReadExpression(MapValue(shape), "property.Value")};");
                code.AppendLine("        }");
                code.AppendLine();
                code.AppendLine("        return map;");
                break;
        }

        code.AppendLine("    }");
        code.AppendLine();
    }

    private string ReadExpression(string shapeName, string element)
    {
        if (_customShapes.TryGetValue(shapeName, out var custom))
        {
            return $"{custom.Read}({element})";
        }

        return TypeOf(shapeName) switch
        {
            "string" => $"AwsJsonReader.ReadString({element})",
            "integer" => $"AwsJsonReader.ReadInt32({element})",
            "long" => $"AwsJsonReader.ReadInt64({element})",
            "boolean" => $"AwsJsonReader.ReadBoolean({element})",
            "double" => $"AwsJsonReader.ReadDouble({element})",
            "float" => $"(float)AwsJsonReader.ReadDouble({element})",
            "timestamp" => $"AwsJsonReader.ReadTimestamp({element})",
            "blob" => $"AwsJsonReader.ReadBlob({element})",
            _ => $"Read_{shapeName}({element})",
        };
    }

    private void GenerateWriter(StringBuilder code, string shapeName)
    {
        var shape = Shape(shapeName);
        code.AppendLine($"    internal static void Write_{shapeName}(Utf8JsonWriter writer, {CsType(shapeName)} value)");
        code.AppendLine("    {");
        switch (TypeOf(shapeName))
        {
            case "structure":
                var className = ClassName(shapeName);
                code.AppendLine("        writer.WriteStartObject();");
                foreach (var (name, memberShape) in Members(shape))
                {
                    var property = $"value.{PropertyName(className, name)}";
                    code.AppendLine($"        if ({property} is not null)");
                    code.AppendLine("        {");
                    code.AppendLine($"            writer.WritePropertyName(\"{name}\");");
                    code.AppendLine($"            {WriteStatement(memberShape, IsValueType(memberShape) ? property + ".Value" : property)}");
                    code.AppendLine("        }");
                }

                code.AppendLine("        writer.WriteEndObject();");
                break;
            case "list":
                code.AppendLine("        writer.WriteStartArray();");
                code.AppendLine("        foreach (var item in value)");
                code.AppendLine("        {");
                code.AppendLine($"            {WriteStatement(ListMember(shape), "item")}");
                code.AppendLine("        }");
                code.AppendLine("        writer.WriteEndArray();");
                break;
            case "map":
                code.AppendLine("        writer.WriteStartObject();");
                code.AppendLine("        foreach (var entry in value)");
                code.AppendLine("        {");
                code.AppendLine("            writer.WritePropertyName(entry.Key);");
                code.AppendLine($"            {WriteStatement(MapValue(shape), "entry.Value")}");
                code.AppendLine("        }");
                code.AppendLine("        writer.WriteEndObject();");
                break;
        }

        code.AppendLine("    }");
        code.AppendLine();
    }

    private string WriteStatement(string shapeName, string value)
    {
        if (_customShapes.TryGetValue(shapeName, out var custom))
        {
            return $"{custom.Write}(writer, {value});";
        }

        return TypeOf(shapeName) switch
        {
            "string" => $"writer.WriteStringValue({value});",
            "integer" or "long" or "double" or "float" => $"writer.WriteNumberValue({value});",
            "boolean" => $"writer.WriteBooleanValue({value});",
            "timestamp" => $"AwsJsonTimestamp.WriteValue(writer, {value});",
            "blob" => $"writer.WriteBase64StringValue({value});",
            _ => $"Write_{shapeName}(writer, {value});",
        };
    }

    // Validation: the model's constraints, checked as AWS's request validation checks them.

    private void GenerateValidator(StringBuilder code, string shapeName)
    {
        var shape = Shape(shapeName);
        var className = ClassName(shapeName);
        var required = Required(shape);
        code.AppendLine($"    internal static void Validate_{shapeName}(global::{_modelNamespace}.{className} value, string path, List<string> violations)");
        code.AppendLine("    {");
        foreach (var (name, memberShape) in Members(shape))
        {
            var property = $"value.{PropertyName(className, name)}";
            var memberPath = $"{Literal(LowerCamel(name))}";
            var pathExpression = $"(path.Length == 0 ? {memberPath} : path + \".\" + {memberPath})";
            var checks = new StringBuilder();
            AppendValueChecks(checks, memberShape, IsValueType(memberShape) ? property + ".Value" : property, "memberPath", "                ", depth: 0);
            if (!required.Contains(name) && checks.Length == 0)
            {
                continue;
            }

            code.AppendLine("        {");
            code.AppendLine($"            var memberPath = {pathExpression};");
            if (required.Contains(name))
            {
                code.AppendLine($"            if ({property} is null)");
                code.AppendLine("            {");
                code.AppendLine("                violations.Add(AwsValidation.Missing(memberPath));");
                code.AppendLine("            }");
            }

            if (checks.Length > 0)
            {
                code.AppendLine($"            if ({property} is not null)");
                code.AppendLine("            {");
                code.Append(checks);
                code.AppendLine("            }");
            }

            code.AppendLine("        }");
        }

        code.AppendLine("    }");
        code.AppendLine();
    }

    /// <summary>The checks on one present value of <paramref name="shapeName"/> at <paramref name="pathVariable"/>.</summary>
    private void AppendValueChecks(StringBuilder code, string shapeName, string value, string pathVariable, string indent, int depth)
    {
        if (_customShapes.ContainsKey(shapeName) || ServiceCheckedShapes.Contains(shapeName))
        {
            return;
        }

        var shape = Shape(shapeName);
        var type = TypeOf(shapeName);
        var min = shape.TryGetProperty("min", out var minElement) ? minElement.GetDouble() : (double?)null;
        var max = shape.TryGetProperty("max", out var maxElement) ? maxElement.GetDouble() : (double?)null;
        var rendered = RenderExpression(shapeName, value, nullable: false);
        switch (type)
        {
            case "string":
                if (min is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Length < {(long)min})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({value}, {pathVariable}, \"Member must have length greater than or equal to {(long)min}\"));");
                }

                if (max is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Length > {(long)max})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({value}, {pathVariable}, \"Member must have length less than or equal to {(long)max}\"));");
                }

                if (shape.TryGetProperty("pattern", out var pattern))
                {
                    var field = PatternField(pattern.GetString()!);
                    code.AppendLine($"{indent}if (!{field}.IsMatch({value}))");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({value}, {pathVariable}, {Literal("Member must satisfy regular expression pattern: " + pattern.GetString())}));");
                }

                // DynamoDB refuses a ConditionalOperator in its own words (naming AND as ALL), so
                // the client checks it.
                if (shape.TryGetProperty("enum", out var values) && shapeName != "ConditionalOperator")
                {
                    var names = values.EnumerateArray().Select(v => v.GetString()!).ToList();
                    code.AppendLine($"{indent}if ({string.Join(" && ", names.Select(n => $"!string.Equals({value}, {Literal(n)}, StringComparison.Ordinal)"))})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({value}, {pathVariable}, {Literal("Member must satisfy enum value set: [" + string.Join(", ", names) + "]")}));");
                }

                break;
            case "blob":
                if (min is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Length < {(long)min})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have length greater than or equal to {(long)min}\"));");
                }

                if (max is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Length > {(long)max})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have length less than or equal to {(long)max}\"));");
                }

                break;
            case "integer" or "long" or "double" or "float":
                if (min is not null)
                {
                    code.AppendLine($"{indent}if ({value} < {Number(min.Value)})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have value greater than or equal to {Number(min.Value)}\"));");
                }

                if (max is not null)
                {
                    code.AppendLine($"{indent}if ({value} > {Number(max.Value)})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have value less than or equal to {Number(max.Value)}\"));");
                }

                break;
            case "list":
            {
                if (min is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Count < {(long)min})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have length greater than or equal to {(long)min}\"));");
                }

                if (max is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Count > {(long)max})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have length less than or equal to {(long)max}\"));");
                }

                var memberShape = ListMember(shape);
                if (TypeOf(memberShape) == "structure" && !_customShapes.ContainsKey(memberShape))
                {
                    var i = $"i{depth}";
                    code.AppendLine($"{indent}for (var {i} = 0; {i} < {value}.Count; {i}++)");
                    code.AppendLine($"{indent}{{");
                    code.AppendLine($"{indent}    Validate_{memberShape}({value}[{i}], {pathVariable} + \".\" + ({i} + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + \".member\", violations);");
                    code.AppendLine($"{indent}}}");
                }
                else
                {
                    var requirements = Requirements(memberShape);
                    if (requirements.Count > 0)
                    {
                        var item = $"item{depth}";
                        var elementChecks = new StringBuilder();
                        AppendValueChecks(elementChecks, memberShape, item, $"elementPath{depth}", indent + "    ", depth + 1);
                        if (elementChecks.Length > 0)
                        {
                            var i = $"i{depth}";
                            code.AppendLine($"{indent}for (var {i} = 0; {i} < {value}.Count; {i}++)");
                            code.AppendLine($"{indent}{{");
                            code.AppendLine($"{indent}    var {item} = {value}[{i}];");
                            code.AppendLine($"{indent}    var elementPath{depth} = {pathVariable} + \".\" + ({i} + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + \".member\";");
                            code.Append(elementChecks);
                            code.AppendLine($"{indent}}}");
                        }
                    }
                }

                break;
            }
            case "map":
            {
                if (min is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Count < {(long)min})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have length greater than or equal to {(long)min}\"));");
                }

                if (max is not null)
                {
                    code.AppendLine($"{indent}if ({value}.Count > {(long)max})");
                    code.AppendLine($"{indent}    violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, \"Member must have length less than or equal to {(long)max}\"));");
                }

                // AWS reports a key or a (scalar or list) value that breaks its shape's constraints
                // once for the whole map, listing every requirement of that shape.
                var keyShape = MapKey(shape);
                var keyRequirements = Requirements(keyShape);
                if (keyRequirements.Count > 0)
                {
                    var key = $"key{depth}";
                    code.AppendLine($"{indent}foreach (var {key} in {value}.Keys)");
                    code.AppendLine($"{indent}{{");
                    code.AppendLine($"{indent}    if ({Violates(keyShape, key)})");
                    code.AppendLine($"{indent}    {{");
                    code.AppendLine($"{indent}        violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, {Literal("Map keys must satisfy constraint: [" + string.Join(", ", keyRequirements) + "]")}));");
                    code.AppendLine($"{indent}        break;");
                    code.AppendLine($"{indent}    }}");
                    code.AppendLine($"{indent}}}");
                }

                var valueShape = MapValue(shape);
                if (TypeOf(valueShape) == "structure" && !_customShapes.ContainsKey(valueShape))
                {
                    var entry = $"entry{depth}";
                    code.AppendLine($"{indent}foreach (var {entry} in {value})");
                    code.AppendLine($"{indent}{{");
                    code.AppendLine($"{indent}    Validate_{valueShape}({entry}.Value, {pathVariable} + \".\" + {entry}.Key + \".member\", violations);");
                    code.AppendLine($"{indent}}}");
                }
                else
                {
                    var valueRequirements = Requirements(valueShape);
                    if (valueRequirements.Count > 0)
                    {
                        var entry = $"entry{depth}";
                        code.AppendLine($"{indent}foreach (var {entry} in {value})");
                        code.AppendLine($"{indent}{{");
                        code.AppendLine($"{indent}    if ({Violates(valueShape, entry + ".Value")})");
                        code.AppendLine($"{indent}    {{");
                        code.AppendLine($"{indent}        violations.Add(AwsValidation.Clause({rendered}, {pathVariable}, {Literal("Map value must satisfy constraint: [" + string.Join(", ", valueRequirements) + "]")}));");
                        code.AppendLine($"{indent}        break;");
                        code.AppendLine($"{indent}    }}");
                        code.AppendLine($"{indent}}}");
                    }
                }

                break;
            }
            case "structure":
                code.AppendLine($"{indent}Validate_{shapeName}({value}, {pathVariable}, violations);");
                break;
        }
    }

    /// <summary>Every requirement a value of a scalar or list shape must meet, as AWS words them (max first).</summary>
    private List<string> Requirements(string shapeName)
    {
        var result = new List<string>();
        if (_customShapes.ContainsKey(shapeName))
        {
            return result;
        }

        var shape = Shape(shapeName);
        var type = TypeOf(shapeName);
        if (type is not ("string" or "blob" or "list" or "integer" or "long"))
        {
            return result;
        }

        var noun = type is "integer" or "long" ? "value" : "length";
        if (shape.TryGetProperty("max", out var max))
        {
            result.Add($"Member must have {noun} less than or equal to {Number(max.GetDouble())}");
        }

        if (shape.TryGetProperty("min", out var min))
        {
            result.Add($"Member must have {noun} greater than or equal to {Number(min.GetDouble())}");
        }

        if (shape.TryGetProperty("pattern", out var pattern))
        {
            result.Add($"Member must satisfy regular expression pattern: {pattern.GetString()}");
        }

        if (shape.TryGetProperty("enum", out var values))
        {
            result.Add($"Member must satisfy enum value set: [{string.Join(", ", values.EnumerateArray().Select(v => v.GetString()))}]");
        }

        return result;
    }

    /// <summary>An expression that is true when <paramref name="value"/> breaks a constraint of its shape.</summary>
    private string Violates(string shapeName, string value)
    {
        var shape = Shape(shapeName);
        var type = TypeOf(shapeName);
        var conditions = new List<string>();
        var length = type switch
        {
            "string" or "blob" => $"{value}.Length",
            "list" => $"{value}.Count",
            _ => value,
        };
        if (shape.TryGetProperty("min", out var min))
        {
            conditions.Add($"{length} < {Number(min.GetDouble())}");
        }

        if (shape.TryGetProperty("max", out var max))
        {
            conditions.Add($"{length} > {Number(max.GetDouble())}");
        }

        if (shape.TryGetProperty("pattern", out var pattern))
        {
            conditions.Add($"!{PatternField(pattern.GetString()!)}.IsMatch({value})");
        }

        if (shape.TryGetProperty("enum", out var values))
        {
            conditions.Add("(" + string.Join(" && ", values.EnumerateArray().Select(v => $"!string.Equals({value}, {Literal(v.GetString()!)}, StringComparison.Ordinal)")) + ")");
        }

        return conditions.Count == 0 ? "false" : string.Join(" || ", conditions);
    }

    private string PatternField(string pattern)
    {
        if (!_patternFields.TryGetValue(pattern, out var field))
        {
            field = $"Pattern{_patternFields.Count}";
            _patternFields[pattern] = field;
        }

        return field;
    }

    /// <summary>A Java regular expression in .NET's dialect.</summary>
    private static string DotNetPattern(string pattern) =>
        pattern
            .Replace(@"\p{Print}", @"[\x20-\x7E]", StringComparison.Ordinal)
            // A surrogate pair range, which .NET (matching UTF-16 code units) can't express as Java does.
            .Replace(@"\uD800\uDC00-\uDBFF\uDFFF", @"\uD800-\uDBFF\uDC00-\uDFFF", StringComparison.Ordinal)
            // Java allows escaping any punctuation; .NET refuses \_ as an unknown escape.
            .Replace(@"\_", "_", StringComparison.Ordinal);

    private static string Number(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static string Literal(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => c.ToString(),
            });
        }

        return text.Append('"').ToString();
    }

    // ------------------------------------------------------------------------------------------
    // Dispatcher
    // ------------------------------------------------------------------------------------------

    public string GenerateDispatcher()
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine($"// Generated transport-neutral operation dispatcher for {_serviceName}");
        code.AppendLine("#nullable enable");
        code.AppendLine();
        code.AppendLine("using System.Text.Json;");
        code.AppendLine($"using {_modelNamespace};");
        code.AppendLine();
        code.AppendLine("namespace LocalSqsSnsMessaging.Http.Handlers;");
        code.AppendLine();
        code.AppendLine($"internal static partial class {_serviceName}OperationDispatcher");
        code.AppendLine("{");
        code.AppendLine("    /// <summary>Whether the service's model has an operation of this name.</summary>");
        code.AppendLine("    public static bool IsOperation(string operationName) => operationName switch");
        code.AppendLine("    {");
        var names = Operations().Select(o => o.Name).ToList();
        for (var i = 0; i < names.Count; i += 4)
        {
            var chunk = string.Join(" or ", names.Skip(i).Take(4).Select(n => $"\"{n}\""));
            code.AppendLine(i == 0 ? $"        {chunk}" : $"            or {chunk}");
        }

        code.AppendLine("            => true,");
        code.AppendLine("        _ => false,");
        code.AppendLine("    };");
        code.AppendLine();
        code.AppendLine("    /// <summary>");
        code.AppendLine("    /// Deserializes the request in <paramref name=\"root\"/>, validates it against the model's");
        code.AppendLine("    /// constraints, runs it on <paramref name=\"client\"/> and writes the response to <paramref name=\"writer\"/>.");
        code.AppendLine("    /// </summary>");
        code.AppendLine($"    public static async Task DispatchAsync(string operationName, JsonElement root, {_clientType} client, Utf8JsonWriter writer, CancellationToken cancellationToken)");
        code.AppendLine("    {");
        code.AppendLine("        switch (operationName)");
        code.AppendLine("        {");
        foreach (var (opName, operation) in Operations())
        {
            var inputShape = operation.GetProperty("input").GetProperty("shape").GetString()!;
            code.AppendLine($"            case \"{opName}\":");
            code.AppendLine("            {");
            code.AppendLine($"                var request = {_serviceName}JsonSerializers.Read_{inputShape}(root);");
            code.AppendLine("                var violations = new List<string>();");
            code.AppendLine($"                {_serviceName}JsonSerializers.Validate_{inputShape}(request, string.Empty, violations);");
            if (_requestChecks is not null)
            {
                code.AppendLine($"                global::{_requestChecks}.Check(request, violations);");
            }

            code.AppendLine("                AwsValidation.ThrowIfAny(violations);");
            code.AppendLine($"                var response = await client.{opName}Async(request, cancellationToken).ConfigureAwait(false);");
            if (operation.TryGetProperty("output", out var output))
            {
                code.AppendLine($"                {_serviceName}JsonSerializers.Write_{output.GetProperty("shape").GetString()}(writer, response);");
            }
            else
            {
                // A Unit output answers an empty body: the .NET SDK reads none, and checks
                // x-amz-crc32 against the bytes it read, so a "{}" body fails its checksum.
                code.AppendLine("                _ = response;");
            }

            code.AppendLine("                return;");
            code.AppendLine("            }");
        }

        code.AppendLine("            default:");
        code.AppendLine("                throw new InternalUnknownOperationException();");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }
}
