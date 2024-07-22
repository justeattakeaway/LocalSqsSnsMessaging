using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace LocalSqsSnsMessaging;

public partial class InMemorySqsClient
{
    Task<string> IAmazonSQS.AuthorizeS3ToSendMessageAsync(string queueUrl, string bucket) => throw new NotImplementedException();
    Endpoint IAmazonSQS.DetermineServiceOperationEndpoint(AmazonWebServiceRequest request) => throw new NotImplementedException();
}