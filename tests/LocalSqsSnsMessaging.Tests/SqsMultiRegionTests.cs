using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// SQS queues are regional: a request is served from the queues of the region it was made in
/// (its SigV4 credential scope, else its endpoint host), and <see cref="InMemoryAwsBus.CurrentRegion"/>
/// when it names none. The evaluation suite's <c>FidelityGap.MultiRegion</c> checks cover the SQS
/// API itself; these cover how the region is read and the deliveries that address a queue by ARN.
/// </summary>
public sealed class SqsMultiRegionTests
{
    private const string AltRegion = "eu-west-1";

    /// <summary>An SDK client over the bus, signing with <paramref name="region"/> for <paramref name="serviceUrl"/>.</summary>
    private static AmazonSQSClient SqsClientIn(InMemoryAwsBus bus, string region, string? serviceUrl = null, AWSCredentials? credentials = null) =>
        new(credentials ?? new BasicAWSCredentials("test", "test"), new AmazonSQSConfig
        {
            ServiceURL = serviceUrl ?? $"https://sqs.{region}.amazonaws.com",
            AuthenticationRegion = region,
            MaxErrorRetry = 0,
#pragma warning disable CA2000 // The SDK client owns the HttpClient factory and its handler.
            HttpClientFactory = new InMemoryHttpClientFactory(new InMemoryAwsHttpMessageHandler(bus, AwsServiceType.Sqs)),
#pragma warning restore CA2000
        });

    [Test]
    public void RegionComesFromTheCredentialScopeBeforeTheHost()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://sqs.us-east-1.amazonaws.com/");
        request.Headers.TryAddWithoutValidation("Authorization",
            "AWS4-HMAC-SHA256 Credential=test/20240101/eu-west-2/sqs/aws4_request, SignedHeaders=host, Signature=abc");

        AwsRequestRegion.FromRequest(request).ShouldBe("eu-west-2");
    }

    [Test]
    [Arguments("https://sqs.ap-southeast-2.amazonaws.com/", "ap-southeast-2")]
    [Arguments("https://sqs.cn-north-1.amazonaws.com.cn/", "cn-north-1")]
    [Arguments("https://us-west-1.queue.amazonaws.com/", "us-west-1")]
    [Arguments("https://sqs.us-east-1.amazonaws.com/?X-Amz-Credential=test%2F20240101%2Fca-central-1%2Fsqs%2Faws4_request", "ca-central-1")]
    [Arguments("http://localhost:4566/", null)]
    [Arguments("https://sqs.amazonaws.com/", null)]
    public void UnsignedRequestsTakeTheRegionFromTheEndpoint(string endpoint, string? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint));

        AwsRequestRegion.FromRequest(request).ShouldBe(expected);
    }

    [Test]
    public async Task ARequestNamingNoRegionIsServedFromTheCurrentRegion()
    {
        var bus = new InMemoryAwsBus { CurrentRegion = "us-west-2" };
        // Unsigned, so no credential scope, against a custom endpoint that names no region.
        using var local = SqsClientIn(bus, AltRegion, "http://localhost:4566", new AnonymousAWSCredentials());
        using var home = bus.CreateSqsClient();

        var url = (await local.CreateQueueAsync("no-region-named")).QueueUrl;

        (await home.GetQueueUrlAsync("no-region-named")).QueueUrl.ShouldBe(url);
        bus.Queues.ShouldContainKey("no-region-named");
    }

    [Test]
    public async Task AClientSigningForAnotherRegionGetsThatRegionsQueues()
    {
        var bus = new InMemoryAwsBus();
        using var home = bus.CreateSqsClient();
        using var alt = SqsClientIn(bus, AltRegion);

        var altUrl = (await alt.CreateQueueAsync("orders")).QueueUrl;
        var homeUrl = (await home.CreateQueueAsync("orders")).QueueUrl;

        altUrl.ShouldBe($"https://sqs.{AltRegion}.amazonaws.com/{bus.CurrentAccountId}/orders");
        homeUrl.ShouldBe($"https://sqs.{bus.CurrentRegion}.amazonaws.com/{bus.CurrentAccountId}/orders");
        var altArn = (await alt.GetQueueAttributesAsync(altUrl, ["QueueArn"])).QueueARN;
        altArn.ShouldBe($"arn:aws:sqs:{AltRegion}:{bus.CurrentAccountId}:orders");
        (await home.ListQueuesAsync(new ListQueuesRequest())).QueueUrls.ShouldBe([homeUrl]);
        (await alt.ListQueuesAsync(new ListQueuesRequest())).QueueUrls.ShouldBe([altUrl]);
    }

    [Test]
    public async Task SnsDeliversToAQueueInAnotherRegionByItsArn()
    {
        var bus = new InMemoryAwsBus();
        using var sns = bus.CreateSnsClient();
        using var home = bus.CreateSqsClient();
        using var alt = SqsClientIn(bus, AltRegion);

        var homeUrl = (await home.CreateQueueAsync("fan-out")).QueueUrl;
        var altUrl = (await alt.CreateQueueAsync("fan-out")).QueueUrl;
        var altArn = (await alt.GetQueueAttributesAsync(altUrl, ["QueueArn"])).QueueARN;
        var topicArn = (await sns.CreateTopicAsync("cross-region")).TopicArn;
        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = altArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        });

        await sns.PublishAsync(topicArn, "hello");

        (await alt.ReceiveMessageAsync(altUrl)).Messages.ShouldHaveSingleItem().Body.ShouldBe("hello");
        ((await home.ReceiveMessageAsync(homeUrl)).Messages ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task AnArnInARegionWithNoQueuesResolvesToTheCurrentRegionAsBefore()
    {
        // Hand-built ARNs naming another region used to reach the home queue of that name; they
        // still do while the bus holds no queue in the region they name.
        var bus = new InMemoryAwsBus();
        using var sns = bus.CreateSnsClient();
        using var sqs = bus.CreateSqsClient();

        var url = (await sqs.CreateQueueAsync("legacy")).QueueUrl;
        var topicArn = (await sns.CreateTopicAsync("legacy-topic")).TopicArn;
        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = $"arn:aws:sqs:{AltRegion}:{bus.CurrentAccountId}:legacy",
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        });

        await sns.PublishAsync(topicArn, "still delivered");

        (await sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem().Body.ShouldBe("still delivered");
    }

    [Test]
    public async Task RedriveFromAnotherRegionsQueueIsRefusedBeforeLookingTheTargetUp()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("source")).QueueUrl;
        var policy = $$"""{"deadLetterTargetArn":"arn:aws:sqs:{{AltRegion}}:{{bus.CurrentAccountId}}:never-created","maxReceiveCount":3}""";

        var error = await Should.ThrowAsync<AmazonSQSException>(() => sqs.SetQueueAttributesAsync(url, new() { ["RedrivePolicy"] = policy }));

        error.ErrorCode.ShouldBe("InvalidParameterValue");
        error.Message.ShouldBe($"Value {policy} for parameter RedrivePolicy is invalid. Reason: Dead-letter target must be in same region as the source.");
    }
}
