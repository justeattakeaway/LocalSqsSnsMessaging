using Amazon.Runtime;
using Amazon.SQS;
using InMemoryAws;

var fixture = new Shared.Fixture();
using var sqs = fixture.Cloud.CreateSqsClient();
var queueUrl = (await sqs.CreateQueueAsync("orders")).QueueUrl;
await sqs.SendMessageAsync(queueUrl, "placed");

using var appClient = new AmazonSQSClient(
    new AnonymousAWSCredentials(),
    new AmazonSQSConfig { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, HttpClientFactory = fixture.CreateHttpClientFactory() });

Console.WriteLine($"{queueUrl} {(await appClient.ReceiveMessageAsync(queueUrl)).Messages[0].Body}");
