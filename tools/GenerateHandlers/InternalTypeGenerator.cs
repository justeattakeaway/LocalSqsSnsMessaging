using System.Text;
using System.Text.Json;

namespace GenerateHandlers;

/// <summary>
/// Generates internal POCO types from AWS service model JSON shapes.
/// These types replace the AWS SDK model types for use in the shared business logic.
/// </summary>
internal sealed class InternalTypeGenerator
{
    private readonly string _serviceName;
    private readonly string _namespace;
    private readonly JsonElement _shapes;
    private readonly JsonElement _operations;

    // Maps model shape name → C# class name for operation types
    private readonly Dictionary<string, string> _shapeToClassName = new();

    // Set of shape names that are error types (skip these)
    private readonly HashSet<string> _errorShapes = new();

    // Set of all shapes we need to generate
    private readonly HashSet<string> _shapesToGenerate = new();

    public InternalTypeGenerator(string serviceName, string modelNamespace, JsonElement shapes, JsonElement operations)
    {
        _serviceName = serviceName;
        _namespace = modelNamespace;
        _shapes = shapes;
        _operations = operations;
    }

    public string Generate()
    {
        // 1. Identify error shapes (skip these - we have hand-written exceptions)
        IdentifyErrorShapes();

        // 2. Build mapping from model shape names to C# class names for operation types
        BuildOperationTypeMapping();

        // 3. Collect all shapes transitively referenced by operations
        CollectReferencedShapes();

        // 4. Generate code
        return GenerateCode();
    }

    private void IdentifyErrorShapes()
    {
        foreach (var shape in _shapes.EnumerateObject())
        {
            if (shape.Value.TryGetProperty("exception", out var exc) && exc.GetBoolean())
            {
                _errorShapes.Add(shape.Name);
            }
            else if (shape.Value.TryGetProperty("error", out _))
            {
                _errorShapes.Add(shape.Name);
            }
        }

        // Also identify error shapes referenced by operations
        foreach (var operation in _operations.EnumerateObject())
        {
            if (operation.Value.TryGetProperty("errors", out var errors))
            {
                foreach (var error in errors.EnumerateArray())
                {
                    var shapeName = error.GetProperty("shape").GetString()!;
                    _errorShapes.Add(shapeName);
                }
            }
        }
    }

    private void BuildOperationTypeMapping()
    {
        foreach (var operation in _operations.EnumerateObject())
        {
            var opName = operation.Name;

            if (operation.Value.TryGetProperty("input", out var input))
            {
                var inputShapeName = input.GetProperty("shape").GetString()!;
                var requestClassName = $"{opName}Request";
                _shapeToClassName[inputShapeName] = requestClassName;
            }

            if (operation.Value.TryGetProperty("output", out var output))
            {
                var outputShapeName = output.GetProperty("shape").GetString()!;
                var responseClassName = $"{opName}Response";
                _shapeToClassName[outputShapeName] = responseClassName;
            }
        }
    }

    private void CollectReferencedShapes()
    {
        // Start from all operation input/output shapes
        foreach (var operation in _operations.EnumerateObject())
        {
            if (operation.Value.TryGetProperty("input", out var input))
            {
                var shapeName = input.GetProperty("shape").GetString()!;
                CollectShapeRecursive(shapeName);
            }

            if (operation.Value.TryGetProperty("output", out var output))
            {
                var shapeName = output.GetProperty("shape").GetString()!;
                CollectShapeRecursive(shapeName);
            }
        }
    }

    private void CollectShapeRecursive(string shapeName)
    {
        if (_errorShapes.Contains(shapeName))
            return;

        if (!_shapes.TryGetProperty(shapeName, out var shape))
            return;

        var shapeType = shape.GetProperty("type").GetString()!;

        if (shapeType == "structure")
        {
            if (!_shapesToGenerate.Add(shapeName))
                return; // Already visited

            // Recurse into members
            if (shape.TryGetProperty("members", out var members))
            {
                foreach (var member in members.EnumerateObject())
                {
                    var memberShapeName = member.Value.GetProperty("shape").GetString()!;
                    CollectShapeRecursive(memberShapeName);
                }
            }
        }
        else if (shapeType == "list")
        {
            var memberShapeName = shape.GetProperty("member").GetProperty("shape").GetString()!;
            CollectShapeRecursive(memberShapeName);
        }
        else if (shapeType == "map")
        {
            var keyShapeName = shape.GetProperty("key").GetProperty("shape").GetString()!;
            var valueShapeName = shape.GetProperty("value").GetProperty("shape").GetString()!;
            CollectShapeRecursive(keyShapeName);
            CollectShapeRecursive(valueShapeName);
        }
    }

    private string GenerateCode()
    {
        var code = new StringBuilder();

        code.AppendLine("// <auto-generated/>");
        code.AppendLine($"// Generated internal POCO types for {_serviceName} from AWS service model");
        code.AppendLine("#nullable enable");
        code.AppendLine();
        code.AppendLine($"namespace {_namespace};");
        code.AppendLine();

        // Determine which shapes are response types (need base class)
        var responseShapes = new HashSet<string>();
        foreach (var operation in _operations.EnumerateObject())
        {
            if (operation.Value.TryGetProperty("output", out var output))
            {
                responseShapes.Add(output.GetProperty("shape").GetString()!);
            }
        }

        // Also track void operations that need empty response classes
        var voidOperations = new List<string>();
        foreach (var operation in _operations.EnumerateObject())
        {
            if (operation.Value.TryGetProperty("input", out _) && !operation.Value.TryGetProperty("output", out _))
            {
                voidOperations.Add(operation.Name);
            }
        }

        // Generate POCO classes for each shape, sorted by name for deterministic output
        foreach (var shapeName in _shapesToGenerate.OrderBy(s => GetClassName(s)))
        {
            var shape = _shapes.GetProperty(shapeName);
            var className = GetClassName(shapeName);
            var isResponse = responseShapes.Contains(shapeName);

            GenerateClass(code, className, shape, isResponse);
        }

        // Generate empty response classes for void operations
        foreach (var opName in voidOperations.OrderBy(s => s))
        {
            var className = $"{opName}Response";
            code.AppendLine($"internal class {className} : AmazonWebServiceResponse {{ }}");
            code.AppendLine();
        }

        return code.ToString();
    }

    private void GenerateClass(StringBuilder code, string className, JsonElement shape, bool isResponse)
    {
        var baseClass = isResponse ? " : AmazonWebServiceResponse" : "";
        code.AppendLine($"internal class {className}{baseClass}");
        code.AppendLine("{");

        if (shape.TryGetProperty("members", out var members))
        {
            foreach (var member in members.EnumerateObject())
            {
                var memberName = member.Name;
                var csharpPropertyName = ToPascalCase(memberName);
                var memberShapeName = member.Value.GetProperty("shape").GetString()!;
                var csharpType = GetCSharpPropertyType(memberShapeName);

                // Initialize collection properties to empty collections (matching AWS SDK behavior)
                var initializer = csharpType.StartsWith("List<", StringComparison.Ordinal) || csharpType.StartsWith("Dictionary<", StringComparison.Ordinal)
                    ? " = [];"
                    : "";

                if (initializer.Length > 0)
                    code.AppendLine($"    public {csharpType} {csharpPropertyName} {{ get; set; }}{initializer}");
                else
                    code.AppendLine($"    public {csharpType} {csharpPropertyName} {{ get; set; }}");
            }
        }

        code.AppendLine("}");
        code.AppendLine();
    }

    private string GetCSharpPropertyType(string shapeName)
    {
        if (!_shapes.TryGetProperty(shapeName, out var shape))
            return "object";

        var shapeType = shape.GetProperty("type").GetString()!;

        return shapeType switch
        {
            "string" => "string?",
            "integer" => "int?",
            "long" => "long?",
            "boolean" => "bool?",
            "double" => "double?",
            "float" => "float?",
            "timestamp" => "DateTime?",
            "blob" => "MemoryStream?",
            "list" => GetListType(shape),
            "map" => GetMapType(shape),
            "structure" => GetClassName(shapeName) + "?",
            _ => "object"
        };
    }

    private string GetListType(JsonElement shape)
    {
        var memberShapeName = shape.GetProperty("member").GetProperty("shape").GetString()!;
        var memberType = GetCSharpElementType(memberShapeName);
        return $"List<{memberType}>?";
    }

    private string GetMapType(JsonElement shape)
    {
        var keyShapeName = shape.GetProperty("key").GetProperty("shape").GetString()!;
        var valueShapeName = shape.GetProperty("value").GetProperty("shape").GetString()!;
        var keyType = GetCSharpElementType(keyShapeName);
        var valueType = GetCSharpElementType(valueShapeName);
        return $"Dictionary<{keyType}, {valueType}>?";
    }

    /// <summary>
    /// Gets the C# type for use as a collection element (non-nullable for value types used in collections).
    /// </summary>
    private string GetCSharpElementType(string shapeName)
    {
        if (!_shapes.TryGetProperty(shapeName, out var shape))
            return "object";

        var shapeType = shape.GetProperty("type").GetString()!;

        return shapeType switch
        {
            "string" => "string",
            "integer" => "int",
            "long" => "long",
            "boolean" => "bool",
            "double" => "double",
            "float" => "float",
            "timestamp" => "DateTime",
            "blob" => "MemoryStream",
            "structure" => GetClassName(shapeName),
            _ => "object"
        };
    }

    private string GetClassName(string shapeName)
    {
        return _shapeToClassName.TryGetValue(shapeName, out var className) ? className : shapeName;
    }

    private static string ToPascalCase(string str)
    {
        if (string.IsNullOrEmpty(str) || char.IsUpper(str[0]))
            return str;
        return char.ToUpperInvariant(str[0]) + str.Substring(1);
    }
}
