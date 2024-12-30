#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable CA2227
#pragma warning disable CA1002
#pragma warning disable CA1716
#pragma warning disable CA1056
#pragma warning disable CA1724
namespace SnsSrcGen;

public class SnsApiSchema
{
    public string Version { get; set; }
    public SchemaMetadata Metadata { get; set; }
    public Dictionary<string, Operation> Operations { get; set; }
    public Dictionary<string, Shape> Shapes { get; set; }
}

public class SchemaMetadata
{
    public string ApiVersion { get; set; }
    public string Protocol { get; set; }
    public string ServiceFullName { get; set; }
    public string XmlNamespace { get; set; }
}

public class Operation
{
    public string Name { get; set; }
    public Http Http { get; set; }
    public ShapeReference Input { get; set; }
    public ShapeReference Output { get; set; }
    public List<ShapeReference> Errors { get; set; }
}

public class Http
{
    public string Method { get; set; }
    public string RequestUri { get; set; }
}

public class ShapeReference
{
    public string Shape { get; set; }
    public string ResultWrapper { get; set; }
}

public class Shape
{
    public string Type { get; set; }
    public Dictionary<string, ShapeMember> Members { get; set; }
    public ShapeMember Member { get; set; }
    public List<string> Required { get; set; }
    public ShapeMember Key { get; set; }
    public ShapeMember Value { get; set; }
    public int? Max { get; set; }
    public int? Min { get; set; }
    public string Pattern { get; set; }
    public List<string> Enum { get; set; }
}

public class ShapeMember
{
    public string Shape { get; set; }
    public string LocationName { get; set; }
}

public class Error
{
    public string Code { get; set; }
    public int HttpStatusCode { get; set; }
    public bool SenderFault { get; set; }
}