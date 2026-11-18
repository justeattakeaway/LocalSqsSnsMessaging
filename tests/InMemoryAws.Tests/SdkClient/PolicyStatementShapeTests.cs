using System.Text.Json.Nodes;
using Shouldly;

namespace InMemoryAws.Tests.SdkClient;

/// <summary>
/// A resource policy may give <c>Statement</c> as one object rather than a list. The permission
/// operations must edit such a policy as they edit a list: keep its statement, add beside it, and
/// remove it by its <c>Sid</c>.
/// </summary>
public sealed class PolicyStatementShapeTests
{
    private static string SingleStatementPolicy(string sid, string resource, string action) => new JsonObject
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonObject
        {
            ["Sid"] = sid,
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = "arn:aws:iam::123456789012:root" },
            ["Action"] = action,
            ["Resource"] = resource,
        },
    }.ToJsonString();

    private static List<string?> Sids(string policy) =>
        JsonNode.Parse(policy)!["Statement"]!.AsArray().Select(s => (string?)s!["Sid"]).ToList();

    [Test]
    public async Task Sqs_AddPermission_ToASingleStatementPolicy_KeepsThatStatement()
    {
        var cloud = new InMemoryAwsCloud();
        using var sqs = cloud.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("single-statement-add")).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(url, ["QueueArn"])).QueueARN;
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = SingleStatementPolicy("Existing", arn, "SQS:SendMessage") });

        await sqs.AddPermissionAsync(url, "Added", [cloud.CurrentAccountId], ["ReceiveMessage"]);

        var policy = (await sqs.GetQueueAttributesAsync(url, ["Policy"])).Policy;
        Sids(policy).ShouldBe(["Existing", "Added"]);
    }

    [Test]
    public async Task Sqs_RemovePermission_FromASingleStatementPolicy_RemovesThePolicy()
    {
        var cloud = new InMemoryAwsCloud();
        using var sqs = cloud.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("single-statement-remove")).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(url, ["QueueArn"])).QueueARN;
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = SingleStatementPolicy("Existing", arn, "SQS:SendMessage") });

        await sqs.RemovePermissionAsync(url, "Existing");

        ((await sqs.GetQueueAttributesAsync(url, ["Policy"])).Attributes ?? []).ShouldNotContainKey("Policy");
    }

    [Test]
    public async Task Sqs_AddPermission_WithTheLabelOfASingleStatement_IsRefused()
    {
        var cloud = new InMemoryAwsCloud();
        using var sqs = cloud.CreateSqsClient();
        var url = (await sqs.CreateQueueAsync("single-statement-duplicate")).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(url, ["QueueArn"])).QueueARN;
        await sqs.SetQueueAttributesAsync(url, new() { ["Policy"] = SingleStatementPolicy("Existing", arn, "SQS:SendMessage") });

        var error = await Should.ThrowAsync<Amazon.SQS.AmazonSQSException>(
            () => sqs.AddPermissionAsync(url, "Existing", [cloud.CurrentAccountId], ["ReceiveMessage"]));

        error.ErrorCode.ShouldBe("InvalidParameterValue");
        error.Message.ShouldBe("Value Existing for parameter Label is invalid. Reason: Already exists.");
    }

    [Test]
    public async Task Sns_AddPermission_ToASingleStatementPolicy_KeepsThatStatement()
    {
        var cloud = new InMemoryAwsCloud();
        using var sns = cloud.CreateSnsClient();
        var arn = (await sns.CreateTopicAsync("single-statement-add")).TopicArn;
        await sns.SetTopicAttributesAsync(arn, "Policy", SingleStatementPolicy("Existing", arn, "SNS:GetTopicAttributes"));

        await sns.AddPermissionAsync(new Amazon.SimpleNotificationService.Model.AddPermissionRequest
        {
            TopicArn = arn, Label = "Added", AWSAccountId = [cloud.CurrentAccountId], ActionName = ["Publish"],
        });

        var policy = (await sns.GetTopicAttributesAsync(arn)).Attributes["Policy"];
        Sids(policy).ShouldBe(["Existing", "Added"]);
    }

    [Test]
    public async Task Sns_RemovePermission_FromASingleStatementPolicy_RemovesThatStatement()
    {
        var cloud = new InMemoryAwsCloud();
        using var sns = cloud.CreateSnsClient();
        var arn = (await sns.CreateTopicAsync("single-statement-remove")).TopicArn;
        await sns.SetTopicAttributesAsync(arn, "Policy", SingleStatementPolicy("Existing", arn, "SNS:GetTopicAttributes"));

        await sns.RemovePermissionAsync(arn, "Existing");

        var policy = (await sns.GetTopicAttributesAsync(arn)).Attributes["Policy"];
        Sids(policy).ShouldBeEmpty();
    }
}
