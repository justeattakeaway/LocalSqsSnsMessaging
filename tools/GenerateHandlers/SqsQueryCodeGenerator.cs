using System.Text;
using System.Text.Json;

namespace GenerateHandlers;

/// <summary>
/// Generates the AWS Query protocol (form-encoded requests, XML responses) serializers and
/// dispatcher for SQS.
/// </summary>
/// <remarks>
/// <para>
/// SQS speaks JSON today, but it is <c>awsQueryCompatible</c>: it still serves the Query protocol it
/// was born with, and a message or attribute written over one reads back identically over the
/// other. The service model in <c>ServiceModels/</c> is the JSON one, which has lost the Query wire
/// names (the old model's <c>locationName</c> traits), so they are restored from
/// <see cref="WireNames"/> and <see cref="MapEntryNames"/>, taken from the
/// <c>sqs-2012-11-05</c> Query model and the <c>@xmlName</c> traits in AWS's Smithy model.
/// </para>
/// <para>
/// Unlike <see cref="QueryCodeGenerator"/> (SNS), every SQS list and map is flattened: a list member
/// <c>AttributeNames</c> goes on the wire as <c>AttributeName.1</c>, <c>AttributeName.2</c>, and a
/// map member <c>Attributes</c> as <c>Attribute.1.Name</c> / <c>Attribute.1.Value</c>, in requests
/// and as repeated elements in responses.
/// </para>
/// </remarks>
internal sealed class SqsQueryCodeGenerator
{
    /// <summary>Query wire name of a structure member, keyed <c>Shape.Member</c>, where it is not the member name.</summary>
    private static readonly Dictionary<string, string> WireNames = new(StringComparer.Ordinal)
    {
        ["AddPermissionRequest.AWSAccountIds"] = "AWSAccountId",
        ["AddPermissionRequest.Actions"] = "ActionName",
        ["ChangeMessageVisibilityBatchRequest.Entries"] = "ChangeMessageVisibilityBatchRequestEntry",
        ["ChangeMessageVisibilityBatchResult.Successful"] = "ChangeMessageVisibilityBatchResultEntry",
        ["ChangeMessageVisibilityBatchResult.Failed"] = "BatchResultErrorEntry",
        ["CreateQueueRequest.Attributes"] = "Attribute",
        ["CreateQueueRequest.tags"] = "Tag",
        ["DeleteMessageBatchRequest.Entries"] = "DeleteMessageBatchRequestEntry",
        ["DeleteMessageBatchResult.Successful"] = "DeleteMessageBatchResultEntry",
        ["DeleteMessageBatchResult.Failed"] = "BatchResultErrorEntry",
        ["GetQueueAttributesRequest.AttributeNames"] = "AttributeName",
        ["GetQueueAttributesResult.Attributes"] = "Attribute",
        ["ListDeadLetterSourceQueuesResult.queueUrls"] = "QueueUrl",
        ["ListMessageMoveTasksResult.Results"] = "ListMessageMoveTasksResultEntry",
        ["ListQueueTagsResult.Tags"] = "Tag",
        ["ListQueuesResult.QueueUrls"] = "QueueUrl",
        ["Message.Attributes"] = "Attribute",
        ["Message.MessageAttributes"] = "MessageAttribute",
        ["MessageAttributeValue.StringListValues"] = "StringListValue",
        ["MessageAttributeValue.BinaryListValues"] = "BinaryListValue",
        ["MessageSystemAttributeValue.StringListValues"] = "StringListValue",
        ["MessageSystemAttributeValue.BinaryListValues"] = "BinaryListValue",
        ["ReceiveMessageRequest.AttributeNames"] = "AttributeName",
        ["ReceiveMessageRequest.MessageSystemAttributeNames"] = "MessageSystemAttributeName",
        ["ReceiveMessageRequest.MessageAttributeNames"] = "MessageAttributeName",
        ["ReceiveMessageResult.Messages"] = "Message",
        ["SendMessageBatchRequest.Entries"] = "SendMessageBatchRequestEntry",
        ["SendMessageBatchRequestEntry.MessageAttributes"] = "MessageAttribute",
        ["SendMessageBatchRequestEntry.MessageSystemAttributes"] = "MessageSystemAttribute",
        ["SendMessageBatchResult.Successful"] = "SendMessageBatchResultEntry",
        ["SendMessageBatchResult.Failed"] = "BatchResultErrorEntry",
        ["SendMessageRequest.MessageAttributes"] = "MessageAttribute",
        ["SendMessageRequest.MessageSystemAttributes"] = "MessageSystemAttribute",
        ["SetQueueAttributesRequest.Attributes"] = "Attribute",
        ["TagQueueRequest.Tags"] = "Tag",
        ["UntagQueueRequest.TagKeys"] = "TagKey",
    };

    /// <summary>The key and value names of a map entry on the wire; any map not listed uses Name/Value.</summary>
    private static readonly Dictionary<string, (string Key, string Value)> MapEntryNames = new(StringComparer.Ordinal)
    {
        ["TagMap"] = ("Key", "Value"),
    };

    private readonly string _modelNamespace;
    private readonly string _xmlNamespace;
    private readonly JsonElement _shapes;
    private readonly SortedSet<string> _pendingHelpers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _writtenHelpers = new(StringComparer.Ordinal);
    private int _variableCounter;

    public SqsQueryCodeGenerator(string modelNamespace, string xmlNamespace, JsonElement shapes)
    {
        _modelNamespace = modelNamespace;
        _xmlNamespace = xmlNamespace;
        _shapes = shapes;
    }

    /// <summary>Generates <c>SqsQuerySerializers.g.cs</c>.</summary>
    public string GenerateSerializers(JsonElement operations)
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("// Generated AWS Query protocol serializers for Sqs");
        code.AppendLine("#nullable enable");
        code.AppendLine();
        code.AppendLine("using System.Buffers;");
        code.AppendLine("using System.Globalization;");
        code.AppendLine("using System.Xml;");
        code.AppendLine($"using {_modelNamespace};");
        code.AppendLine();
        code.AppendLine("namespace LocalSqsSnsMessaging.Http.Handlers;");
        code.AppendLine();
        code.AppendLine("/// <summary>Reads SQS Query-protocol (form-encoded) requests and writes their XML responses.</summary>");
        code.AppendLine("internal static class SqsQuerySerializers");
        code.AppendLine("{");
        code.AppendLine($"    internal const string XmlNamespace = \"{_xmlNamespace}\";");
        code.AppendLine();

        foreach (var operation in operations.EnumerateObject())
        {
            var opName = operation.Name;
            if (!operation.Value.TryGetProperty("input", out var input))
            {
                continue;
            }

            GenerateRequestDeserializer(code, opName, input.GetProperty("shape").GetString()!);

            var outputShape = operation.Value.TryGetProperty("output", out var output)
                ? output.GetProperty("shape").GetString()
                : null;
            GenerateResponseSerializer(code, opName, outputShape);
        }

        while (_pendingHelpers.Count > 0)
        {
            var helper = _pendingHelpers.Min!;
            _pendingHelpers.Remove(helper);
            if (!_writtenHelpers.Add(helper))
            {
                continue;
            }

            var separator = helper.IndexOf('_', StringComparison.Ordinal);
            var kind = helper[..separator];
            var shapeName = helper[(separator + 1)..];
            switch (kind)
            {
                case "ReadStructure":
                    GenerateStructureReader(code, shapeName);
                    break;
                case "WriteStructure":
                    GenerateStructureWriter(code, shapeName);
                    break;
            }
        }

        code.AppendLine("    private static MemoryStream ReadBlob(string value) => new(Convert.FromBase64String(value));");
        code.AppendLine();
        code.AppendLine("    private static string WriteBlob(MemoryStream value) => Convert.ToBase64String(value.ToArray());");
        code.AppendLine();
        code.AppendLine("    /// <summary>True if any parameter is named <paramref name=\"prefix\"/> or starts with <c>prefix.</c>.</summary>");
        code.AppendLine("    private static bool HasPrefix(Dictionary<string, string> parameters, string prefix)");
        code.AppendLine("    {");
        code.AppendLine("        if (parameters.ContainsKey(prefix))");
        code.AppendLine("            return true;");
        code.AppendLine("        var dotted = prefix + \".\";");
        code.AppendLine("        foreach (var key in parameters.Keys)");
        code.AppendLine("        {");
        code.AppendLine("            if (key.StartsWith(dotted, StringComparison.Ordinal))");
        code.AppendLine("                return true;");
        code.AppendLine("        }");
        code.AppendLine("        return false;");
        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }

    /// <summary>Generates <c>SqsQueryOperationHandler.g.cs</c>: Action name to request, call and response.</summary>
    public static string GenerateDispatcher(JsonElement operations)
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("// Generated AWS Query protocol dispatcher for Sqs");
        code.AppendLine("#nullable enable");
        code.AppendLine();
        code.AppendLine("using System.Buffers;");
        code.AppendLine();
        code.AppendLine("namespace LocalSqsSnsMessaging.Http.Handlers;");
        code.AppendLine();
        code.AppendLine("internal static partial class SqsQueryOperationHandler");
        code.AppendLine("{");
        code.AppendLine("    /// <summary>");
        code.AppendLine("    /// Runs the SQS operation named <paramref name=\"action\"/> and writes its XML response.");
        code.AppendLine("    /// Returns false, writing nothing, when SQS has no such operation.");
        code.AppendLine("    /// </summary>");
        code.AppendLine("    private static async Task<bool> TryDispatchAsync(");
        code.AppendLine("        string action,");
        code.AppendLine("        Dictionary<string, string> parameters,");
        code.AppendLine("        InternalSqsClient client,");
        code.AppendLine("        IBufferWriter<byte> output,");
        code.AppendLine("        CancellationToken cancellationToken)");
        code.AppendLine("    {");
        code.AppendLine("        switch (action)");
        code.AppendLine("        {");
        foreach (var operation in operations.EnumerateObject())
        {
            var opName = operation.Name;
            if (!operation.Value.TryGetProperty("input", out _))
            {
                continue;
            }

            code.AppendLine($"            case \"{opName}\":");
            code.AppendLine("            {");
            code.AppendLine($"                var request = SqsQuerySerializers.Deserialize{opName}Request(parameters);");
            code.AppendLine($"                var response = await client.{opName}Async(request, cancellationToken).ConfigureAwait(false);");
            code.AppendLine($"                SqsQuerySerializers.Serialize{opName}Response(response, output);");
            code.AppendLine("                return true;");
            code.AppendLine("            }");
        }

        code.AppendLine("            default:");
        code.AppendLine("                return false;");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    /// <summary>True if SQS has an operation named <paramref name=\"name\"/>, over either protocol.</summary>");
        code.AppendLine("    public static bool IsOperation(string name) => name switch");
        code.AppendLine("    {");
        var names = operations.EnumerateObject().Where(o => o.Value.TryGetProperty("input", out _)).Select(o => $"\"{o.Name}\"");
        code.AppendLine($"        {string.Join(" or ", names)} => true,");
        code.AppendLine("        _ => false,");
        code.AppendLine("    };");
        code.AppendLine("}");
        return code.ToString();
    }

    private void GenerateRequestDeserializer(StringBuilder code, string opName, string shapeName)
    {
        code.AppendLine($"    internal static {opName}Request Deserialize{opName}Request(Dictionary<string, string> parameters)");
        code.AppendLine("    {");
        code.AppendLine($"        var request = new {opName}Request();");
        foreach (var member in Members(shapeName))
        {
            code.AppendLine($"        // {member.Name}");
            GenerateMemberRead(code, "        ", shapeName, member.Name, member.Value, "parameters", $"\"{WireName(shapeName, member.Name)}\"", "request", hasAnyVar: null);
        }

        code.AppendLine("        return request;");
        code.AppendLine("    }");
        code.AppendLine();
    }

    private void GenerateResponseSerializer(StringBuilder code, string opName, string? shapeName)
    {
        code.AppendLine($"    internal static void Serialize{opName}Response({opName}Response response, IBufferWriter<byte> buffer)");
        code.AppendLine("    {");
        code.AppendLine("        using var stream = new BufferWriterStream(buffer);");
        code.AppendLine("        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false });");
        code.AppendLine($"        writer.WriteStartElement(\"{opName}Response\", XmlNamespace);");
        if (shapeName is not null)
        {
            code.AppendLine($"        writer.WriteStartElement(\"{opName}Result\");");
            foreach (var member in Members(shapeName))
            {
                GenerateMemberWrite(code, "        ", shapeName, member.Name, member.Value, $"response.{Property(member.Name)}");
            }

            code.AppendLine("        writer.WriteEndElement();");
        }

        code.AppendLine("        writer.WriteStartElement(\"ResponseMetadata\");");
        code.AppendLine("        writer.WriteElementString(\"RequestId\", Guid.NewGuid().ToString());");
        code.AppendLine("        writer.WriteEndElement();");
        code.AppendLine("        writer.WriteEndElement();");
        code.AppendLine("        writer.Flush();");
        code.AppendLine("    }");
        code.AppendLine();
    }

    private void GenerateStructureReader(StringBuilder code, string shapeName)
    {
        code.AppendLine($"    private static {shapeName}? Read{shapeName}(Dictionary<string, string> parameters, string prefix)");
        code.AppendLine("    {");
        code.AppendLine($"        var structure = new {shapeName}();");
        code.AppendLine("        var hasAny = false;");
        foreach (var member in Members(shapeName))
        {
            GenerateMemberRead(code, "        ", shapeName, member.Name, member.Value, "parameters", $"prefix + \".{WireName(shapeName, member.Name)}\"", "structure", hasAnyVar: "hasAny");
        }

        code.AppendLine("        return hasAny ? structure : null;");
        code.AppendLine("    }");
        code.AppendLine();
    }

    private void GenerateStructureWriter(StringBuilder code, string shapeName)
    {
        code.AppendLine($"    private static void Write{shapeName}(XmlWriter writer, string elementName, {shapeName} structure)");
        code.AppendLine("    {");
        code.AppendLine("        writer.WriteStartElement(elementName);");
        foreach (var member in Members(shapeName))
        {
            GenerateMemberWrite(code, "        ", shapeName, member.Name, member.Value, $"structure.{Property(member.Name)}");
        }

        code.AppendLine("        writer.WriteEndElement();");
        code.AppendLine("    }");
        code.AppendLine();
    }

    /// <summary>Reads one member, whose parameter name (or prefix) is the C# expression <paramref name="keyExpr"/>.</summary>
    private void GenerateMemberRead(StringBuilder code, string indent, string ownerShape, string memberName, JsonElement member, string parametersVar, string keyExpr, string targetVar, string? hasAnyVar)
    {
        var targetShapeName = member.GetProperty("shape").GetString()!;
        var targetShape = _shapes.GetProperty(targetShapeName);
        var type = targetShape.GetProperty("type").GetString()!;
        var property = $"{targetVar}.{Property(memberName)}";
        var markAny = hasAnyVar is null ? "" : $" {hasAnyVar} = true;";
        var v = NextVariable();

        switch (type)
        {
            case "string":
                code.AppendLine($"{indent}if ({parametersVar}.TryGetValue({keyExpr}, out var {v}))");
                code.AppendLine($"{indent}{{ {property} = {v};{markAny} }}");
                break;
            case "integer":
            case "long":
            case "boolean":
                var parse = type switch
                {
                    "integer" => $"int.TryParse({v}, NumberStyles.Integer, CultureInfo.InvariantCulture, out var {v}Parsed)",
                    "long" => $"long.TryParse({v}, NumberStyles.Integer, CultureInfo.InvariantCulture, out var {v}Parsed)",
                    _ => $"bool.TryParse({v}, out var {v}Parsed)",
                };
                code.AppendLine($"{indent}if ({parametersVar}.TryGetValue({keyExpr}, out var {v}) && {parse})");
                code.AppendLine($"{indent}{{ {property} = {v}Parsed;{markAny} }}");
                break;
            case "blob":
                code.AppendLine($"{indent}if ({parametersVar}.TryGetValue({keyExpr}, out var {v}))");
                code.AppendLine($"{indent}{{ {property} = ReadBlob({v});{markAny} }}");
                break;
            case "list":
            {
                var itemShapeName = targetShape.GetProperty("member").GetProperty("shape").GetString()!;
                var itemType = _shapes.GetProperty(itemShapeName).GetProperty("type").GetString()!;
                var csharpItem = CSharpType(itemShapeName, itemType);
                var itemKey = IsFlattened(member, targetShape) ? $"{v}Prefix + \".\" + {v}Index" : $"{v}Prefix + \".member.\" + {v}Index";
                code.AppendLine($"{indent}{{");
                code.AppendLine($"{indent}    var {v}Prefix = {keyExpr};");
                code.AppendLine($"{indent}    var {v}List = new List<{csharpItem}>();");
                code.AppendLine($"{indent}    for (var {v}Index = 1; ; {v}Index++)");
                code.AppendLine($"{indent}    {{");
                code.AppendLine($"{indent}        var {v}Key = {itemKey}.ToString(CultureInfo.InvariantCulture);");
                AppendItemRead(code, $"{indent}        ", itemShapeName, itemType, parametersVar, $"{v}Key", $"{v}List.Add");
                code.AppendLine($"{indent}    }}");
                code.AppendLine($"{indent}    if ({v}List.Count > 0 || {parametersVar}.ContainsKey({v}Prefix))");
                code.AppendLine($"{indent}    {{ {property} = {v}List;{markAny} }}");
                code.AppendLine($"{indent}}}");
                break;
            }
            case "map":
            {
                var (keyName, valueName) = MapEntryNames.GetValueOrDefault(targetShapeName, ("Name", "Value"));
                var valueShapeName = targetShape.GetProperty("value").GetProperty("shape").GetString()!;
                var valueType = _shapes.GetProperty(valueShapeName).GetProperty("type").GetString()!;
                var csharpValue = CSharpType(valueShapeName, valueType);
                var entryPrefix = IsFlattened(member, targetShape) ? $"{v}Prefix + \".\" + {v}Index" : $"{v}Prefix + \".entry.\" + {v}Index";
                code.AppendLine($"{indent}{{");
                code.AppendLine($"{indent}    var {v}Prefix = {keyExpr};");
                code.AppendLine($"{indent}    var {v}Map = new Dictionary<string, {csharpValue}>();");
                code.AppendLine($"{indent}    for (var {v}Index = 1; ; {v}Index++)");
                code.AppendLine($"{indent}    {{");
                code.AppendLine($"{indent}        var {v}Entry = {entryPrefix}.ToString(CultureInfo.InvariantCulture);");
                code.AppendLine($"{indent}        if (!{parametersVar}.TryGetValue({v}Entry + \".{keyName}\", out var {v}MapKey))");
                code.AppendLine($"{indent}            break;");
                AppendItemRead(code, $"{indent}        ", valueShapeName, valueType, parametersVar, $"{v}Entry + \".{valueName}\"", $"{v}Map[{v}MapKey] = ", assign: true);
                code.AppendLine($"{indent}    }}");
                code.AppendLine($"{indent}    if ({v}Map.Count > 0 || {parametersVar}.ContainsKey({v}Prefix))");
                code.AppendLine($"{indent}    {{ {property} = {v}Map;{markAny} }}");
                code.AppendLine($"{indent}}}");
                break;
            }
            case "structure":
                _pendingHelpers.Add($"ReadStructure_{targetShapeName}");
                code.AppendLine($"{indent}if (Read{targetShapeName}({parametersVar}, {keyExpr}) is {{ }} {v})");
                code.AppendLine($"{indent}{{ {property} = {v};{markAny} }}");
                break;
            default:
                throw new NotSupportedException($"{ownerShape}.{memberName}: Query reading of '{type}' is not supported.");
        }
    }

    /// <summary>
    /// Reads one list item or map value at the parameter (or prefix) <paramref name="keyExpr"/>. A
    /// list item that is absent ends the list; a map value that is absent skips the entry.
    /// </summary>
    private void AppendItemRead(StringBuilder code, string indent, string shapeName, string type, string parametersVar, string keyExpr, string sink, bool assign = false)
    {
        string Sink(string value) => assign ? $"{sink}{value};" : $"{sink}({value});";
        var missing = assign ? "continue" : "break";
        var v = NextVariable();
        switch (type)
        {
            case "string":
                code.AppendLine($"{indent}if (!{parametersVar}.TryGetValue({keyExpr}, out var {v}))");
                code.AppendLine($"{indent}    {missing};");
                code.AppendLine($"{indent}{Sink(v)}");
                break;
            case "blob":
                code.AppendLine($"{indent}if (!{parametersVar}.TryGetValue({keyExpr}, out var {v}))");
                code.AppendLine($"{indent}    {missing};");
                code.AppendLine($"{indent}{Sink($"ReadBlob({v})")}");
                break;
            case "structure":
                _pendingHelpers.Add($"ReadStructure_{shapeName}");
                // A map entry whose value has no fields still counts, as {"name": {}} would over
                // JSON, so that SQS validation refuses it; a list ends at the first absent item.
                code.AppendLine($"{indent}var {v} = Read{shapeName}({parametersVar}, {keyExpr});");
                if (!assign)
                {
                    code.AppendLine($"{indent}if ({v} == null && !HasPrefix({parametersVar}, {keyExpr}))");
                    code.AppendLine($"{indent}    break;");
                }

                code.AppendLine($"{indent}{Sink($"{v} ?? new {shapeName}()")}");
                break;
            default:
                throw new NotSupportedException($"{shapeName}: Query reading of '{type}' items is not supported.");
        }
    }

    /// <summary>Writes one member, read from the C# expression <paramref name="valueExpr"/>.</summary>
    private void GenerateMemberWrite(StringBuilder code, string indent, string ownerShape, string memberName, JsonElement member, string valueExpr)
    {
        var targetShapeName = member.GetProperty("shape").GetString()!;
        var targetShape = _shapes.GetProperty(targetShapeName);
        var type = targetShape.GetProperty("type").GetString()!;
        var wireName = WireName(ownerShape, memberName);

        switch (type)
        {
            case "string":
                code.AppendLine($"{indent}if ({valueExpr} != null) writer.WriteElementString(\"{wireName}\", {valueExpr});");
                break;
            case "integer":
            case "long":
                code.AppendLine($"{indent}if ({valueExpr}.HasValue) writer.WriteElementString(\"{wireName}\", {valueExpr}.Value.ToString(CultureInfo.InvariantCulture));");
                break;
            case "boolean":
                code.AppendLine($"{indent}if ({valueExpr}.HasValue) writer.WriteElementString(\"{wireName}\", {valueExpr}.Value ? \"true\" : \"false\");");
                break;
            case "blob":
                code.AppendLine($"{indent}if ({valueExpr} != null) writer.WriteElementString(\"{wireName}\", WriteBlob({valueExpr}));");
                break;
            case "list":
            {
                var itemShapeName = targetShape.GetProperty("member").GetProperty("shape").GetString()!;
                var itemType = _shapes.GetProperty(itemShapeName).GetProperty("type").GetString()!;
                var flattened = IsFlattened(member, targetShape);
                var v = NextVariable();
                code.AppendLine($"{indent}if ({valueExpr} is {{ Count: > 0 }} {v}List)");
                code.AppendLine($"{indent}{{");
                if (!flattened)
                {
                    code.AppendLine($"{indent}    writer.WriteStartElement(\"{wireName}\");");
                }

                code.AppendLine($"{indent}    foreach (var {v}Item in {v}List)");
                code.AppendLine($"{indent}    {{");
                AppendItemWrite(code, $"{indent}        ", itemShapeName, itemType, flattened ? wireName : "member", $"{v}Item");
                code.AppendLine($"{indent}    }}");
                if (!flattened)
                {
                    code.AppendLine($"{indent}    writer.WriteEndElement();");
                }

                code.AppendLine($"{indent}}}");
                break;
            }
            case "map":
            {
                var (keyName, valueName) = MapEntryNames.GetValueOrDefault(targetShapeName, ("Name", "Value"));
                var valueShapeName = targetShape.GetProperty("value").GetProperty("shape").GetString()!;
                var valueType = _shapes.GetProperty(valueShapeName).GetProperty("type").GetString()!;
                var flattened = IsFlattened(member, targetShape);
                var v = NextVariable();
                code.AppendLine($"{indent}if ({valueExpr} is {{ Count: > 0 }} {v}Map)");
                code.AppendLine($"{indent}{{");
                if (!flattened)
                {
                    code.AppendLine($"{indent}    writer.WriteStartElement(\"{wireName}\");");
                }

                code.AppendLine($"{indent}    foreach (var {v}Entry in {v}Map)");
                code.AppendLine($"{indent}    {{");
                code.AppendLine($"{indent}        writer.WriteStartElement(\"{(flattened ? wireName : "entry")}\");");
                code.AppendLine($"{indent}        writer.WriteElementString(\"{keyName}\", {v}Entry.Key);");
                AppendItemWrite(code, $"{indent}        ", valueShapeName, valueType, valueName, $"{v}Entry.Value");
                code.AppendLine($"{indent}        writer.WriteEndElement();");
                code.AppendLine($"{indent}    }}");
                if (!flattened)
                {
                    code.AppendLine($"{indent}    writer.WriteEndElement();");
                }

                code.AppendLine($"{indent}}}");
                break;
            }
            case "structure":
                _pendingHelpers.Add($"WriteStructure_{targetShapeName}");
                code.AppendLine($"{indent}if ({valueExpr} != null) Write{targetShapeName}(writer, \"{wireName}\", {valueExpr});");
                break;
            default:
                throw new NotSupportedException($"{ownerShape}.{memberName}: Query writing of '{type}' is not supported.");
        }
    }

    private void AppendItemWrite(StringBuilder code, string indent, string shapeName, string type, string elementName, string valueExpr)
    {
        switch (type)
        {
            case "string":
                code.AppendLine($"{indent}writer.WriteElementString(\"{elementName}\", {valueExpr});");
                break;
            case "blob":
                code.AppendLine($"{indent}writer.WriteElementString(\"{elementName}\", WriteBlob({valueExpr}));");
                break;
            case "structure":
                _pendingHelpers.Add($"WriteStructure_{shapeName}");
                code.AppendLine($"{indent}Write{shapeName}(writer, \"{elementName}\", {valueExpr});");
                break;
            default:
                throw new NotSupportedException($"{shapeName}: Query writing of '{type}' items is not supported.");
        }
    }

    private List<JsonProperty> Members(string shapeName)
        => _shapes.GetProperty(shapeName).TryGetProperty("members", out var members)
            ? [.. members.EnumerateObject()]
            : [];

    private static string WireName(string shapeName, string memberName)
        => WireNames.GetValueOrDefault($"{shapeName}.{memberName}", memberName);

    private static bool IsFlattened(JsonElement member, JsonElement targetShape)
        => (member.TryGetProperty("flattened", out var m) && m.GetBoolean())
           || (targetShape.TryGetProperty("flattened", out var s) && s.GetBoolean());

    private string NextVariable() => $"v{++_variableCounter}";

    private static string Property(string memberName)
        => char.IsUpper(memberName[0]) ? memberName : char.ToUpperInvariant(memberName[0]) + memberName[1..];

    private static string CSharpType(string shapeName, string type) => type switch
    {
        "string" => "string",
        "blob" => "MemoryStream",
        "structure" => shapeName,
        _ => throw new NotSupportedException($"{shapeName}: '{type}' collection items are not supported."),
    };
}
