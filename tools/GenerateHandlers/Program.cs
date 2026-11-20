using System.Text;
using System.Text.Json;
using GenerateHandlers;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: GenerateHandlers <project-directory>");
    return 1;
}

var projectDir = args[0];
var serviceModelsDir = Path.Combine(projectDir, "ServiceModels");
var outputDir = Path.Combine(projectDir, "Http", "Handlers", "Generated");
var modelsDir = Path.Combine(projectDir, "Models");

Directory.CreateDirectory(outputDir);
Directory.CreateDirectory(modelsDir);

Console.WriteLine("Generating operation handlers from AWS service models...");

// Generate internal POCO types from service models
GenerateInternalTypes(
    Path.Combine(serviceModelsDir, "sqs-2012-11-05.normal.json"),
    "Sqs",
    "InMemoryAws.Sqs.Model",
    modelsDir);

GenerateInternalTypes(
    Path.Combine(serviceModelsDir, "sns-2010-03-31.normal.json"),
    "Sns",
    "InMemoryAws.Sns.Model",
    modelsDir);

GenerateInternalTypes(
    Path.Combine(serviceModelsDir, "eventbridge-2015-10-07.normal.json"),
    "EventBridge",
    "InMemoryAws.EventBridge.Model",
    modelsDir);

// Generate SQS handler (JSON protocol)
GenerateJsonProtocolHandler(
    Path.Combine(serviceModelsDir, "sqs-2012-11-05.normal.json"),
    "Sqs",
    "InMemoryAws.Sqs.Model",
    outputDir);

// Generate SQS's second wire protocol: SQS is awsQueryCompatible and still serves AWS Query
GenerateSqsQueryProtocol(
    Path.Combine(serviceModelsDir, "sqs-2012-11-05.normal.json"),
    "InMemoryAws.Sqs.Model",
    outputDir);

// Generate SNS handler (Query protocol)
GenerateQueryProtocolHandler(
    Path.Combine(serviceModelsDir, "sns-2010-03-31.normal.json"),
    "Sns",
    "InMemoryAws.Sns.Model",
    outputDir);

// Generate EventBridge handler (JSON protocol, json 1.1). EventBridge answers an empty list as
// "Rules": [] rather than leaving the member out.
GenerateJsonProtocolHandler(
    Path.Combine(serviceModelsDir, "eventbridge-2015-10-07.normal.json"),
    "EventBridge",
    "InMemoryAws.EventBridge.Model",
    outputDir,
    writeEmptyResponseLists: true);

// DynamoDB and DynamoDB Streams (JSON protocol, json 1.0). Their AttributeValue holds maps and
// lists of itself, so they use the shape-per-method generator, with AttributeValue read straight
// into the immutable DynamoValue the store keeps.
var attributeValue = new Dictionary<string, ShapeJsonCodeGenerator.CustomShape>(StringComparer.Ordinal)
{
    ["AttributeValue"] = new(
        "InMemoryAws.DynamoDb.DynamoValue",
        "InMemoryAws.DynamoDb.DynamoValueJson.Read",
        "InMemoryAws.DynamoDb.DynamoValueJson.Write",
        "InMemoryAws.DynamoDb.DynamoValueJson.Render"),
};

GenerateShapeJsonService(
    Path.Combine(serviceModelsDir, "dynamodb-2012-08-10.normal.json"),
    "DynamoDb",
    "InMemoryAws.DynamoDb.Model",
    "InMemoryAws.DynamoDb.InternalDynamoDbClient",
    modelsDir,
    outputDir,
    attributeValue,
    "InMemoryAws.DynamoDb.DynamoDbRequestChecks",
    // DynamoDB's own write requests carry a workload profile the model does not declare.
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PutRequest"] = "workloadProfileName=null",
        ["DeleteRequest"] = "workloadProfileName=null",
    });

GenerateShapeJsonService(
    Path.Combine(serviceModelsDir, "streams.dynamodb-2012-08-10.normal.json"),
    "DynamoDbStreams",
    "InMemoryAws.DynamoDbStreams.Model",
    "InMemoryAws.DynamoDb.InternalDynamoDbStreamsClient",
    modelsDir,
    outputDir,
    attributeValue);

// S3 (REST-XML protocol): models, XML serializers, the router and dispatcher, and the client base.
GenerateRestXmlService(Path.Combine(serviceModelsDir, "s3-2006-03-01.normal.json"), modelsDir, outputDir, Path.Combine(projectDir, "S3Client"));

Console.WriteLine("Code generation completed successfully!");
return 0;

static void GenerateRestXmlService(string modelPath, string modelsDir, string outputDir, string clientDir)
{
    Console.WriteLine("  Processing S3 (REST-XML protocol)...");

    using var doc = JsonDocument.Parse(File.ReadAllText(modelPath));
    var generator = new RestXmlCodeGenerator(doc.RootElement);
    Directory.CreateDirectory(clientDir);
    File.WriteAllText(Path.Combine(modelsDir, "S3Models.g.cs"), generator.GenerateModels());
    File.WriteAllText(Path.Combine(outputDir, "S3XmlSerializers.g.cs"), generator.GenerateSerializers());
    File.WriteAllText(Path.Combine(outputDir, "S3WireProtocol.g.cs"), generator.GenerateWireProtocol());
    File.WriteAllText(Path.Combine(clientDir, "S3ClientBase.g.cs"), generator.GenerateClientBase());

    Console.WriteLine("    Generated models, serializers, router, dispatcher and client base for S3");
}

static void GenerateShapeJsonService(
    string modelPath,
    string serviceName,
    string modelNamespace,
    string clientType,
    string modelsDir,
    string outputDir,
    IReadOnlyDictionary<string, ShapeJsonCodeGenerator.CustomShape> customShapes,
    string? requestChecks = null,
    IReadOnlyDictionary<string, string>? unmodeledMembers = null)
{
    Console.WriteLine($"  Processing {serviceName} (JSON protocol, shape per method)...");

    using var doc = JsonDocument.Parse(File.ReadAllText(modelPath));
    var root = doc.RootElement;
    var generator = new ShapeJsonCodeGenerator(
        serviceName, modelNamespace, clientType, root.GetProperty("shapes"), root.GetProperty("operations"), customShapes, requestChecks, unmodeledMembers);

    File.WriteAllText(Path.Combine(modelsDir, $"{serviceName}Models.g.cs"), generator.GenerateModels());
    File.WriteAllText(Path.Combine(outputDir, $"{serviceName}JsonSerializers.g.cs"), generator.GenerateSerializers());
    File.WriteAllText(Path.Combine(outputDir, $"{serviceName}OperationDispatcher.g.cs"), generator.GenerateDispatcher());

    Console.WriteLine($"    Generated models, serializers and dispatcher for {serviceName}");
}

static void GenerateJsonProtocolHandler(string modelPath, string serviceName, string modelNamespace, string outputDir, bool writeEmptyResponseLists = false)
{
    Console.WriteLine($"  Processing {serviceName} (JSON protocol)...");

    var json = File.ReadAllText(modelPath);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var operations = root.GetProperty("operations");
    var shapes = root.GetProperty("shapes");

    // The operation switch behind {Service}WireProtocol, which both hosts call. SQS speaks two
    // protocols, so its JSON switch has its own name and file.
    if (serviceName == "Sqs")
    {
        GenerateJsonDispatcher(serviceName, modelNamespace, operations, outputDir, "TryDispatchJsonAsync", "SqsWireProtocol.Json.g.cs");
    }
    else
    {
        GenerateJsonDispatcher(serviceName, modelNamespace, operations, outputDir, "TryDispatchAsync", $"{serviceName}WireProtocol.g.cs");
    }

    // Generate the serializers using the new generator
    var generator = new JsonCodeGenerator(serviceName, modelNamespace, shapes, writeEmptyResponseLists);
    var serializersCode = generator.GenerateSerializers(operations);

    var outputPath = Path.Combine(outputDir, $"{serviceName}JsonSerializers.g.cs");
    File.WriteAllText(outputPath, serializersCode);

    Console.WriteLine($"    Generated JSON protocol handler and serializers for {serviceName}");
}

static void GenerateJsonDispatcher(string serviceName, string modelNamespace, JsonElement operations, string outputDir, string methodName, string fileName)
{
    var code = new StringBuilder();
    code.AppendLine("// <auto-generated/>");
    code.AppendLine($"// Generated JSON protocol dispatcher for {serviceName}");
    code.AppendLine("#nullable enable");
    code.AppendLine();
    code.AppendLine("using System.Buffers;");
    code.AppendLine("using InMemoryAws.Http.Handlers;");
    code.AppendLine($"using {modelNamespace};");
    code.AppendLine();
    code.AppendLine("namespace InMemoryAws.Http;");
    code.AppendLine();
    code.AppendLine($"internal static partial class {serviceName}WireProtocol");
    code.AppendLine("{");
    code.AppendLine("    /// <summary>");
    code.AppendLine($"    /// Runs the {serviceName} operation named <paramref name=\"operation\"/> on the JSON <paramref name=\"body\"/> and");
    code.AppendLine($"    /// writes its JSON response. Returns false, writing nothing, when {serviceName} has no such operation.");
    code.AppendLine("    /// </summary>");
    code.AppendLine($"    private static async ValueTask<bool> {methodName}(");
    code.AppendLine("        string operation,");
    code.AppendLine("        ReadOnlyMemory<byte> body,");
    code.AppendLine($"        Internal{serviceName}Client client,");
    code.AppendLine("        IBufferWriter<byte> output,");
    code.AppendLine("        CancellationToken cancellationToken)");
    code.AppendLine("    {");
    code.AppendLine("        switch (operation)");
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
        code.AppendLine($"                var request = {serviceName}JsonSerializers.Deserialize{opName}Request(body);");
        code.AppendLine($"                var response = await client.{opName}Async(request, cancellationToken).ConfigureAwait(false);");
        code.AppendLine($"                {serviceName}JsonSerializers.Serialize{opName}Response(response, output);");
        code.AppendLine("                return true;");
        code.AppendLine("            }");
    }

    code.AppendLine("            default:");
    code.AppendLine("                return false;");
    code.AppendLine("        }");
    code.AppendLine("    }");
    code.AppendLine("}");

    File.WriteAllText(Path.Combine(outputDir, fileName), code.ToString());
}

static void GenerateSqsQueryProtocol(string modelPath, string modelNamespace, string outputDir)
{
    Console.WriteLine("  Processing Sqs (Query protocol)...");

    var json = File.ReadAllText(modelPath);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var operations = root.GetProperty("operations");

    // The JSON model carries no xmlNamespace; this is the one SQS has always answered Query in.
    var generator = new SqsQueryCodeGenerator(modelNamespace, "http://queue.amazonaws.com/doc/2012-11-05/", root.GetProperty("shapes"));
    File.WriteAllText(Path.Combine(outputDir, "SqsQuerySerializers.g.cs"), generator.GenerateSerializers(operations));
    File.WriteAllText(Path.Combine(outputDir, "SqsWireProtocol.Query.g.cs"), SqsQueryCodeGenerator.GenerateDispatcher(operations));

    Console.WriteLine("    Generated Query protocol dispatcher and serializers for Sqs");
}

static void GenerateQueryProtocolHandler(string modelPath, string serviceName, string modelNamespace, string outputDir)
{
    Console.WriteLine($"  Processing {serviceName} (Query protocol)...");

    var json = File.ReadAllText(modelPath);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var metadata = root.GetProperty("metadata");
    var operations = root.GetProperty("operations");
    var shapes = root.GetProperty("shapes");
    var xmlNamespace = metadata.GetProperty("xmlNamespace").GetString() ?? "";

    // The operation switch behind {Service}WireProtocol.HandleAsync, which both hosts call
    GenerateQueryDispatcher(serviceName, modelNamespace, operations, outputDir);

    // Generate the serializers using the new generator
    var generator = new QueryCodeGenerator(serviceName, modelNamespace, xmlNamespace, shapes);
    var serializersCode = generator.GenerateSerializers(operations);

    var outputPath = Path.Combine(outputDir, $"{serviceName}QuerySerializers.g.cs");
    File.WriteAllText(outputPath, serializersCode);

    Console.WriteLine($"    Generated Query protocol handler and serializers for {serviceName}");
}

static void GenerateQueryDispatcher(string serviceName, string modelNamespace, JsonElement operations, string outputDir)
{
    var code = new StringBuilder();
    code.AppendLine("// <auto-generated/>");
    code.AppendLine($"// Generated AWS Query protocol dispatcher for {serviceName}");
    code.AppendLine("#nullable enable");
    code.AppendLine();
    code.AppendLine("using System.Buffers;");
    code.AppendLine("using InMemoryAws.Http.Handlers;");
    code.AppendLine($"using {modelNamespace};");
    code.AppendLine();
    code.AppendLine("namespace InMemoryAws.Http;");
    code.AppendLine();
    code.AppendLine($"internal static partial class {serviceName}WireProtocol");
    code.AppendLine("{");
    code.AppendLine("    /// <summary>");
    code.AppendLine($"    /// Runs the {serviceName} operation named <paramref name=\"action\"/> and writes its XML response.");
    code.AppendLine($"    /// Returns false, writing nothing, when {serviceName} has no such operation.");
    code.AppendLine("    /// </summary>");
    code.AppendLine("    private static async ValueTask<bool> TryDispatchAsync(");
    code.AppendLine("        string action,");
    code.AppendLine("        ReadOnlyMemory<byte> parameters,");
    code.AppendLine($"        Internal{serviceName}Client client,");
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
        code.AppendLine($"                var request = {serviceName}QuerySerializers.Deserialize{opName}Request(parameters.Span);");
        code.AppendLine($"                var response = await client.{opName}Async(request, cancellationToken).ConfigureAwait(false);");
        code.AppendLine($"                {serviceName}QuerySerializers.Serialize{opName}Response(response, output);");
        code.AppendLine("                return true;");
        code.AppendLine("            }");
    }

    code.AppendLine("            default:");
    code.AppendLine("                return false;");
    code.AppendLine("        }");
    code.AppendLine("    }");
    code.AppendLine("}");

    File.WriteAllText(Path.Combine(outputDir, $"{serviceName}WireProtocol.g.cs"), code.ToString());
}

static void GenerateInternalTypes(string modelPath, string serviceName, string modelNamespace, string outputDir)
{
    Console.WriteLine($"  Generating internal types for {serviceName}...");

    var json = File.ReadAllText(modelPath);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var operations = root.GetProperty("operations");
    var shapes = root.GetProperty("shapes");

    var generator = new InternalTypeGenerator(serviceName, modelNamespace, shapes, operations);
    var code = generator.Generate();

    var outputPath2 = Path.Combine(outputDir, $"{serviceName}Models.g.cs");
    File.WriteAllText(outputPath2, code);

    Console.WriteLine($"    Generated internal types for {serviceName}");
}
