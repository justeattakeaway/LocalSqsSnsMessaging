using System.Net;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.SdkClient;

/// <summary>
/// The caller identity, queue policy and SSE-KMS behaviour of SDK clients, beyond what the AWS
/// evaluation suite checks: in particular that callers who never sign specially, never set a
/// policy and never name a custom KMS alias see no change.
/// </summary>
public sealed class SqsCallerPolicyAndKmsTests
{
    private static AmazonSQSClient SignedClient(InMemoryAwsBus bus, string accessKey, out InMemoryAwsHttpClientFactory factory)
    {
        factory = new InMemoryAwsHttpClientFactory(bus);
        return new AmazonSQSClient(
            new BasicAWSCredentials(accessKey, "secret"),
            new AmazonSQSConfig
            {
                ServiceURL = $"https://sqs.{bus.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = bus.CurrentRegion,
                MaxErrorRetry = 0,
                HttpClientFactory = factory,
            });
    }

    private static async Task<string> SenderIdOfNextMessage(AmazonSQSClient sqs, string queueUrl)
    {
        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MessageSystemAttributeNames = ["SenderId"],
        });
        return received.Messages.ShouldHaveSingleItem().Attributes["SenderId"];
    }

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string queueUrl) =>
        (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];

    private static string Policy(params JsonObject[] statements) => new JsonObject
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(statements),
    }.ToJsonString();

    [Test]
    public async Task SenderId_UnsignedClient_IsTheAccountId()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("sender-unsigned")).QueueUrl;

        await sqs.SendMessageAsync(url, "hello");

        (await SenderIdOfNextMessage(sqs, url)).ShouldBe(bus.CurrentAccountId);
    }

    [Test]
    public async Task SenderId_SignedWithAnAccessKey_IsTheBusUserId()
    {
        var bus = new InMemoryAwsBus { CurrentUserId = "AIDATESTUSER00000001" };
        using var sqs = SignedClient(bus, "AKIAEXAMPLEKEY", out var factory);
        using var _ = factory;
        var url = (await sqs.CreateQueueAsync("sender-signed")).QueueUrl;

        await sqs.SendMessageAsync(url, "hello");

        (await SenderIdOfNextMessage(sqs, url)).ShouldBe("AIDATESTUSER00000001");
    }

    [Test]
    public async Task SenderId_SignedWithAnAccountIdAccessKey_IsThatAccountsRoot()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = SignedClient(bus, "123456789012", out var factory);
        using var _ = factory;
        var url = (await sqs.CreateQueueAsync("sender-root")).QueueUrl;

        await sqs.SendMessageAsync(url, "hello");

        (await SenderIdOfNextMessage(sqs, url)).ShouldBe("123456789012");
    }

    [Test]
    public async Task Deny_ForEveryone_AlsoBlocksTheUnsignedClient_AndOnlyForThatAction()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("deny-everyone")).QueueUrl;
        var arn = await QueueArn(sqs, url);
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = Policy(new JsonObject
        {
            ["Effect"] = "Deny",
            ["Principal"] = "*",
            ["Action"] = new JsonArray("sqs:ReceiveMessage", "sqs:Delete*"),
            ["Resource"] = arn,
        }) });

        var denied = await Should.ThrowAsync<AmazonSQSException>(() => sqs.ReceiveMessageAsync(url));
        denied.ErrorCode.ShouldBe("AccessDenied");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        denied.Message.ShouldBe(
            $"User: arn:aws:iam::{bus.CurrentAccountId}:root is not authorized to perform: sqs:receivemessage " +
            $"on resource: {arn} with an explicit deny in a resource-based policy");
        await Should.ThrowAsync<AmazonSQSException>(() => sqs.DeleteQueueAsync(url));

        // Sends are not covered by the statement.
        await sqs.SendMessageAsync(url, "still allowed");
    }

    [Test]
    public async Task Deny_ForAnotherAccount_OrUnderAnUnevaluatedCondition_IsNotEnforced()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("deny-not-applicable")).QueueUrl;
        var arn = await QueueArn(sqs, url);
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = Policy(
            new JsonObject
            {
                ["Effect"] = "Deny",
                ["Principal"] = new JsonObject { ["AWS"] = "arn:aws:iam::111122223333:root" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
            },
            new JsonObject
            {
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                // Not evaluated by the library, so the statement is skipped rather than guessed at.
                ["Condition"] = new JsonObject { ["DateGreaterThan"] = new JsonObject { ["aws:CurrentTime"] = "2000-01-01T00:00:00Z" } },
            },
            new JsonObject
            {
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                // A direct send has no source ARN; only an SNS delivery from that topic would.
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = "arn:aws:sns:us-east-1:000000000000:topic" } },
            }) });

        await sqs.SendMessageAsync(url, "allowed");
    }

    [Test]
    public async Task Deny_WithANegatedConditionOnAKeyTheLibraryDoesNotModel_IsNotEnforced()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("deny-unmodelled-key")).QueueUrl;
        var arn = await QueueArn(sqs, url);
        // A common organization guardrail: the request has no modelled aws:PrincipalOrgID, so the
        // statement cannot be evaluated and is skipped, not applied as if the key were absent.
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = Policy(new JsonObject
        {
            ["Effect"] = "Deny",
            ["Principal"] = "*",
            ["Action"] = "sqs:*",
            ["Resource"] = arn,
            ["Condition"] = new JsonObject { ["StringNotEquals"] = new JsonObject { ["aws:PrincipalOrgID"] = "o-abc123" } },
        }) });

        await sqs.SendMessageAsync(url, "allowed");
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = "" });
    }

    [Test]
    public async Task Deny_ConditionedOnSourceIp_MatchesTheInProcessLoopbackAddress()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("deny-by-ip")).QueueUrl;
        var arn = await QueueArn(sqs, url);
        JsonObject DenyFrom(string cidr) => new()
        {
            ["Effect"] = "Deny",
            ["Principal"] = "*",
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = arn,
            ["Condition"] = new JsonObject { ["IpAddress"] = new JsonObject { ["aws:SourceIp"] = cidr } },
        };

        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = Policy(DenyFrom("10.0.0.0/8")) });
        await sqs.SendMessageAsync(url, "from loopback");

        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = Policy(DenyFrom("127.0.0.0/8")) });
        await Should.ThrowAsync<AmazonSQSException>(() => sqs.SendMessageAsync(url, "denied"));
    }

    [Test]
    public async Task SseKmsQueue_WithAnSnsStyleEveryoneGrant_StillAcceptsTheUnsignedClient()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "sse-sns-grant",
            Attributes = new() { ["KmsMasterKeyId"] = "alias/aws/sqs" },
        })).QueueUrl;
        var arn = await QueueArn(sqs, url);
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = Policy(new JsonObject
        {
            ["Effect"] = "Allow",
            ["Principal"] = "*",
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = arn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = "arn:aws:sns:us-east-1:000000000000:topic" } },
        }) });

        // The grant is for SNS, not for this caller, so the caller is still the account.
        await sqs.SendMessageAsync(url, "sent by the owner");
    }

    [Test]
    [Arguments("alias/aws/sqs")]
    [Arguments("1234abcd-12ab-34cd-56ef-1234567890ab")]
    [Arguments("arn:aws:kms:us-east-1:000000000000:key/1234abcd-12ab-34cd-56ef-1234567890ab")]
    [Arguments("mrk-1234abcd12ab34cd56ef1234567890ab")]
    [Arguments("alias/registered")]
    [Arguments("arn:aws:kms:us-east-1:000000000000:alias/registered")]
    public async Task KmsKey_ThatExists_EncryptsSends(string keyId)
    {
        var bus = new InMemoryAwsBus { KmsKeyAliases = new HashSet<string> { "registered" } };
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "kms-exists",
            Attributes = new() { ["KmsMasterKeyId"] = keyId },
        })).QueueUrl;

        await sqs.SendMessageAsync(url, "encrypted");
        await sqs.SendMessageBatchAsync(url, [new SendMessageBatchRequestEntry("1", "batched")]);
    }

    [Test]
    public async Task KmsKey_UnknownAlias_RefusesSingleAndBatchSends()
    {
        var bus = new InMemoryAwsBus { KmsKeyAliases = new HashSet<string>() };
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "kms-missing",
            Attributes = new() { ["KmsMasterKeyId"] = "arn:aws:kms:us-east-1:000000000000:alias/missing" },
        })).QueueUrl;

        var single = await Should.ThrowAsync<AmazonSQSException>(() => sqs.SendMessageAsync(url, "nope"));
        single.ErrorCode.ShouldBe("KMS.NotFoundException");
        single.Message.ShouldStartWith("Alias arn:aws:kms:us-east-1:000000000000:alias/missing is not found.");
        var batch = await Should.ThrowAsync<AmazonSQSException>(() =>
            sqs.SendMessageBatchAsync(url, [new SendMessageBatchRequestEntry("1", "nope")]));
        batch.ErrorCode.ShouldBe("KMS.NotFoundException");

        (await sqs.GetQueueAttributesAsync(url, ["ApproximateNumberOfMessages"])).ApproximateNumberOfMessages.ShouldBe(0);
    }

    [Test]
    public async Task ReceiptHandle_FromADeletedQueue_FailsAloneInABatchOnTheRecreatedQueue()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("incarnation-batch")).QueueUrl;
        await sqs.SendMessageAsync(url, "old");
        var oldHandle = (await sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem().ReceiptHandle;
        await sqs.DeleteQueueAsync(url);
        url = (await sqs.CreateQueueAsync("incarnation-batch")).QueueUrl;
        await sqs.SendMessageAsync(url, "new");
        var newHandle = (await sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem().ReceiptHandle;

        var result = await sqs.DeleteMessageBatchAsync(url, [
            new DeleteMessageBatchRequestEntry("old", oldHandle),
            new DeleteMessageBatchRequestEntry("new", newHandle),
        ]);

        result.Successful.ShouldHaveSingleItem().Id.ShouldBe("new");
        result.Failed.ShouldHaveSingleItem().Id.ShouldBe("old");
    }

    [Test]
    public async Task AddPermission_WritesASinglePrincipalAndActionAsStrings_AndSeveralAsLists()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("add-permission-shape")).QueueUrl;

        await sqs.AddPermissionAsync(url, "one", [bus.CurrentAccountId], ["SendMessage"]);
        await sqs.AddPermissionAsync(url, "two", [bus.CurrentAccountId], ["SendMessage", "ReceiveMessage"]);

        var statements = JsonNode.Parse((await sqs.GetQueueAttributesAsync(url, ["Policy"])).Policy)!["Statement"]!.AsArray();
        statements[0]!["Action"]!.GetValue<string>().ShouldBe("SQS:SendMessage");
        statements[0]!["Principal"]!["AWS"]!.GetValue<string>().ShouldBe($"arn:aws:iam::{bus.CurrentAccountId}:root");
        statements[1]!["Action"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["SQS:SendMessage", "SQS:ReceiveMessage"]);
    }

    [Test]
    [Arguments("10.1.2.3/8", "10.200.0.1", true)]
    [Arguments("10.1.2.3/8", "11.0.0.1", false)]
    [Arguments("127.0.0.1/32", "127.0.0.1", true)]
    [Arguments("127.0.0.1", "127.0.0.2", false)]
    [Arguments("192.168.1.0/23", "192.168.0.255", true)]
    [Arguments("2001:db8::/32", "2001:db8:1::1", true)]
    [Arguments("2001:db8::/32", "127.0.0.1", false)]
    public void SourceIpCidrMatching(string cidr, string address, bool expected)
    {
        SqsQueuePolicy.InCidr(cidr, address).ShouldBe(expected);
    }

    [Test]
    [Arguments("sqs:*", "sqs:SendMessage", true)]
    [Arguments("sqs:Send*", "sqs:sendmessage", true)]
    [Arguments("sqs:Receive?essage", "sqs:ReceiveMessage", true)]
    [Arguments("sqs:SendMessage", "sqs:SendMessageBatch", false)]
    [Arguments("*", "anything", true)]
    public void ActionWildcards(string pattern, string value, bool expected)
    {
        SqsQueuePolicy.Glob(pattern, value, StringComparison.OrdinalIgnoreCase).ShouldBe(expected);
    }

    [Test]
    public async Task KmsKey_AnyAlias_ExistsWhenNoAliasesAreConfigured()
    {
        var bus = new InMemoryAwsBus();
        using var sqs = bus.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "kms-unconfigured",
            Attributes = new() { ["KmsMasterKeyId"] = "alias/my-app-key" },
        })).QueueUrl;

        await sqs.SendMessageAsync(url, "encrypted");
    }
}
