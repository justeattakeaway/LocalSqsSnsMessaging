using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

if (args.Length < 1)
{
    Console.WriteLine("Usage: SnsCodeGen.exe <output-directory>");
    return;
}

var outputDir = args[0];

SnsCodeGenerator.GenerateCode(outputDir);
SqsCodeGenerator.GenerateCode(outputDir);

static class SqsCodeGenerator
{
    public static void GenerateCode(string outputDirectory)
    {
        var sb = new StringBuilder();

        // Add using statements and class definition
        sb.AppendLine("""
                      using System.Net;
                      using System.Net.Http.Headers;
                      using System.Net.Http.Json;
                      using Amazon.Runtime;
                      using Amazon.SQS;
                      using Amazon.SQS.Model;

                      sealed class SqsClientHandler : HttpClientHandler
                      {
                          private readonly IAmazonSQS _innerClient;
                      
                          public SqsClientHandler(IAmazonSQS innerClient)
                          {
                              _innerClient = innerClient;
                          }
                          
                          private static HttpResponseMessage CreateErrorResponse(AmazonServiceException ex)
                          {
                              var statusCode = ex switch
                              {
                                  InvalidMessageContentsException => HttpStatusCode.BadRequest,
                                  BatchEntryIdsNotDistinctException => HttpStatusCode.BadRequest,
                                  BatchRequestTooLongException => HttpStatusCode.BadRequest,
                                  EmptyBatchRequestException => HttpStatusCode.BadRequest,
                                  InvalidAttributeNameException => HttpStatusCode.BadRequest,
                                  InvalidBatchEntryIdException => HttpStatusCode.BadRequest,
                                  InvalidSecurityException => HttpStatusCode.Forbidden,
                                  MessageNotInflightException => HttpStatusCode.BadRequest,
                                  OverLimitException => HttpStatusCode.TooManyRequests,
                                  PurgeQueueInProgressException => HttpStatusCode.Conflict,
                                  QueueDeletedRecentlyException => HttpStatusCode.Conflict,
                                  QueueDoesNotExistException => HttpStatusCode.NotFound,
                                  QueueNameExistsException => HttpStatusCode.Conflict,
                                  ReceiptHandleIsInvalidException => HttpStatusCode.BadRequest,
                                  TooManyEntriesInBatchRequestException => HttpStatusCode.BadRequest,
                                  UnsupportedOperationException => HttpStatusCode.BadRequest,
                                  _ => HttpStatusCode.InternalServerError
                              };
                          
                              return new HttpResponseMessage(statusCode)
                              {
                                  Content = JsonContent.Create(new ErrorResponse(ex.Message, ex.ErrorCode, ex.RequestId), jsonTypeInfo: SqsJsonSerializerContext.Default.ErrorResponse)
                              };
                          }
                      
                          protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                          {
                              if (request.Headers.TryGetValues("X-Amz-Target", out var values))
                              {
                                  var targetHeaderValue = values.FirstOrDefault(x => x.StartsWith("AmazonSQS.", StringComparison.Ordinal));
                                  if (targetHeaderValue != null)
                                  {
                                      var target = targetHeaderValue["AmazonSQS.".Length..];
                      
                                      switch (target)
                                      {
                      """);

        // Get all request types from Amazon.SQS.Model namespace
        var assembly = typeof(IAmazonSQS).Assembly;
        var requestTypes = assembly.GetTypes()
            .Where(t => t.Namespace == "Amazon.SQS.Model" && 
                       t.Name.EndsWith("Request", StringComparison.Ordinal) &&
                       !t.IsAbstract &&
                       t.BaseType == typeof(AmazonSQSRequest))
            .OrderBy(t => t.Name)
            .ToList();

        foreach (var requestType in requestTypes)
        {
            var actionName = requestType.Name.Replace("Request", "", StringComparison.Ordinal);
            var responseType = assembly.GetType($"Amazon.SQS.Model.{actionName}Response");
            
            if (responseType is not null)
            {
                sb.AppendLine($$"""
                                                     case "{{actionName}}":
                                                     {
                                                         try
                                                         {
                                                             var requestObject = await request.Content!.ReadFromJsonAsync<{{requestType.Name}}>(SqsJsonSerializerContext.Default.{{requestType.Name}}, cancellationToken).ConfigureAwait(false);
                                                             var result = await _innerClient.{{actionName}}Async(requestObject, cancellationToken).ConfigureAwait(false);
                                     
                                                             var response = new HttpResponseMessage(HttpStatusCode.OK)
                                                             {
                                                                 Content = new PooledJsonContent<{{responseType.Name.Replace("Request", "Response", StringComparison.Ordinal)}}>(result, SqsJsonSerializerContext.Default.{{responseType.Name.Replace("Request", "Response", StringComparison.Ordinal)}})
                                                             };
                                                             response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                                                             return response;
                                                         }
                                                         catch (AmazonServiceException ex)
                                                         {
                                                             return CreateErrorResponse(ex);
                                                         }
                                                     }

                                 """);
            }
        }

        // Close switch and method
        sb.AppendLine("""
                                      }
                                  }
                              }
                              
                              return new HttpResponseMessage(HttpStatusCode.NotFound)
                              {
                                  Content = JsonContent.Create(new ErrorResponse("Unknown SQS operation", "UnknownOperation", string.Empty), jsonTypeInfo: SqsJsonSerializerContext.Default.ErrorResponse)
                              };
                          }
                      }
                      
                      internal sealed record ErrorResponse(string Message, string ErrorCode, string RequestId);
                      """);
        
        var fileName = "SqsClientHandler.cs";
        File.WriteAllText(
            Path.Combine(outputDirectory, fileName),
            sb.ToString());

        Console.WriteLine($"Generated {fileName}");

        GenerateSqsJsonSerializerContext(outputDirectory, requestTypes);
    }

    private static void GenerateSqsJsonSerializerContext(string outputDirectory, List<Type> requestTypes)
    {
        var sb = new StringBuilder();

        // Add using statements and class definition
        sb.AppendLine("""
                      using System.Text.Json.Serialization;
                      using Amazon.SQS.Model;

                      """
        );

        foreach (var requestType in requestTypes)
        {
            sb.AppendLine($"""
                           [JsonSerializable(typeof({requestType.Name}))]  
                           [JsonSerializable(typeof({requestType.Name.Replace("Request", "Response", StringComparison.Ordinal)}))] 
                           """);
        }

        sb.AppendLine("""
                      [JsonSerializable(typeof(ErrorResponse))]
                      """);
        sb.AppendLine("""
                      internal sealed partial class SqsJsonSerializerContext : JsonSerializerContext;
                      """);

        var fileName = "SqsJsonSerializerContext.cs";
        File.WriteAllText(
            Path.Combine(outputDirectory, fileName),
            sb.ToString());

        Console.WriteLine($"Generated {fileName}");
    }
}

static class SnsCodeGenerator
{
    public static void GenerateCode(string outputDirectory)
    {
        var assembly = typeof(AmazonSimpleNotificationServiceClient).Assembly;

        // Generate helper class first
        GenerateHelperClass(outputDirectory);
        
        GenerateDictionaryExtensions(outputDirectory);

        // Find all SNS response types
        // var sdk = _loadContext.LoadFromAssemblyPath("/Users/stuart.lang/.nuget/packages/awssdk.core/3.7.400.36/lib/net8.0/AWSSDK.Core.dll");
        var baseType = typeof(Amazon.Runtime.AmazonWebServiceResponse);

        var responseTypes = assembly.GetTypes()
            .Where(t => IsSnsResponseType(t, baseType))
            .ToList();

        Console.WriteLine($"Found {responseTypes.Count} SNS response types");

        foreach (var type in responseTypes)
        {
            GenerateWriterForType(type, outputDirectory);
        }
        
        GenerateClientHandler(assembly, responseTypes, outputDirectory);
    }

    private static bool IsSnsResponseType(Type type, Type baseType)
    {
        if (!type.IsClass || type.IsAbstract)
            return false;

        var current = type;
        while (current != null)
        {
            if (current == baseType)
                return true;
            current = current.BaseType;
        }
        return false;
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

    private static void GenerateWriterForType(Type type, string outputDirectory)
    {
        var namespaceName = type.Namespace;
        var className = type.Name;
        var properties = GetSerializableProperties(type);

        var builder = new StringBuilder();
        builder.AppendLine("""
                           using System.Buffers;
                           using System.Diagnostics;
                           using System.Runtime.CompilerServices;
                           using System.Collections;
                           using System.Collections.Generic;
                           using System.Globalization;
                           """);

        builder.AppendLine($"namespace {namespaceName}");
        builder.AppendLine("{");
        builder.AppendLine($"    public static class {className}XmlWriter");
        builder.AppendLine("    {");

        // Generate the Write method
        builder.AppendLine($$"""
                             public static MemoryStream Write({{className}} response)
                             {
                                 Debug.Assert(response is not null);
                                 var memoryStream = MemoryStreamFactory.GetStream(nameof({{className}}));
                                 using var utf8XmlWriter = new Utf8XmlWriter((IBufferWriter<byte>)memoryStream);
                             
                                 utf8XmlWriter.WriteStartElement("{{className}}"u8, "https://sns.amazonaws.com/doc/2010-03-31/"u8);
                             
                                 utf8XmlWriter.WriteStartElement("{{className[..^"Response".Length]}}Result"u8);
                             """);

        List<string> isSetMethods = [];

        // Generate property writing code
        foreach (var prop in properties)
        {
            var propName = prop.Name;
            var propType = prop.PropertyType;

            // Check if IsSet method exists
            var isSetMethod = type.GetMethod($"IsSet{propName}",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Determine if we need a null check
            bool isNullableType = propType.IsClass ||
                                  Nullable.GetUnderlyingType(propType) != null ||
                                  (propType.IsValueType && propType.IsGenericType &&
                                   propType.GetGenericTypeDefinition() == typeof(Nullable<>));

            // Start the property check if needed
            if (isSetMethod != null)
            {
                isSetMethods.Add(propName);
                builder.AppendLine($$"""
                                     
                                                       if (IsSet{{propName}}(response))
                                                       {
                                     """);
            }
            else if (isNullableType)
            {
                builder.AppendLine($$"""
                                     
                                                       if (response.{{propName}} != null)
                                                       {
                                     """);
            }
            else
            {
                // For non-nullable types, no check needed
                builder.AppendLine();
            }

            // Check if type is a dictionary or implements IEnumerable<KeyValuePair<,>>
            bool isDictionary = propType.IsGenericType && (
                propType.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
                propType.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                (propType.GetInterfaces()
                    .Any(i => i.IsGenericType &&
                              i.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                              i.GetGenericArguments()[0].IsGenericType &&
                              i.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(KeyValuePair<,>))));

            if (isDictionary)
            {
                var kvpType = propType.GetGenericArguments();
                if (kvpType.Length != 2) continue; // Skip if not a proper key-value pair type

                builder.AppendLine($$"""
                                                            utf8XmlWriter.WriteStartElement("{{propName}}"u8);
                                                            foreach (var kvp in response.{{propName}})
                                                            {
                                                                utf8XmlWriter.WriteStartElement("entry"u8);
                                                                
                                                                utf8XmlWriter.WriteElement("key"u8, Convert.ToString(kvp.Key, CultureInfo.InvariantCulture)!);
                                                                utf8XmlWriter.WriteElement("value"u8, Convert.ToString(kvp.Value, CultureInfo.InvariantCulture)!);
                                                                
                                                                utf8XmlWriter.WriteEndElement(); // entry
                                                            }
                                                            utf8XmlWriter.WriteEndElement(); // {{propName}}
                                     """);
            }
            // Handle regular collections
            else if (typeof(IEnumerable).IsAssignableFrom(propType) && propType != typeof(string))
            {
                // Get the element type for collections
                Type elementType;
                if (propType.IsArray)
                {
                    elementType = propType.GetElementType()!;
                }
                else if (propType.IsGenericType)
                {
                    elementType = propType.GetGenericArguments()[0];
                }
                else
                {
                    elementType = typeof(object);
                }

                builder.AppendLine($$"""
                                                           utf8XmlWriter.WriteStartElement("{{propName}}"u8);
                                                           foreach (var item in response.{{propName}})
                                                           {
                                                               utf8XmlWriter.WriteStartElement("member"u8);
                                     """);

                if (elementType.IsPrimitive || elementType == typeof(string))
                {
                    builder.AppendLine("""
                                                              utf8XmlWriter.WriteElement(string.Empty, Convert.ToString(item, CultureInfo.InvariantCulture)!);
                                       """);
                }
                else
                {
                    // For complex types, generate property writing for each nested property
                    foreach (var itemProp in elementType.GetProperties())
                    {
                        var itemPropType = itemProp.PropertyType;
                        bool isItemPropNullable = itemPropType.IsClass ||
                                                  Nullable.GetUnderlyingType(itemPropType) != null ||
                                                  (itemPropType.IsValueType && itemPropType.IsGenericType &&
                                                   itemPropType.GetGenericTypeDefinition() == typeof(Nullable<>));

                        if (isItemPropNullable)
                        {
                            builder.AppendLine($$"""
                                                                if (item.{{itemProp.Name}} != null)
                                                                {
                                                                    utf8XmlWriter.WriteElement("{{itemProp.Name}}", Convert.ToString(item.{{itemProp.Name}}, CultureInfo.InvariantCulture)!);
                                                                }
                                                 """);
                        }
                        else
                        {
                            builder.AppendLine($$"""
                                                                utf8XmlWriter.WriteElement("{{itemProp.Name}}", Convert.ToString(item.{{itemProp.Name}}, CultureInfo.InvariantCulture)!);
                                                 """);
                        }
                    }
                }

                builder.AppendLine("""
                                                              utf8XmlWriter.WriteEndElement(); // member
                                                          }
                                                          utf8XmlWriter.WriteEndElement(); // collection element
                                   """);
            }
            else
            {
                // Handle non-collection properties
                builder.AppendLine($$"""
                                                           utf8XmlWriter.WriteElement("{{propName}}", Convert.ToString(response.{{propName}}, CultureInfo.InvariantCulture)!);
                                     """);
            }

            if (isSetMethod != null || isNullableType)
            {
                builder.AppendLine("""
                                                      }
                                   """);
            }
        }

        builder.AppendLine("""
                               
                                                     utf8XmlWriter.WriteEndElement(); // Result
                               
                                                     SnsXmlWriterHelpers.WriteResponseMetadata(utf8XmlWriter, response);
                               
                                                     utf8XmlWriter.WriteEndElement(); // Response
                                                     utf8XmlWriter.WriteEndElement();
                                                     utf8XmlWriter.Flush();
                                                     
                                                     memoryStream.Position = 0;
                               
                                                     return memoryStream;
                                                 }
                           """);

        foreach (var isSetMethod in isSetMethods)
        {
            builder.AppendLine($$"""
                                 
                                         [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(IsSet{{isSetMethod}}))]
                                         public static extern bool IsSet{{isSetMethod}}({{className}} response);
                                 """);
        }

        builder.AppendLine("    }"); // class
        builder.AppendLine("}"); // namespace

        var fileName = $"{className}XmlWriter.cs";
        File.WriteAllText(
            Path.Combine(outputDirectory, fileName),
            builder.ToString());

        Console.WriteLine($"Generated {fileName}");
    }

    private static void GenerateClientHandler(Assembly assembly, List<Type> responseTypes, string outputDirectory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""
                           using System.Diagnostics;
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
                                       Debug.Assert(request != null);
                                       if (request.Content is ByteArrayContent byteArrayContent)
                                       {
                                           var contentString = await byteArrayContent.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                                           var query = QueryHelpers.ParseQuery(contentString);
                                           if (query.TryGetValue("Action", out var action))
                                           {
                                               switch (action)
                                               {
                           """);

        // Generate cases for each request/response pair
        foreach (var responseType in responseTypes)
        {
            // Infer request type name from response type name
            var responseName = responseType.Name;
            Debug.Assert(responseName != null);
            if (!responseName.EndsWith("Response", StringComparison.Ordinal))
                continue;

            var requestName = responseName.Replace("Response", "Request", StringComparison.Ordinal);
            var requestType = assembly.GetType($"{responseType.Namespace}.{requestName}");
            if (requestType == null)
                continue;

            // Get the properties of the request type
            var requestProperties = requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType == requestType)
                .ToList();

            var actionName = responseName.Replace("Response", "", StringComparison.Ordinal);

            builder.AppendLine($$"""
                                 
                                                         case "{{actionName}}":
                                                         {
                                                             var {{actionName.ToLowerFirst()}}Request = new {{requestName}}
                                                             {
                                 """);

            // Add property assignments from query parameters
            foreach (var prop in requestProperties)
            {
                builder.AppendLine(GeneratePropertyAssignment(prop));
            }

            builder.AppendLine($$"""
                                                             };
                                                             var {{actionName.ToLowerFirst()}}Result = await _innerClient.{{actionName}}Async({{actionName.ToLowerFirst()}}Request, cancellationToken).ConfigureAwait(false);
                                                             var {{responseName.ToLowerFirst()}}Content = {{responseName}}XmlWriter.Write({{actionName.ToLowerFirst()}}Result); 
                                                             var {{responseName.ToLowerFirst()}} = new HttpResponseMessage(HttpStatusCode.OK)
                                                             {
                                                                 Content = new StreamContent({{responseName.ToLowerFirst()}}Content)
                                                             };
                                                             {{responseName.ToLowerFirst()}}.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                                                             return {{responseName.ToLowerFirst()}};
                                                         }
                                 """);
        }

        builder.AppendLine("""
                           
                                               }
                                           }
                                       }
                                       return new HttpResponseMessage();
                                   }
                               }
                           }
                           """);

        File.WriteAllText(
            Path.Combine(outputDirectory, "SnsClientHandler.cs"),
            builder.ToString());

        Console.WriteLine("Generated SnsClientHandler.cs");
    }

    private static string GeneratePropertyAssignment(PropertyInfo prop)
    {
        string GenerateNestedProperties(Type type, string prefix, string varPrefix)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType == type);

            return string.Join(",\n", properties.Select(p =>
            {
                var propType = p.PropertyType;
                var propName = p.Name;

                // Handle dictionaries
                var isDictionary = propType.IsGenericType &&
                                   propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                                   typeof(Dictionary<,>).AssemblyQualifiedName;

                if (isDictionary)
                {
                    var valueType = propType.GetGenericArguments()[1];
                    return
                        $$"""                        {{propName}} = query.ToFlatDictionary<{{valueType}}>($"{{prefix}}.{{propName}}", v => {{GenerateParseExpression("v", valueType)}})""";
                }

                // Handle collections
                var isCollection = propType.IsGenericType &&
                                   (propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                                    typeof(List<>).AssemblyQualifiedName ||
                                    propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                                    typeof(IList<>).AssemblyQualifiedName ||
                                    propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                                    typeof(ICollection<>).AssemblyQualifiedName);

                if (isCollection)
                {
                    var elementType = propType.GetGenericArguments()[0];

                    if (!elementType.IsPrimitive &&
                        elementType.AssemblyQualifiedName != typeof(string).AssemblyQualifiedName &&
                        !elementType.IsEnum)
                    {
                        return $$"""
                                 {{propName}} = query.Keys
                                    .Where(k => k.StartsWith($"{{prefix}}.{{propName}}.member.", StringComparison.Ordinal))
                                    .Select(k => int.Parse(k.Split('.')[2], CultureInfo.InvariantCulture))
                                    .Distinct()
                                    .Select(index => new {{elementType.Name }}
                                     {
                                         {
                                             {
                                                 GenerateNestedProperties(elementType, $"{prefix}.{propName}.member.{index}",
                                                     $"{varPrefix}{propName}")
                                             }
                                         }
                                     })
                                     .ToList()
                                 """;
                    }

                    return
                        $$"""
                          {{propName}} = query.TryGetValue($"{{prefix}}.{{propName}}.member", out var {{varPrefix}}{{propName}}Values) ? 
                          {
                              {
                                  varPrefix
                              }
                          }
                          {
                              {
                                  propName
                              }
                          }
                          Values.Select < string, {
                              {
                                  elementType
                              }
                          }?>((string v) =>
                          {
                              {
                                  GenerateParseExpression("v", elementType)
                              }
                          }).ToList() : null
                          """;
                }

                // Handle complex types
                if (!propType.IsPrimitive &&
                    propType.AssemblyQualifiedName != typeof(string).AssemblyQualifiedName &&
                    !propType.IsEnum)
                {
                    var nestedProperties = propType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.DeclaringType == propType);

                    if (nestedProperties.Any())
                    {
                        return 
                            $$"""
                                {{propName}} = new {{propType.Name}}
                                {
                                    {
                                        {
                                            GenerateNestedProperties(propType, $"{prefix}.{propName}", $"{varPrefix}{propName}")
                                        }
                                    }
                                }
                                """;
                    }
                }

                return
                    $$"""
                      {{propName}} = query.TryGetValue($"{{prefix}}.{{propName}}", out var {{varPrefix}}{{propName}}) ? 
                      {{
                          GenerateParseExpression($"{varPrefix}{propName}", propType)
                      }} : default
                      """;
            }));
        }

        var propType = prop.PropertyType;
        var propName = prop.Name;

        // Handle top-level dictionaries
        var isDictionary = propType.IsGenericType &&
                           propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                           typeof(Dictionary<,>).AssemblyQualifiedName;

        if (isDictionary)
        {
            var valueType = propType.GetGenericArguments()[1];
            return $$"""
                                                     {{propName}} = query.ToFlatDictionary<{{valueType}}>("{{propName}}", v => {{GenerateParseExpression("v", valueType)}}),
                     """;
        }

        // Handle top-level collections
        var isCollection = propType.IsGenericType &&
                           (propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                            typeof(List<>).AssemblyQualifiedName ||
                            propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                            typeof(IList<>).AssemblyQualifiedName ||
                            propType.GetGenericTypeDefinition().AssemblyQualifiedName ==
                            typeof(ICollection<>).AssemblyQualifiedName);

        if (isCollection)
        {
            var elementType = propType.GetGenericArguments()[0];

            if (!elementType.IsPrimitive &&
                elementType.AssemblyQualifiedName != typeof(string).AssemblyQualifiedName &&
                !elementType.IsEnum)
            {
                return $$"""
                                                     {{propName}} = query.Keys
                                                         .Where(k => k.StartsWith("{{propName}}.member.", StringComparison.Ordinal))
                                                         .Select(k => int.Parse(k.Split('.')[2], CultureInfo.InvariantCulture))
                                                         .Distinct()
                                                         .Select(index => new {{elementType.Name}}
                                                         {
                                                             {{GenerateNestedProperties(elementType, $"{propName}.member.{{index}}", propName.ToLowerFirst())}}
                                                         })
                                                         .ToList(),
                         """;
            }

            return $$"""
                                                     {{propName}} = query.TryGetValue("{{propName}}.member", out var {{propName.ToLowerFirst()}}Values) ? 
                                                         {{propName.ToLowerFirst()}}Values.Select<string, {{elementType}}?>((string v) => {{GenerateParseExpression("v", elementType)}}).ToList() : null,
                     """;
        }

        // Handle top-level complex types
        if (!propType.IsPrimitive &&
            propType.AssemblyQualifiedName != typeof(string).AssemblyQualifiedName &&
            !propType.IsEnum)
        {
            var nestedProperties = propType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType == propType);

            if (nestedProperties.Any())
            {
                return $$"""
                                                     {{propName}} = new {{propType.Name}}
                                                     {
                                                         {{GenerateNestedProperties(propType, propName, propName.ToLowerFirst())}}
                                                     },
                         """;
            }
        }

        return $$"""
                                                 {{propName}} = query.TryGetValue("{{propName}}", out var {{propName.ToLowerFirst()}}) ? 
                                                     {{GenerateParseExpression($"{propName.ToLowerFirst()}", propType)}} : default!,
                 """;
    }

    private static string GenerateParseExpression(string valueVar, Type type)
    {
        if (type.AssemblyQualifiedName == typeof(string).AssemblyQualifiedName)
            return $"{valueVar}.ToString()";
        if (type.AssemblyQualifiedName == typeof(int).AssemblyQualifiedName)
            return $"int.Parse({valueVar}.ToString(), CultureInfo.InvariantCulture)";
        if (type.AssemblyQualifiedName == typeof(long).AssemblyQualifiedName)
            return $"long.Parse({valueVar}.ToString(), CultureInfo.InvariantCulture)";
        if (type.AssemblyQualifiedName == typeof(bool).AssemblyQualifiedName)
            return $"bool.Parse({valueVar}.ToString())";
        if (type.AssemblyQualifiedName == typeof(double).AssemblyQualifiedName)
            return $"double.Parse({valueVar}.ToString(), CultureInfo.InvariantCulture)";
        if (type.AssemblyQualifiedName == typeof(decimal).AssemblyQualifiedName)
            return $"decimal.Parse({valueVar}.ToString(), CultureInfo.InvariantCulture)";
        if (type.AssemblyQualifiedName == typeof(DateTime).AssemblyQualifiedName)
            return $"DateTime.Parse({valueVar}.ToString(), CultureInfo.InvariantCulture)";
        if (type.IsGenericType && type.GetGenericTypeDefinition().AssemblyQualifiedName == typeof(List<>).AssemblyQualifiedName)
            return $"{valueVar}.Select<string, {type.GenericTypeArguments.First()}>((string v) => {GenerateParseExpression("v", type.GenericTypeArguments.First())}).ToList()";
        if (type.IsGenericType && type.GetGenericTypeDefinition().AssemblyQualifiedName == typeof(Dictionary<,>).AssemblyQualifiedName)
        {
            var keyType = type.GenericTypeArguments[0];
            var valueType = type.GenericTypeArguments[1];
            return $"{valueVar}.Select<string, {valueType}>((string v) => v.Split('=')).ToDictionary<string[], {keyType}, {valueType}>(kv => {GenerateParseExpression("kv[0]", keyType)}, kv => {GenerateParseExpression("kv[1]", valueType)})";
        }
        
        return "default!";
    }

    private static IEnumerable<PropertyInfo> GetSerializableProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.DeclaringType == type);
    }
}

internal static class StringExtensions
{
    public static string ToLowerFirst(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;
        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}
