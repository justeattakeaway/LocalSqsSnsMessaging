using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;
using Xunit;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsQueueTagsTests
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;
    private string _queueUrl = null!;

    protected abstract Task AdvanceTime(TimeSpan timeSpan);

    private async Task SetupQueue()
    {
        // Create test queue
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" });
        _queueUrl = createQueueResponse.QueueUrl;

        // Get queue ARN
        var attrResponse = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = _queueUrl,
            AttributeNames = ["QueueArn"]
        });
    }

    [Fact]
    public async Task TagQueueAsync_ValidTags_TagsAreApplied()
    {
        await SetupQueue();

        // Add tags to the queue
        var tags = new Dictionary<string, string>
        {
            ["Environment"] = "Production",
            ["Team"] = "Infrastructure",
            ["Cost-Center"] = "12345"
        };

        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = tags
        }, TestContext.Current.CancellationToken);

        // Verify tags were applied
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.ToDictionary().ShouldBeEquivalentTo(tags);
    }

    [Fact]
    public async Task TagQueueAsync_InvalidQueueUrl_ThrowsException()
    {
        await SetupQueue();

        var tags = new Dictionary<string, string>
        {
            ["Environment"] = "Production"
        };

        await Assert.ThrowsAsync<QueueDoesNotExistException>(() =>
            Sqs.TagQueueAsync(new TagQueueRequest
            {
                QueueUrl = "https://sqs.us-east-1.amazonaws.com/invalid-queue",
                Tags = tags
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UntagQueueAsync_ExistingTags_TagsAreRemoved()
    {
        await SetupQueue();

        // First, add some tags
        var initialTags = new Dictionary<string, string>
        {
            ["Environment"] = "Production",
            ["Team"] = "Infrastructure",
            ["Cost-Center"] = "12345"
        };

        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = initialTags
        }, TestContext.Current.CancellationToken);

        // Remove specific tags
        await Sqs.UntagQueueAsync(new UntagQueueRequest
        {
            QueueUrl = _queueUrl,
            TagKeys = ["Environment", "Team"]
        }, TestContext.Current.CancellationToken);

        // Verify only non-removed tags remain
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.Count.ShouldBe(1);
        listTagsResponse.Tags.ShouldContainKey("Cost-Center");
        listTagsResponse.Tags["Cost-Center"].ShouldBe("12345");
    }

    [Fact]
    public async Task UntagQueueAsync_NonexistentTags_NoError()
    {
        await SetupQueue();

        // Try to remove tags that don't exist
        await Sqs.UntagQueueAsync(new UntagQueueRequest
        {
            QueueUrl = _queueUrl,
            TagKeys = ["NonexistentTag1", "NonexistentTag2"]
        }, TestContext.Current.CancellationToken);

        // Verify no tags exist
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.ShouldBeUninitialized();
    }

    [Fact]
    public async Task ListQueueTagsAsync_NoTags_ReturnsEmptyDictionary()
    {
        await SetupQueue();

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.ShouldBeUninitialized();
    }

    [Fact]
    public async Task TagQueueAsync_UpdateExistingTag_TagValueIsUpdated()
    {
        await SetupQueue();

        // Add initial tags
        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = new Dictionary<string, string>
            {
                ["Environment"] = "Development"
            }
        }, TestContext.Current.CancellationToken);

        // Update tag value
        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = new Dictionary<string, string>
            {
                ["Environment"] = "Production"
            }
        }, TestContext.Current.CancellationToken);

        // Verify tag was updated
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.Count.ShouldBe(1);
        listTagsResponse.Tags["Environment"].ShouldBe("Production");
    }

    [Fact]
    public async Task TagQueueAsync_MaximumTags_Success()
    {
        await SetupQueue();

        // AWS allows up to 50 tags per queue
        var tags = Enumerable.Range(1, 50)
            .ToDictionary(
                i => $"Key{i}",
                i => $"Value{i}"
            );

        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = tags
        }, TestContext.Current.CancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.Count.ShouldBe(50);
        listTagsResponse.Tags.ToDictionary().ShouldBeEquivalentTo(tags);
    }

    [Fact]
    public async Task TagQueueAsync_EmptyTagValue_Success()
    {
        await SetupQueue();

        var tags = new Dictionary<string, string>
        {
            ["EmptyTag"] = string.Empty
        };

        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = tags
        }, TestContext.Current.CancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.ShouldContainKey("EmptyTag");
        listTagsResponse.Tags["EmptyTag"].ShouldBeEmpty();
    }

    [Fact]
    public async Task TagQueueAsync_NullTagValue_Success()
    {
        await SetupQueue();

        var tags = new Dictionary<string, string>
        {
            ["NullTag"] = null!
        };

        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = tags
        }, TestContext.Current.CancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.ShouldBeEmpty();
    }

    [Fact]
    public async Task TagQueueAsync_UpdateTagToEmptyValue_Success()
    {
        await SetupQueue();

        // Add initial tag with non-empty value
        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = new Dictionary<string, string>
            {
                ["TestTag"] = "InitialValue"
            }
        }, TestContext.Current.CancellationToken);

        // Update tag to empty value
        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = new Dictionary<string, string>
            {
                ["TestTag"] = string.Empty
            }
        }, TestContext.Current.CancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, TestContext.Current.CancellationToken);

        listTagsResponse.Tags.ShouldContainKey("TestTag");
        listTagsResponse.Tags["TestTag"].ShouldBeEmpty();
    }
}
