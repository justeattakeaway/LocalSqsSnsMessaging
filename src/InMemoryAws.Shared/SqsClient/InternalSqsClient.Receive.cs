#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using InMemoryAws.Sqs.Model;

namespace InMemoryAws;

/// <summary>ReceiveMessage: validation, and trimming each received message to the attributes asked for (the queue receives).</summary>
internal sealed partial class InternalSqsClient
{
    public async Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.ReceiveMessage);

        SqsRequestValidation.ValidateReceive(request);
        cancellationToken.ThrowIfCancellationRequested();

        // A receive that names no wait time or visibility timeout takes the queue's; an explicit
        // zero is honoured (a short poll, or a message that is visible again immediately).
        var messages = await queue.ReceiveAsync(new SqsQueueResource.ReceiveRequest(
                MaxMessages: request.MaxNumberOfMessages ?? 1,
                VisibilityTimeout: request.VisibilityTimeout is { } visibilityTimeout ? TimeSpan.FromSeconds(visibilityTimeout) : queue.VisibilityTimeout,
                WaitTime: request.WaitTimeSeconds is { } waitTime ? TimeSpan.FromSeconds(waitTime) : queue.ReceiveMessageWaitTime,
                AttemptId: queue.IsFifo ? request.ReceiveRequestAttemptId : null),
            cancellationToken).ConfigureAwait(true);

        var requestedSystemAttributes = GetRequestedSystemAttributes(request);
        foreach (var message in messages)
        {
            FilterSystemAttributes(message, requestedSystemAttributes);
            FilterMessageAttributes(message, request.MessageAttributeNames);
        }

        _cloud.RecordOperation(AwsServiceName.Sqs, SqsActionName.ReceiveMessage, queue.Arn);
        return new ReceiveMessageResponse
        {
            Messages = messages.ToInitializedList(),
        }.SetCommonProperties();
    }

    /// <summary>
    /// The system attributes a receive asked for: <c>MessageSystemAttributeNames</c> together with
    /// the deprecated <c>AttributeNames</c>, which SQS still honours.
    /// </summary>
    private static List<string>? GetRequestedSystemAttributes(ReceiveMessageRequest request)
    {
        if (request.AttributeNames is not { Count: > 0 } legacyNames)
        {
            return request.MessageSystemAttributeNames;
        }

        if (request.MessageSystemAttributeNames is not { Count: > 0 } systemNames)
        {
            return legacyNames;
        }

        return [.. systemNames.Union(legacyNames, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Keeps only the message attributes the receive asked for, as SQS does: none unless named,
    /// all for <c>All</c>, <c>*</c> or <c>.*</c>, names starting with the prefix for <c>prefix.*</c>, and
    /// otherwise exact (case-sensitive) names. The digest covers just the attributes returned.
    /// </summary>
    private static void FilterMessageAttributes(Message message, List<string>? requestedMessageAttributes)
    {
        if (message.MessageAttributes is { Count: > 0 } attributes && requestedMessageAttributes is { Count: > 0 })
        {
            var returnAll = requestedMessageAttributes.Exists(name => name is "All" or ".*" or "*");
            message.MessageAttributes = returnAll
                ? attributes
                : attributes
                    .Where(attribute => requestedMessageAttributes.Exists(name => MatchesAttributeName(name, attribute.Key)))
                    .ToDictionary();
        }
        else
        {
            message.MessageAttributes = null;
        }

        if (message.MessageAttributes is { Count: 0 })
        {
            message.MessageAttributes = null;
        }

        message.MD5OfMessageAttributes = SqsMessageAttributeDigest.Compute(message.MessageAttributes);

        static bool MatchesAttributeName(string requested, string attributeName) =>
            requested.EndsWith(".*", StringComparison.Ordinal)
                ? attributeName.StartsWith(requested[..^2], StringComparison.Ordinal)
                : string.Equals(requested, attributeName, StringComparison.Ordinal);
    }

    private static void FilterSystemAttributes(Message message, List<string>? requestedSystemAttributes)
    {
        if (requestedSystemAttributes is null || requestedSystemAttributes.Count == 0)
        {
            message.Attributes = ((Dictionary<string, string>?)null).ToInitializedDictionary();
            return;
        }

        if (requestedSystemAttributes.Contains("All"))
        {
            return; // Keep all attributes
        }

        if (message.Attributes is not null)
        {
            var attributesToRemove = message.Attributes.Keys
                .Where(key => !requestedSystemAttributes.Contains(key))
                .ToList();

            foreach (var key in attributesToRemove)
            {
                message.Attributes.Remove(key);
            }
        }
    }
}
