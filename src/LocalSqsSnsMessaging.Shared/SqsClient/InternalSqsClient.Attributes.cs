#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>GetQueueAttributes and SetQueueAttributes, the computed attributes and the redrive policy.</summary>
internal sealed partial class InternalSqsClient
{
    public Task<GetQueueAttributesResponse> GetQueueAttributesAsync(GetQueueAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.GetQueueAttributes);

        var attributes = new Dictionary<string, string>();
        var attributeNames = request.AttributeNames ?? [];
        SqsQueueValidation.ValidateAttributeNamesToGet(attributeNames);

        if (attributeNames.Count == 0 || attributeNames.Contains("All"))
        {
            attributes = new Dictionary<string, string>(queue.Attributes ?? []);
            AddComputedAttributes(queue, queue.CountMessages(), attributes);
        }
        else
        {
            var counts = queue.CountMessages();
            foreach (var attributeName in attributeNames)
            {
                if (queue.Attributes?.TryGetValue(attributeName, out var value) == true)
                {
                    attributes[attributeName] = value;
                }
                else if (IsComputedAttribute(attributeName))
                {
                    AddComputedAttribute(queue, counts, attributeName, attributes);
                }
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.GetQueueAttributes, queue.Arn);
        return Task.FromResult(new GetQueueAttributesResponse
        {
            Attributes = attributes
        }.SetCommonProperties());
    }

    private static void UpdateQueueProperties(SqsQueueResource queue)
    {
        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.VisibilityTimeout, out var visibilityTimeout) == true)
        {
            queue.VisibilityTimeout = TimeSpan.FromSeconds(int.Parse(visibilityTimeout, NumberFormatInfo.InvariantInfo));
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.MessageRetentionPeriod, out var retentionSeconds) == true)
        {
            queue.MessageRetentionPeriod = TimeSpan.FromSeconds(int.Parse(retentionSeconds, NumberFormatInfo.InvariantInfo));
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.DelaySeconds, out var delaySeconds) == true)
        {
            queue.DelaySeconds = int.Parse(delaySeconds, NumberFormatInfo.InvariantInfo);
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.ReceiveMessageWaitTimeSeconds, out var waitTimeSeconds) == true)
        {
            queue.ReceiveMessageWaitTime = TimeSpan.FromSeconds(int.Parse(waitTimeSeconds, NumberFormatInfo.InvariantInfo));
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.MaximumMessageSize, out var maximumMessageSize) == true)
        {
            queue.MaximumMessageSize = int.Parse(maximumMessageSize, NumberFormatInfo.InvariantInfo);
        }

        queue.ContentBasedDeduplication = queue.Attributes?.TryGetValue(InternalQueueAttributeName.ContentBasedDeduplication, out var contentBasedDeduplication) == true
            && bool.TryParse(contentBasedDeduplication, out var enabled) && enabled;

        ExtractRedrivePolicy(queue);
    }

    private static bool IsComputedAttribute(string attributeName)
    {
        return attributeName == InternalQueueAttributeName.ApproximateNumberOfMessages
               || attributeName == InternalQueueAttributeName.ApproximateNumberOfMessagesNotVisible
               || attributeName == InternalQueueAttributeName.ApproximateNumberOfMessagesDelayed
               || attributeName == InternalQueueAttributeName.CreatedTimestamp
               || attributeName == InternalQueueAttributeName.LastModifiedTimestamp;
    }

    private static void AddComputedAttributes(SqsQueueResource queue, SqsQueueResource.MessageCounts counts, Dictionary<string, string> attributes)
    {
        AddComputedAttribute(queue, counts, InternalQueueAttributeName.ApproximateNumberOfMessages, attributes);
        AddComputedAttribute(queue, counts, InternalQueueAttributeName.ApproximateNumberOfMessagesNotVisible, attributes);
        AddComputedAttribute(queue, counts, InternalQueueAttributeName.ApproximateNumberOfMessagesDelayed, attributes);
        AddComputedAttribute(queue, counts, InternalQueueAttributeName.CreatedTimestamp, attributes);
        AddComputedAttribute(queue, counts, InternalQueueAttributeName.LastModifiedTimestamp, attributes);
    }

    /// <param name="counts">The queue's message counts, taken once per request (each count sweeps expired messages).</param>
    private static void AddComputedAttribute(SqsQueueResource queue, SqsQueueResource.MessageCounts counts, string attributeName, Dictionary<string, string> attributes)
    {
        long? value = attributeName switch
        {
            InternalQueueAttributeName.ApproximateNumberOfMessages => counts.Visible,
            InternalQueueAttributeName.ApproximateNumberOfMessagesNotVisible => counts.InFlight,
            InternalQueueAttributeName.ApproximateNumberOfMessagesDelayed => counts.Delayed,
            // Epoch seconds, as SQS reports them.
            InternalQueueAttributeName.CreatedTimestamp => queue.CreatedTimestamp.ToUnixTimeSeconds(),
            InternalQueueAttributeName.LastModifiedTimestamp => queue.LastModifiedTimestamp.ToUnixTimeSeconds(),
            _ => null,
        };

        if (value is not null)
        {
            attributes[attributeName] = value.Value.ToString(NumberFormatInfo.InvariantInfo);
        }
    }

    public Task<SetQueueAttributesResponse> SetQueueAttributesAsync(SetQueueAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.SetQueueAttributes);

        if (request.Attributes is not { Count: > 0 })
        {
            throw SqsRequestValidation.MissingParameter("Attribute.Name");
        }

        // Every attribute is checked before any is applied: one invalid value applies none.
        queue.Attributes ??= [];
        SqsQueueValidation.ValidateAttributes(request.Attributes, queue.IsFifo, creating: false, current: queue.Attributes);
        SqsQueueValidation.ValidateRedriveAllowPolicyRegion(request.Attributes, queue.Region);
        if (request.Attributes.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var redrivePolicy))
        {
            ValidateRedrivePolicy(queue, redrivePolicy);
        }

        foreach (var (key, value) in request.Attributes)
        {
            queue.Attributes[key] = value;
        }

        if (request.Attributes.ContainsKey(InternalQueueAttributeName.Policy))
        {
            StorePolicy(queue.Attributes);
        }

        queue.LastModifiedTimestamp = _bus.TimeProvider.GetUtcNow();
        UpdateQueueProperties(queue);
        if (queue.IsFifo)
        {
            // A FIFO queue's delay applies to messages already waiting.
            queue.FifoDelaySecondsChanged(_bus.TimeProvider);
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.SetQueueAttributes, queue.Arn);
        return Task.FromResult(new SetQueueAttributesResponse
        {
            HttpStatusCode = HttpStatusCode.OK
        }.SetCommonProperties());
    }

    /// <summary>
    /// Checks a <c>RedrivePolicy</c> being set on <paramref name="source"/>: well formed, naming an
    /// existing queue of the same type whose <c>RedriveAllowPolicy</c> admits the source.
    /// </summary>
    private void ValidateRedrivePolicy(SqsQueueResource source, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var policy = SqsQueueValidation.ParseRedrivePolicy(value);
        if (Arn.RegionOf(policy.DeadLetterTargetArn) is { } targetRegion
            && !string.Equals(targetRegion, source.Region, StringComparison.Ordinal))
        {
            throw SqsRequestValidation.InvalidParameterValue("RedrivePolicy", value,
                "Dead-letter target must be in same region as the source.");
        }

        var deadLetterQueue = FindQueueByArn(policy.DeadLetterTargetArn)
            ?? throw SqsRequestValidation.InvalidParameterValue("RedrivePolicy", value, "Dead letter target does not exist.");
        if (deadLetterQueue.IsFifo != source.IsFifo)
        {
            throw SqsRequestValidation.InvalidParameterValue("RedrivePolicy", value,
                "Dead-letter queue must be same type of queue as the source..");
        }

        if (!SqsQueueValidation.RedriveAllowed(deadLetterQueue, source.Arn))
        {
            throw SqsQueueValidation.RedriveNotAllowed(policy);
        }
    }

    private static void ExtractRedrivePolicy(SqsQueueResource queue)
    {
        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var emptyPolicy) == true
            && string.IsNullOrEmpty(emptyPolicy))
        {
            // An empty RedrivePolicy unlinks the dead-letter queue.
            queue.Attributes.Remove(InternalQueueAttributeName.RedrivePolicy);
            queue.DeadLetterTargetArn = null;
            queue.MaxReceiveCount = null;
            return;
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var redrivePolicy) == true)
        {
            var policy = JsonDocument.Parse(redrivePolicy);
            var deadLetterTargetArn = policy.RootElement.GetProperty("deadLetterTargetArn").GetString();
            var maxReceiveCount = 0;
            var maxReceiveCountProperty = policy.RootElement.GetProperty("maxReceiveCount");
            if (maxReceiveCountProperty.ValueKind == JsonValueKind.Number)
            {
                maxReceiveCount = maxReceiveCountProperty.GetInt32();
            }
            else
            {
                var maxReceiveCountString = maxReceiveCountProperty.GetString();
                if (maxReceiveCountString != null)
                {
                    maxReceiveCount = int.Parse(maxReceiveCountString, NumberFormatInfo.InvariantInfo);
                }
            }

            if (deadLetterTargetArn != null && maxReceiveCount > 0)
            {
                // Checked by ValidateRedrivePolicy when set. The dead-letter queue may be deleted
                // later, which leaves the policy in place with nowhere to redrive to until a queue
                // of the same ARN is created again.
                queue.DeadLetterTargetArn = deadLetterTargetArn;
                queue.MaxReceiveCount = maxReceiveCount;

                // SQS stores the policy normalised: compact JSON with a numeric maxReceiveCount.
                queue.Attributes[InternalQueueAttributeName.RedrivePolicy] = new JsonObject
                {
                    ["deadLetterTargetArn"] = deadLetterTargetArn,
                    ["maxReceiveCount"] = maxReceiveCount
                }.ToJsonString();
            }
        }
    }
}
