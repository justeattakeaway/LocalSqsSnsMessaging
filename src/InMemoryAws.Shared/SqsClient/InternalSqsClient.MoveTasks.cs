#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using InMemoryAws.Sqs.Model;

namespace InMemoryAws;

/// <summary>StartMessageMoveTask, CancelMessageMoveTask and ListMessageMoveTasks.</summary>
internal sealed partial class InternalSqsClient
{
    public Task<StartMessageMoveTaskResponse> StartMessageMoveTaskAsync(StartMessageMoveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceQueueName = GetQueueNameFromArn(request.SourceArn!);
        if (!Queues.TryGetValue(sourceQueueName, out var sourceQueue))
        {
            throw new InternalResourceNotFoundException("Source queue not found.");
        }

        if (_cloud.MoveTasks.Values
                .Any(t => t.SourceArn.Equals(request.SourceArn, StringComparison.OrdinalIgnoreCase) && t.Status == MoveTaskStatus.Running))
        {
            throw new InternalInvalidParameterValueException(
                "There is already a task running. Only one active task is allowed for a source queue arn at a given time.");
        }

        var deadLetterQueues =
            Queues.Values
                .Select(q => q.DeadLetterTargetArn)
                .Where(arn => arn is not null);

        if (!deadLetterQueues.Contains(sourceQueue.Arn))
        {
            throw new InternalInvalidParameterValueException("Source queue must be configured as a Dead Letter Queue.");
        }

        if (request.MaxNumberOfMessagesPerSecond is < 1 or > 500)
        {
            throw SqsRequestValidation.InvalidParameterValue(
                "Value for parameter MaxNumberOfMessagesPerSecond is invalid. Reason: You must enter a number that's between 1 and 500.");
        }

        SqsQueueResource? destinationQueue = null;
        if (request.DestinationArn is not null)
        {
            if (string.Equals(request.DestinationArn, request.SourceArn, StringComparison.Ordinal))
            {
                throw SqsRequestValidation.InvalidParameterValue("Source queue arn and destination queue arn cannot be the same.");
            }

            destinationQueue = FindQueueByArn(request.DestinationArn)
                ?? throw new InternalResourceNotFoundException("The resource that you specified for the DestinationArn parameter doesn't exist.");
            if (destinationQueue.IsFifo != sourceQueue.IsFifo)
            {
                throw SqsRequestValidation.InvalidParameterValue("The source queue and destination queue must be of the same queue type.");
            }
        }

        // In-flight messages count too: the task moves them once they become visible again.
        var counts = sourceQueue.CountMessages();
        var moveTask = new SqsMoveTask
        {
            TaskHandle = Guid.NewGuid().ToString(),
            SourceArn = sourceQueue.Arn,
            DestinationArn = destinationQueue?.Arn,
            MaxNumberOfMessagesPerSecond = request.MaxNumberOfMessagesPerSecond,
            ApproximateNumberOfMessagesToMove = counts.Visible + counts.InFlight,
            StartedTimestamp = _cloud.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _cloud.MoveTasks.TryAdd(moveTask.TaskHandle, moveTask);
        moveTask.Start(_cloud);

        _cloud.RecordOperation(AwsServiceName.Sqs, SqsActionName.StartMessageMoveTask, sourceQueue.Arn);
        return Task.FromResult(new StartMessageMoveTaskResponse
        {
            TaskHandle = moveTask.TaskHandle
        }.SetCommonProperties());
    }

    public Task<CancelMessageMoveTaskResponse> CancelMessageMoveTaskAsync(CancelMessageMoveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Only a running task can be cancelled; one that has settled is answered as if unknown.
        if (!_cloud.MoveTasks.TryGetValue(request.TaskHandle!, out var task) || !task.TryCancel())
        {
            throw new InternalResourceNotFoundException("Task does not exist.");
        }

        _cloud.RecordOperation(AwsServiceName.Sqs, SqsActionName.CancelMessageMoveTask, task.SourceArn);
        return Task.FromResult(new CancelMessageMoveTaskResponse
        {
            ApproximateNumberOfMessagesMoved = task.ApproximateNumberOfMessagesMoved
        }.SetCommonProperties());
    }

    public Task<ListMessageMoveTasksResponse> ListMessageMoveTasksAsync(ListMessageMoveTasksRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MaxResults is < 1 or > 10)
        {
            throw SqsRequestValidation.InvalidParameterValue(
                "Value for parameter MaxResults is invalid. Reason: You must enter a number that's between 1 and 10.");
        }

        // Most recent first; SQS returns only the latest task unless MaxResults asks for more.
        var tasks = _cloud.MoveTasks.Values
            .Where(t => t.SourceArn.Equals(request.SourceArn, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Sequence)
            .Take(request.MaxResults ?? 1)
            .Select(t => new ListMessageMoveTasksResultEntry
            {
                TaskHandle = t.TaskHandle,
                SourceArn = t.SourceArn,
                DestinationArn = t.DestinationArn,
                MaxNumberOfMessagesPerSecond = t.MaxNumberOfMessagesPerSecond,
                Status = t.Status,
                FailureReason = t.FailureReason,
                StartedTimestamp = t.StartedTimestamp,
                ApproximateNumberOfMessagesMoved = t.ApproximateNumberOfMessagesMoved,
                ApproximateNumberOfMessagesToMove = t.ApproximateNumberOfMessagesToMove
            });

        _cloud.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListMessageMoveTasks, request.SourceArn);
        return Task.FromResult(new ListMessageMoveTasksResponse
        {
            Results = tasks.ToList()
        }.SetCommonProperties());
    }
}
