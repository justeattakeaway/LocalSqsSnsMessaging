#pragma warning disable CA1308
using System.Text.Json;

namespace SnsSrcGen;

internal static class SnsCodeGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void GenerateCode(string schemaJson, string outputDirectory)
    {
        var schema = JsonSerializer.Deserialize<SnsApiSchema>(schemaJson, JsonOptions);

        // Generate helper class first
        GenerateHelperClass(outputDirectory);
        GenerateDictionaryExtensions(outputDirectory);

        // Generate writers for each response type
        foreach (var operation in schema!.Operations.Values)
        {
            if (operation.Output is not null)
            {
                var responseTypeName = operation.Name + "Response";

                var responseShape = schema.Shapes[operation.Output.Shape];
                GenerateWriterForShape(
                    responseTypeName,
                    responseShape,
                    schema.Shapes,
                    outputDirectory,
                    operation.Output.ResultWrapper);
            }
        }

        GenerateClientHandler(schema, outputDirectory);
    }

    private static void GenerateHelperClass(string outputDirectory)
    {
        var code = """

                   using System;
                   using System.IO;
                   using System.Xml;
                   using Amazon.Runtime;

                   namespace Amazon.SimpleNotificationService.Model
                   {
                       internal static class SnsXmlWriterHelpers
                       {
                           public static XmlWriterSettings DefaultSettings => new XmlWriterSettings 
                           { 
                               Indent = true,
                               IndentChars = "    ",
                               NamespaceHandling = NamespaceHandling.OmitDuplicates,
                               OmitXmlDeclaration = true,
                               Encoding = Encoding.UTF8
                           };
                   
                           public static void WriteResponseMetadata(Utf8XmlWriter writer, AmazonWebServiceResponse response)
                           {
                               writer.WriteStartElement("ResponseMetadata"u8);
                               writer.WriteElement("RequestId"u8, response.ResponseMetadata.RequestId);
                           }
                       }
                   }
                   """;

        File.WriteAllText(
            Path.Combine(outputDirectory, "SnsXmlWriterHelpers.cs"),
            code);
    }

    private static void GenerateDictionaryExtensions(string outputDirectory)
    {
        var code = """
                   using Microsoft.Extensions.Primitives;

                   internal static class DictionaryExtensions
                   {
                       public static Dictionary<string, TValue> ToFlatDictionary<TValue>(
                           this Dictionary<string, StringValues> source,
                           string prefix,
                           Func<StringValues, TValue> mapper)
                       {
                           var result = new Dictionary<string, TValue>();
                           var prefixWithDot = prefix + ".entry.";
                           var prefixLength = prefixWithDot.Length;
                   
                           // First pass - find and add all keys
                           foreach (var kvp in source)
                           {
                               var key = kvp.Key;
                               if (!key.StartsWith(prefixWithDot, StringComparison.Ordinal))
                                   continue;
                   
                               var afterPrefix = key.AsSpan(prefixLength);
                               var firstDot = afterPrefix.IndexOf('.');
                               if (firstDot == -1) continue;
                   
                               var typeSpan = afterPrefix[(firstDot + 1)..];
                               if (!typeSpan.Equals("key", StringComparison.Ordinal))
                                   continue;
                   
                               // We found a key entry, look for its matching value
                               var indexSpan = afterPrefix[..firstDot];
                               var valueKey = string.Concat(prefixWithDot, indexSpan.ToString(), ".value");
                           
                               if (source.TryGetValue(valueKey, out var valueEntry))
                               {
                                   result[kvp.Value.ToString()] = mapper(valueEntry);
                               }
                           }
                   
                           return result;
                       }
                   }
                   """;

        File.WriteAllText(
            Path.Combine(outputDirectory, "DictionaryExtensions.cs"),
            code);
    }

    private static void GenerateWriterForShape(string shapeName, Shape shape, Dictionary<string, Shape> shapes,
        string outputDirectory, string resultWrapper)
    {
        var builder = new StringBuilder();

        builder.AppendLine("""
                           using System.Buffers;
                           using System.Diagnostics;
                           using System.Runtime.CompilerServices;
                           using System.Collections;
                           using System.Collections.Generic;
                           using System.Globalization;
                           """);

        builder.AppendLine($"namespace Amazon.SimpleNotificationService.Model");
        builder.AppendLine("{");
        builder.AppendLine($"    public static class {shapeName}XmlWriter");
        builder.AppendLine("    {");

        // Generate Write method
        builder.AppendLine($$$"""
                              public static MemoryStream Write({{{shapeName}}} response)
                              {
                                  Debug.Assert(response is not null);
                                  var memoryStream = MemoryStreamFactory.GetStream(nameof({{{shapeName}}}));
                                  using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
                                  
                                  utf8XmlWriter.WriteStartElement("{{{shapeName}}}"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
                              """);

        // Write result wrapper if specified
        if (!string.IsNullOrEmpty(resultWrapper))
        {
            builder.AppendLine($$$"""
                                      utf8XmlWriter.WriteStartElement("{{{resultWrapper}}}"u8);
                                  """);
        }

        // Generate property writing code for each member
        if (shape.Members != null)
        {
            foreach (var member in shape.Members)
            {
                var memberShape = shapes[member.Value.Shape];
                GenerateMemberWriter(builder, "response", member.Key, member.Value, memberShape, shapes);
            }
        }

        // Close result wrapper if specified
        if (!string.IsNullOrEmpty(resultWrapper))
        {
            builder.AppendLine("""
                                   utf8XmlWriter.WriteEndElement(); // Result wrapper
                               """);
        }

        builder.AppendLine("""
                               
                               SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
                               
                               utf8XmlWriter.WriteEndElement(); // Response
                               utf8XmlWriter.Flush();
                               
                               memoryStream.Position = 0;
                               return memoryStream;
                           }
                           """);

        builder.AppendLine("    }"); // class
        builder.AppendLine("}"); // namespace

        File.WriteAllText(
            Path.Combine(outputDirectory, $"{shapeName}XmlWriter.cs"),
            builder.ToString());
    }

    private static void GenerateMemberWriter(StringBuilder builder, string instanceName, string memberName, ShapeMember member,
        Shape memberShape, Dictionary<string, Shape> shapes)
    {
        var isRequired = memberShape.Required != null && memberShape.Required.Contains(memberName);
        var propertyName = char.ToUpperInvariant(memberName[0]) + memberName[1..];

        if (!isRequired)
        {
            builder.AppendLine($$"""
                                     if ({{instanceName}}.{{propertyName}} != null)
                                     {
                                 """);
        }

        switch (memberShape.Type.ToLowerInvariant())
        {
            case "string":
                builder.AppendLine(
                    $$"""
                              utf8XmlWriter.WriteElement("{{memberName}}"u8, {{instanceName}}.{{propertyName}});
                      """);
                break;

            case "list":
                var listItemShape = shapes[memberShape.Member.Shape];
                builder.AppendLine($$"""
                                     utf8XmlWriter.WriteStartElement("{{memberName}}"u8);
                                     foreach (var item in {{instanceName}}.{{propertyName}})
                                     {
                                         utf8XmlWriter.WriteStartElement("member"u8);
                                     """);

                if (listItemShape.Type == "structure")
                {
                    foreach (var listMember in listItemShape.Members)
                    {
                        var listMemberShape = shapes[listMember.Value.Shape];
                        GenerateMemberWriter(builder, "item", listMember.Key, listMember.Value, listMemberShape, shapes);
                    }
                }
                else
                {
                    builder.AppendLine("""
                                       utf8XmlWriter.WriteString(Convert.ToString(item, CultureInfo.InvariantCulture)!);
                                       """);
                }

                builder.AppendLine("""
                                       utf8XmlWriter.WriteEndElement(); // member
                                   }
                                   utf8XmlWriter.WriteEndElement(); // collection
                                   """);
                break;

            case "map":
                var mapValueShape = shapes[memberShape.Value.Shape];
                builder.AppendLine($$"""
                                     utf8XmlWriter.WriteStartElement("{{memberName}}"u8);
                                     foreach (var kvp in {{instanceName}}.{{propertyName}})
                                     {
                                         utf8XmlWriter.WriteStartElement("entry"u8);
                                         utf8XmlWriter.WriteElement("key"u8, Convert.ToString(kvp.Key, CultureInfo.InvariantCulture)!);
                                         utf8XmlWriter.WriteElement("value"u8, Convert.ToString(kvp.Value, CultureInfo.InvariantCulture)!);
                                         utf8XmlWriter.WriteEndElement(); // entry
                                     }
                                     utf8XmlWriter.WriteEndElement(); // map
                                     """);
                break;

            default:
                builder.AppendLine($$"""
                                     utf8XmlWriter.WriteElement("{{memberName}}"u8, Convert.ToString({{instanceName}}.{{propertyName}}, CultureInfo.InvariantCulture)!);
                                     """);
                break;
        }

        if (!isRequired)
        {
            builder.AppendLine("    }");
        }
    }

    private static void GenerateClientHandler(SnsApiSchema schema, string outputDirectory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""
                           using System.Net;
                           using System.Net.Http.Headers;
                           using Amazon.SimpleNotificationService.Model;
                           using Microsoft.AspNetCore.WebUtilities;

                           namespace Amazon.SimpleNotificationService
                           {
                               public sealed class SnsClientHandler : HttpClientHandler
                               {
                                   private readonly IAmazonSimpleNotificationService _innerClient;
                               
                                   public SnsClientHandler(IAmazonSimpleNotificationService innerClient)
                                   {
                                       _innerClient = innerClient;
                                   }
                                   
                                   protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                                   {
                                       if (request.Content is ByteArrayContent byteArrayContent)
                                       {
                                           var contentString = await byteArrayContent.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                                           var query = QueryHelpers.ParseQuery(contentString);
                                           if (query.TryGetValue("Action", out var action))
                                           {
                                               switch (action)
                                               {
                           """);

        foreach (var operation in schema.Operations)
        {
            if (operation.Value.Output != null)
            {
                var xmlWriterName = operation.Value.Name + "ResponseXmlWriter";

                builder.AppendLine($$"""
                                     case "{{operation.Key}}":
                                     {
                                         var requestModel = new {{operation.Key}}Request();
                                         // Map query parameters to request properties based on schema
                                         {{GenerateRequestMapping(operation.Value.Input.Shape, schema.Shapes)}}
                                         var result = await _innerClient.{{operation.Key}}Async(requestModel, cancellationToken).ConfigureAwait(false);
                                         var content = {{xmlWriterName}}.Write(result);
                                         
                                         var response = new HttpResponseMessage(HttpStatusCode.OK)
                                         {
                                             Content = new StreamContent(content)
                                         };
                                         response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                                         return response;
                                     }
                                     """);
            }
        }

        builder.AppendLine("""
                                               }
                                           }
                                       }
                                       return new HttpResponseMessage(HttpStatusCode.NotFound);
                                   }
                               }
                           }
                           """);

        File.WriteAllText(
            Path.Combine(outputDirectory, "SnsClientHandler.cs"),
            builder.ToString());
    }

    private static string GenerateRequestMapping(string shapeName, Dictionary<string, Shape> shapes)
    {
        var shape = shapes[shapeName];
        var builder = new StringBuilder();

        foreach (var member in shape.Members)
        {
            var memberShape = shapes[member.Value.Shape];
            var propertyName = char.ToUpperInvariant(member.Key[0]) + member.Key[1..];

            switch (memberShape.Type?.ToLowerInvariant())
            {
                case "list":
                    var listItemShape = shapes[memberShape.Member.Shape];
                    if (listItemShape.Type?.ToLowerInvariant() == "structure")
                    {
                        builder.AppendLine($$"""
                                                 // Handle list of structures
                                                 {
                                                     var {{propertyName.ToLowerFirst()}}List = new List<{{GetShapeTypeName(memberShape.Member.Shape, shapes)}}>();
                                                     var prefix = "{{propertyName}}.Entry.";
                                                     var entries = query.Keys
                                                         .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                                                         .Select(k => k.Substring(prefix.Length))
                                                         .Select(k => k.Split('.')[0])
                                                         .Distinct()
                                                         .OrderBy(x => int.Parse(x))
                                                         .ToList();
                                             
                                                     foreach (var index in entries)
                                                     {
                                                         var item = new {{GetShapeTypeName(memberShape.Member.Shape, shapes)}}();
                                                         {{GenerateStructureMapping(memberShape.Member.Shape, shapes, $"{propertyName}.Entry.{{index}}.", "item")}}
                                                         {{propertyName.ToLowerFirst()}}List.Add(item);
                                                     }
                                             
                                                     if ({{propertyName.ToLowerFirst()}}List.Count > 0)
                                                     {
                                                         requestModel.{{propertyName}} = {{propertyName.ToLowerFirst()}}List;
                                                     }
                                                 }
                                             """);
                    }
                    else
                    {
                        builder.AppendLine($$"""
                                                 // Handle simple list
                                                 {
                                                     var {{propertyName.ToLowerFirst()}}List = new List<{{GetShapeTypeName(memberShape.Member.Shape, shapes)}}>();
                                                     var prefix = "{{propertyName}}.member.";
                                                     
                                                     foreach (var kvp in query.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)))
                                                     {
                                                         {{propertyName.ToLowerFirst()}}List.Add({{GenerateValueParserForShape(listItemShape, "kvp.Value.ToString()", shapes)}});
                                                     }
                                                     
                                                     if ({{propertyName.ToLowerFirst()}}List.Count > 0)
                                                     {
                                                         requestModel.{{propertyName}} = {{propertyName.ToLowerFirst()}}List;
                                                     }
                                                 }
                                             """);
                    }

                    break;

                case "structure":
                    builder.AppendLine($$"""
                                             // Handle structure type {{propertyName}}
                                             {
                                                 var has{{propertyName}}Fields = query.Keys.Any(k => k.StartsWith("{{propertyName}}.", StringComparison.Ordinal));
                                                 if (has{{propertyName}}Fields)
                                                 {
                                                     var {{propertyName.ToLowerFirst()}} = new {{GetShapeTypeName(member.Value.Shape, shapes)}}();
                                                     {{GenerateStructureMapping(member.Value.Shape, shapes, $"{propertyName}.", $"{propertyName.ToLowerFirst()}")}}
                                                     requestModel.{{propertyName}} = {{propertyName.ToLowerFirst()}};
                                                 }
                                             }
                                         """);
                    break;

                case "map":
                    var mapShapeName = memberShape.Value.Shape;
                    var mapValueShape = shapes[mapShapeName];
                    builder.AppendLine($$"""
                                             // Handle map type {{propertyName}}
                                             {
                                                 var {{propertyName.ToLowerFirst()}}Map = query.ToFlatDictionary(
                                                     "{{propertyName}}", 
                                                     v => {{GenerateValueParserForShape(mapValueShape, "v.ToString()", shapes, mapShapeName)}});
                                                 
                                                 if ({{propertyName.ToLowerFirst()}}Map.Count > 0)
                                                 {
                                                     requestModel.{{propertyName}} = {{propertyName.ToLowerFirst()}}Map;
                                                 }
                                             }
                                         """);
                    break;

                default:
                    builder.AppendLine($$"""
                                             if (query.TryGetValue("{{propertyName}}", out var {{propertyName.ToLowerFirst()}}Value))
                                             {
                                                 requestModel.{{propertyName}} = {{GenerateValueParserForShape(memberShape, $"{propertyName.ToLowerFirst()}Value.ToString()", shapes)}};
                                             }
                                         """);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string GenerateStructureMapping(string shapeName, Dictionary<string, Shape> shapes, string prefix,
        string targetVariable)
    {
        var shape = shapes[shapeName];
        var builder = new StringBuilder();

        if (shape.Members == null) return "";

        foreach (var member in shape.Members)
        {
            var memberShape = shapes[member.Value.Shape];
            var propertyName = char.ToUpperInvariant(member.Key[0]) + member.Key[1..];

            switch (memberShape.Type?.ToLowerInvariant())
            {
                case "list":
                    var listItemShape = shapes[memberShape.Member.Shape];
                    builder.AppendLine($$"""
                                             {
                                                 var {{propertyName.ToLowerFirst()}}List = new List<{{GetShapeTypeName(memberShape.Member.Shape, shapes)}}>();
                                                 var {{propertyName.ToLowerFirst()}}Entries = query.ToFlatDictionary($"{{prefix}}{{propertyName}}", v => v.ToString());
                                                 
                                                 foreach (var entry in {{propertyName.ToLowerFirst()}}Entries.Values)
                                                 {
                                                     {{propertyName.ToLowerFirst()}}List.Add({{GenerateValueParserForShape(listItemShape, "entry", shapes)}});
                                                 }
                                                 
                                                 if ({{propertyName.ToLowerFirst()}}List.Count > 0)
                                                 {
                                                     {{targetVariable}}.{{propertyName}} = {{propertyName.ToLowerFirst()}}List;
                                                 }
                                             }
                                         """);
                    break;

                case "structure":
                    builder.AppendLine($$"""
                                             {
                                                 var has{{propertyName}}Fields = query.Keys.Any(k => k.StartsWith($"{{prefix}}{{propertyName}}.", StringComparison.Ordinal));
                                                 if (has{{propertyName}}Fields)
                                                 {
                                                     var nested{{propertyName}} = new {{GetShapeTypeName(member.Value.Shape, shapes)}}();
                                                     {{GenerateStructureMapping(member.Value.Shape, shapes, $"{prefix}{propertyName}.", $"nested{propertyName}")}}
                                                     {{targetVariable}}.{{propertyName}} = nested{{propertyName}};
                                                 }
                                             }
                                         """);
                    break;

                default:
                    builder.AppendLine($$"""
                                             if (query.TryGetValue($"{{prefix}}{{propertyName}}", out var {{propertyName.ToLowerFirst()}}Value))
                                             {
                                                 {{targetVariable}}.{{propertyName}} = {{GenerateValueParserForShape(memberShape, $"{propertyName.ToLowerFirst()}Value.ToString()", shapes)}};
                                             }
                                         """);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string GetShapeTypeName(string shapeName, Dictionary<string, Shape> shapes)
    {
        var shape = shapes[shapeName];
        return shape.Type?.ToLowerInvariant() switch
        {
            "string" => "string",
            "integer" => "int",
            "boolean" => "bool",
            "double" => "double",
            "timestamp" => "DateTime",
            "structure" => shapeName,
            "list" => $"List<{GetShapeTypeName(shape.Member.Shape, shapes)}>",
            "map" => $"Dictionary<string, {GetShapeTypeName(shape.Value.Shape, shapes)}>",
            _ => "string"
        };
    }

    private static string GenerateValueParserForShape(Shape shape, string valueExpression,
        Dictionary<string, Shape> shapes, string? mapShapeName = null)
    {
        return shape.Type?.ToLowerInvariant() switch
        {
            "string" => valueExpression,
            "integer" or "long" => $"long.Parse({valueExpression}, CultureInfo.InvariantCulture)",
            "boolean" => $"bool.Parse({valueExpression})",
            "double" => $"double.Parse({valueExpression}, CultureInfo.InvariantCulture)",
            "timestamp" => $"DateTime.Parse({valueExpression}, CultureInfo.InvariantCulture)",
            "structure" => $"new {mapShapeName}({valueExpression})",
            _ => valueExpression
        };
    }
}