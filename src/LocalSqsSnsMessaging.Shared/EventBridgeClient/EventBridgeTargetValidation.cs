using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Validates a PutTargets request's targets as EventBridge does, before any of them is stored: a
/// refused request changes nothing.
/// </summary>
internal static class EventBridgeTargetValidation
{
    /// <summary>The most targets one rule can hold.</summary>
    public const int MaxTargetsPerRule = 5;

    private const int MaxRetryAttempts = 185;
    private const int MinEventAgeInSeconds = 60;
    private const int MaxEventAgeInSeconds = 86400;

    /// <summary>
    /// Services whose targets EventBridge invokes with an IAM role, so a target on them needs its own
    /// <c>RoleArn</c> (the rule's is never used). Event buses need one too; see <see cref="RequiresRoleArn"/>.
    /// </summary>
    private static readonly HashSet<string> RoleRequiringServices = new(StringComparer.Ordinal)
    {
        "states", "kinesis", "firehose", "ecs", "batch", "codebuild", "codepipeline", "sagemaker"
    };

    /// <summary>
    /// Throws the error EventBridge answers for the first problem in <paramref name="targets"/>:
    /// a missing Id or Arn, or an out-of-range <c>RetryPolicy</c> (all reported together, as AWS's
    /// constraint check does); an Id given twice; a malformed Arn; a FIFO SNS topic target; a FIFO
    /// queue as dead-letter queue; or a role-requiring target without a <c>RoleArn</c>.
    /// </summary>
    public static void Validate(IReadOnlyList<Target> targets)
    {
        ThrowConstraintViolations(targets);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (!ids.Add(target.Id!))
            {
                throw new InternalValidationException(
                    $"Parameter targets is not valid. Reason: More than one target with Id '{target.Id}' is provided.");
            }
        }

        foreach (var target in targets)
        {
            var arn = target.Arn!;
            if (!TryParseArn(arn, out var service, out var resource))
            {
                throw new InternalValidationException($"Parameter {arn} is not valid. Reason: Provided Arn is not in correct format.");
            }

            if (service == "sns" && resource.EndsWith(".fifo", StringComparison.Ordinal))
            {
                throw new InternalValidationException("SNS FIFO is not a supported service for a target.");
            }

            if (target.DeadLetterConfig?.Arn is { } deadLetterArn
                && TryParseArn(deadLetterArn, out var deadLetterService, out var deadLetterQueue)
                && deadLetterService == "sqs"
                && deadLetterQueue.EndsWith(".fifo", StringComparison.Ordinal))
            {
                throw new InternalValidationException("SQS FIFO is not supported by Dead Letter Queues.");
            }

            if (string.IsNullOrEmpty(target.RoleArn) && RequiresRoleArn(service, resource))
            {
                throw new InternalValidationException($"RoleArn is required for target {arn}.");
            }
        }
    }

    /// <summary>
    /// Reports every member constraint the targets break, in AWS's
    /// "N validation error(s) detected: ...; ..." form.
    /// </summary>
    private static void ThrowConstraintViolations(IReadOnlyList<Target> targets)
    {
        var violations = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var member = $"targets.{i + 1}.member";
            if (target.Id is null)
            {
                violations.Add($"Value null at '{member}.id' failed to satisfy constraint: Member must not be null");
            }
            if (target.Arn is null)
            {
                violations.Add($"Value null at '{member}.arn' failed to satisfy constraint: Member must not be null");
            }

            var retry = target.RetryPolicy;
            if (retry?.MaximumRetryAttempts is { } attempts)
            {
                AddRangeViolation(violations, attempts, 0, MaxRetryAttempts, $"{member}.retryPolicy.maximumRetryAttempts");
            }
            if (retry?.MaximumEventAgeInSeconds is { } age)
            {
                AddRangeViolation(violations, age, MinEventAgeInSeconds, MaxEventAgeInSeconds, $"{member}.retryPolicy.maximumEventAgeInSeconds");
            }
        }

        if (violations.Count > 0)
        {
            var count = violations.Count == 1 ? "1 validation error" : $"{violations.Count} validation errors";
            throw new InternalValidationException($"{count} detected: {string.Join("; ", violations)}");
        }
    }

    private static void AddRangeViolation(List<string> violations, int value, int min, int max, string path)
    {
        if (value < min)
        {
            violations.Add($"Value '{value}' at '{path}' failed to satisfy constraint: Member must have value greater than or equal to {min}");
        }
        else if (value > max)
        {
            violations.Add($"Value '{value}' at '{path}' failed to satisfy constraint: Member must have value less than or equal to {max}");
        }
    }

    /// <summary>Whether a target ARN needs a <c>RoleArn</c>: a role-requiring service, an event bus, or Systems Manager automation.</summary>
    private static bool RequiresRoleArn(string service, string resource) =>
        RoleRequiringServices.Contains(service)
        || (service == "events" && resource.StartsWith("event-bus/", StringComparison.Ordinal))
        || (service == "ssm" && (resource.StartsWith("document/", StringComparison.Ordinal)
                                 || resource.StartsWith("automation-definition/", StringComparison.Ordinal)));

    /// <summary>
    /// Splits <c>arn:partition:service:region:account:resource</c>; partition, service and resource
    /// must be present (region and account may be empty, as for some global resources).
    /// </summary>
    private static bool TryParseArn(string arn, out string service, out string resource)
    {
        var parts = arn.Split([':'], 6);
        if (parts.Length == 6 && parts[0] == "arn" && parts[1].Length > 0 && parts[2].Length > 0 && parts[5].Length > 0)
        {
            service = parts[2];
            resource = parts[5];
            return true;
        }

        service = "";
        resource = "";
        return false;
    }
}
