using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Shouldly;

namespace InMemoryAws.Tests.Sns;

/// <summary>
/// Tag values with nothing in them, as SNS answered them in eu-west-1 (November 2026): an empty value is kept and
/// read back empty (<c>&lt;Value/&gt;</c>, which the SDK reads as <c>""</c>), and a missing one is refused, on
/// CreateTopic and TagResource alike.
/// </summary>
public sealed class SnsTagValueTests
{
    private const string NullValueMessage =
        "1 validation error detected: Value null at 'tags.1.member.value' failed to satisfy constraint: Member must not be null";

    [Test]
    public async Task AnEmptyValueIsKeptAndReadBackEmpty()
    {
        using var sns = new InMemoryAwsCloud().CreateSnsClient();
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = "orders",
            Tags = [new Tag { Key = "created", Value = "" }],
        })).TopicArn;
        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = topicArn, Tags = [new Tag { Key = "tagged", Value = "" }] });

        var tags = (await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = topicArn })).Tags;

        tags.ToDictionary(t => t.Key, t => t.Value).ShouldBe(new Dictionary<string, string> { ["created"] = "", ["tagged"] = "" }, ignoreOrder: true);
    }

    [Test]
    public async Task CreateTopicRefusesATagWithNoValue()
    {
        using var sns = new InMemoryAwsCloud().CreateSnsClient();

        var error = await Should.ThrowAsync<AmazonSimpleNotificationServiceException>(() => sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = "orders",
            Tags = [new Tag { Key = "missing", Value = null }],
        }));

        error.ErrorCode.ShouldBe("ValidationError");
        error.Message.ShouldBe(NullValueMessage);
        ((await sns.ListTopicsAsync(new ListTopicsRequest())).Topics ?? []).ShouldBeEmpty();
    }

    [Test]
    public async Task TagResourceRefusesATagWithNoValueAndChangesNothing()
    {
        using var sns = new InMemoryAwsCloud().CreateSnsClient();
        var topicArn = (await sns.CreateTopicAsync("orders")).TopicArn;

        var error = await Should.ThrowAsync<AmazonSimpleNotificationServiceException>(() => sns.TagResourceAsync(new TagResourceRequest
        {
            ResourceArn = topicArn,
            Tags = [new Tag { Key = "missing", Value = null }],
        }));

        error.ErrorCode.ShouldBe("ValidationError");
        error.Message.ShouldBe(NullValueMessage);
        ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = topicArn })).Tags ?? []).ShouldBeEmpty();
    }
}
