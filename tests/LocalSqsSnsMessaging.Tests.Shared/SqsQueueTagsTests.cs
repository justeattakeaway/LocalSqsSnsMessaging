using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

public abstract class SqsQueueTagsTests
{
    protected IAmazonSQS Sqs = null!;
    protected string AccountId = null!;
    private string _queueUrl = null!;

    private async Task SetupQueue()
    {
        // Create a test queue
        var createQueueResponse = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test-queue" });
        _queueUrl = createQueueResponse.QueueUrl;
    }

    [Test]
    public async Task TagQueueAsync_ValidTags_TagsAreApplied(CancellationToken cancellationToken)
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
        }, cancellationToken);

        // Verify tags were applied
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.ToDictionary().ShouldBeEquivalentTo(tags);
    }

    [Test]
    public async Task TagQueueAsync_InvalidQueueUrl_ThrowsException(CancellationToken cancellationToken)
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
            }, cancellationToken));
    }

    [Test]
    public async Task UntagQueueAsync_ExistingTags_TagsAreRemoved(CancellationToken cancellationToken)
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
        }, cancellationToken);

        // Remove specific tags
        await Sqs.UntagQueueAsync(new UntagQueueRequest
        {
            QueueUrl = _queueUrl,
            TagKeys = ["Environment", "Team"]
        }, cancellationToken);

        // Verify only non-removed tags remain
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.Count.ShouldBe(1);
        listTagsResponse.Tags.ShouldContainKey("Cost-Center");
        listTagsResponse.Tags["Cost-Center"].ShouldBe("12345");
    }

    [Test]
    public async Task UntagQueueAsync_NonexistentTags_NoError(CancellationToken cancellationToken)
    {
        await SetupQueue();

        // Try to remove tags that don't exist
        await Sqs.UntagQueueAsync(new UntagQueueRequest
        {
            QueueUrl = _queueUrl,
            TagKeys = ["NonexistentTag1", "NonexistentTag2"]
        }, cancellationToken);

        // Verify no tags exist
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.ShouldBeEmptyAwsCollection();
    }

    [Test]
    public async Task ListQueueTagsAsync_NoTags_ReturnsEmptyDictionary(CancellationToken cancellationToken)
    {
        await SetupQueue();

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.ShouldBeEmptyAwsCollection();
    }

    [Test]
    public async Task TagQueueAsync_UpdateExistingTag_TagValueIsUpdated(CancellationToken cancellationToken)
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
        }, cancellationToken);

        // Update tag value
        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = new Dictionary<string, string>
            {
                ["Environment"] = "Production"
            }
        }, cancellationToken);

        // Verify tag was updated
        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.Count.ShouldBe(1);
        listTagsResponse.Tags["Environment"].ShouldBe("Production");
    }

    [Test]
    public async Task TagQueueAsync_MaximumTags_Success(CancellationToken cancellationToken)
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
        }, cancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.Count.ShouldBe(50);
        listTagsResponse.Tags.ToDictionary().ShouldBeEquivalentTo(tags);
    }

    [Test]
    public async Task TagQueueAsync_EmptyTagValue_Success(CancellationToken cancellationToken)
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
        }, cancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.ShouldContainKey("EmptyTag");
        listTagsResponse.Tags["EmptyTag"].ShouldBeEmpty();
    }

    [Test]
    public async Task TagQueueAsync_NullTagValue_Success(CancellationToken cancellationToken)
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
        }, cancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.ShouldBeEmpty();
    }

    [Test]
    public async Task TagQueueAsync_UpdateTagToEmptyValue_Success(CancellationToken cancellationToken)
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
        }, cancellationToken);

        // Update tag to empty value
        await Sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = _queueUrl,
            Tags = new Dictionary<string, string>
            {
                ["TestTag"] = string.Empty
            }
        }, cancellationToken);

        var listTagsResponse = await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest
        {
            QueueUrl = _queueUrl
        }, cancellationToken);

        listTagsResponse.Tags.ShouldContainKey("TestTag");
        listTagsResponse.Tags["TestTag"].ShouldBeEmpty();
    }
}
