using Amazon.SQS;
using SnsSrcGen;

if (args.Length < 1)
{
    args = ["./output"];
}

var outputDir = args[0];

if (!Directory.Exists(outputDir))
{
    Directory.CreateDirectory(outputDir);
}

foreach (var file in Directory.EnumerateFiles(outputDir, "*.cs"))
{
    File.Delete(file);
}

SnsCodeGenerator.GenerateCode(File.ReadAllText("sns-2010-03-31.api.json"), outputDir );
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

internal static class StringExtensions
{
    public static string ToLowerFirst(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;
        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}
