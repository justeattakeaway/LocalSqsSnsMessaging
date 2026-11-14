using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Sns;

/// <summary>
/// SNS deliveries to SQS weighed against the subscribed queue's access policy, and the validation
/// of topic permissions and subscription redrive and delivery policies.
/// </summary>
public sealed class SnsQueuePolicyDeliveryTests : IDisposable
{
    private readonly InMemoryAwsBus _bus = new();
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonSQSClient _sqs;

    public SnsQueuePolicyDeliveryTests()
    {
        _sns = _bus.CreateSnsClient();
        _sqs = _bus.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
    }

    private async Task<(string Url, string Arn)> CreateQueueAsync(string name)
    {
        var url = (await _sqs.CreateQueueAsync(name)).QueueUrl;
        var arn = (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];
        return (url, arn);
    }

    private async Task<string> SubscribeAsync(string topicArn, string queueArn) =>
        (await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;

    private Task<SetQueueAttributesResponse> SetPolicyAsync(string url, params JsonObject[] statements) =>
        _sqs.SetQueueAttributesAsync(url, new Dictionary<string, string>
        {
            ["Policy"] = new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = new JsonArray(statements) }.ToJsonString(),
        });

    private static JsonObject Statement(string effect, string queueArn, JsonNode principal, JsonObject? condition = null)
    {
        var statement = new JsonObject
        {
            ["Effect"] = effect,
            ["Principal"] = principal,
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
        };
        if (condition is not null)
        {
            statement["Condition"] = condition;
        }

        return statement;
    }

    private static JsonObject SnsPrincipal() => new() { ["Service"] = "sns.amazonaws.com" };

    private async Task<List<string>> ReceiveBodiesAsync(string url) =>
        (await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10 }))
        .Messages?.Select(m => m.Body).ToList() ?? [];

    [Test]
    public async Task Publish_DeliversToAQueueWhosePolicySaysNothingAboutSns()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;
        var (url, arn) = await CreateQueueAsync("queue");
        await SetPolicyAsync(url, Statement("Allow", arn, new JsonObject { ["AWS"] = $"arn:aws:iam::{_bus.CurrentAccountId}:root" }));
        await SubscribeAsync(topicArn, arn);

        await _sns.PublishAsync(topicArn, "hello");

        (await ReceiveBodiesAsync(url)).ShouldBe(["hello"]);
    }

    [Test]
    public async Task Publish_ExplicitDenyForSnsWithholdsDeliveryAndDeadLetters()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;
        var (url, arn) = await CreateQueueAsync("queue");
        var (dlqUrl, dlqArn) = await CreateQueueAsync("dlq");
        await SetPolicyAsync(url, Statement("Deny", arn, SnsPrincipal()));
        var subscriptionArn = await SubscribeAsync(topicArn, arn);
        await _sns.SetSubscriptionAttributesAsync(subscriptionArn, "RedrivePolicy",
            new JsonObject { ["deadLetterTargetArn"] = dlqArn }.ToJsonString());

        await _sns.PublishAsync(topicArn, "denied");

        (await ReceiveBodiesAsync(url)).ShouldBeEmpty();
        (await ReceiveBodiesAsync(dlqUrl)).ShouldBe(["denied"]);
    }

    [Test]
    public async Task Publish_DeliversOnlyFromTheSourceArnThePolicyAdmits()
    {
        var admitted = (await _sns.CreateTopicAsync("admitted")).TopicArn;
        var other = (await _sns.CreateTopicAsync("other")).TopicArn;
        var (url, arn) = await CreateQueueAsync("queue");
        await SetPolicyAsync(url, Statement("Allow", arn, SnsPrincipal(),
            new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = admitted } }));
        await SubscribeAsync(admitted, arn);
        await SubscribeAsync(other, arn);

        await _sns.PublishAsync(other, "from other");
        await _sns.PublishAsync(admitted, "from admitted");

        (await ReceiveBodiesAsync(url)).ShouldBe(["from admitted"]);
    }

    [Test]
    public async Task Publish_WithholdsDeliveryWhenTheSourceAccountIsNotAdmitted()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;
        var (url, arn) = await CreateQueueAsync("queue");
        await SetPolicyAsync(url, Statement("Allow", arn, "*",
            new JsonObject { ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = "111122223333" } }));
        await SubscribeAsync(topicArn, arn);

        await _sns.PublishAsync(topicArn, "withheld");
        (await ReceiveBodiesAsync(url)).ShouldBeEmpty();

        await SetPolicyAsync(url, Statement("Allow", arn, "*",
            new JsonObject { ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = _bus.CurrentAccountId } }));
        await _sns.PublishAsync(topicArn, "delivered");
        (await ReceiveBodiesAsync(url)).ShouldBe(["delivered"]);
    }

    [Test]
    public async Task AddPermission_RefusesAnUnknownActionOrAMalformedAccountId()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;

        var badAction = await Should.ThrowAsync<InvalidParameterException>(() => _sns.AddPermissionAsync(
            topicArn, "bad-action", [_bus.CurrentAccountId], ["NotARealSnsAction"]));
        badAction.Message.ShouldBe("Invalid parameter: Policy statement action out of service scope!");

        var badAccount = await Should.ThrowAsync<InvalidParameterException>(() => _sns.AddPermissionAsync(
            topicArn, "bad-account", ["not-an-account"], ["Publish"]));
        badAccount.Message.ShouldBe("Invalid parameter: A provided account ID is not valid");

        var policy = (await _sns.GetTopicAttributesAsync(topicArn)).Attributes["Policy"];
        policy.ShouldNotContain("bad-action");
        policy.ShouldNotContain("bad-account");
    }

    [Test]
    public async Task SetSubscriptionAttributes_RefusesAnEmptyOrNonQueueRedrivePolicy()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;
        var (_, arn) = await CreateQueueAsync("queue");
        var subscriptionArn = await SubscribeAsync(topicArn, arn);

        var empty = await Should.ThrowAsync<InvalidParameterException>(() =>
            _sns.SetSubscriptionAttributesAsync(subscriptionArn, "RedrivePolicy", ""));
        empty.Message.ShouldBe("Invalid parameter: RedrivePolicy: missing required value");

        var topicTarget = await Should.ThrowAsync<InvalidParameterException>(() =>
            _sns.SetSubscriptionAttributesAsync(subscriptionArn, "RedrivePolicy",
                new JsonObject { ["deadLetterTargetArn"] = topicArn }.ToJsonString()));
        topicTarget.Message.ShouldBe("Invalid parameter: RedrivePolicy: deadLetterTargetArn must be a SQS queue");
    }

    [Test]
    public async Task SetSubscriptionAttributes_StoresTheDeliveryPolicyInNormalizedForm()
    {
        var topicArn = (await _sns.CreateTopicAsync("topic")).TopicArn;
        var (_, arn) = await CreateQueueAsync("queue");
        var subscriptionArn = await SubscribeAsync(topicArn, arn);

        await _sns.SetSubscriptionAttributesAsync(subscriptionArn, "DeliveryPolicy",
            """{"healthyRetryPolicy": {"numRetries": 5, "minDelayTarget": 1, "maxDelayTarget": 10}}""");

        var stored = JsonNode.Parse((await _sns.GetSubscriptionAttributesAsync(subscriptionArn)).Attributes["DeliveryPolicy"]);
        JsonNode.DeepEquals(stored, JsonNode.Parse("""
            {
              "healthyRetryPolicy": {
                "minDelayTarget": 1, "maxDelayTarget": 10, "numRetries": 5, "numMaxDelayRetries": null,
                "numNoDelayRetries": null, "numMinDelayRetries": null, "backoffFunction": null
              },
              "sicklyRetryPolicy": null, "throttlePolicy": null, "requestPolicy": null, "guaranteed": false
            }
            """)).ShouldBeTrue(stored!.ToJsonString());
    }
}
