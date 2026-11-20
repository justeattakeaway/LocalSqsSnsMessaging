using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>A bucket's event notification settings.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>
    /// The notification configuration in force, as stored (every configuration with an id, filter
    /// rule names as S3 answers them); null for a bucket never configured, or configured empty.
    /// </summary>
    public NotificationConfiguration? Notifications { get; set; }

    /// <summary>The last sequencer handed to an object event of this bucket, as a number.</summary>
    public long LastSequencer { get; set; }
}

/// <summary>
/// S3's rules for a notification configuration (event types, key filters, overlapping
/// configurations, destination ARNs) and for which configurations an object event matches.
/// </summary>
internal static class S3NotificationRules
{
    /// <summary>The event types a configuration may name.</summary>
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        "s3:ReducedRedundancyLostObject",
        "s3:ObjectCreated:*",
        "s3:ObjectCreated:Put",
        "s3:ObjectCreated:Post",
        "s3:ObjectCreated:Copy",
        "s3:ObjectCreated:CompleteMultipartUpload",
        "s3:ObjectRemoved:*",
        "s3:ObjectRemoved:Delete",
        "s3:ObjectRemoved:DeleteMarkerCreated",
        "s3:ObjectRestore:*",
        "s3:ObjectRestore:Post",
        "s3:ObjectRestore:Completed",
        "s3:ObjectRestore:Delete",
        "s3:Replication:*",
        "s3:Replication:OperationFailedReplication",
        "s3:Replication:OperationNotTracked",
        "s3:Replication:OperationMissedThreshold",
        "s3:Replication:OperationReplicatedAfterThreshold",
        "s3:LifecycleTransition",
        "s3:IntelligentTiering",
        "s3:ObjectAcl:Put",
        "s3:LifecycleExpiration:*",
        "s3:LifecycleExpiration:Delete",
        "s3:LifecycleExpiration:DeleteMarkerCreated",
        "s3:ObjectTagging:*",
        "s3:ObjectTagging:Put",
        "s3:ObjectTagging:Delete",
    };

    /// <summary>The pattern SNS topic ARNs must match (which a FIFO topic's <c>.fifo</c> name does not).</summary>
    public const string TopicArnPattern = "^arn:aws[\\p{Alpha}\\-]*:sns:[\\p{Alnum}\\-]+:[\\p{Digit}]{12}:[\\p{Alnum}\\-_]{1,256}$";

    private static readonly Regex TopicArn = new("^arn:aws[A-Za-z\\-]*:sns:[A-Za-z0-9\\-]+:[0-9]{12}:[A-Za-z0-9\\-_]{1,256}$", RegexOptions.CultureInvariant);

    /// <summary>One configuration of any destination type, as validation and matching see it.</summary>
    internal sealed record Entry(string? Id, string Kind, string? Arn, List<string>? Events, NotificationConfigurationFilter? Filter);

    /// <summary>Every queue, topic and Lambda configuration of <paramref name="configuration"/>, in that order.</summary>
    public static IEnumerable<Entry> Entries(NotificationConfiguration configuration)
    {
        foreach (var topic in configuration.TopicConfigurations ?? [])
        {
            yield return new Entry(topic.Id, "Topic", topic.TopicArn, topic.Events, topic.Filter);
        }

        foreach (var queue in configuration.QueueConfigurations ?? [])
        {
            yield return new Entry(queue.Id, "Queue", queue.QueueArn, queue.Events, queue.Filter);
        }

        foreach (var function in configuration.LambdaFunctionConfigurations ?? [])
        {
            yield return new Entry(function.Id, "CloudFunction", function.LambdaFunctionArn, function.Events, function.Filter);
        }
    }

    /// <summary>
    /// Checks a configuration's shape as S3 does before it looks at any destination: every
    /// configuration names a destination and at least one supported event, its filter has at most
    /// one prefix and one suffix rule, ids are unique, ARNs are well formed (FIFO queues and topics
    /// are refused), and no two configurations share an event type for keys both of their filters
    /// admit. Fills in the ids S3 generates and normalizes filter rule names.
    /// </summary>
    public static void Validate(NotificationConfiguration configuration, string bucketRegion)
    {
        var entries = Entries(configuration).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Arn) || entry.Events is not { Count: > 0 })
            {
                throw S3Errors.MalformedXml();
            }

            foreach (var name in entry.Events)
            {
                if (!EventTypes.Contains(name))
                {
                    throw S3Errors.InvalidArgument("The event is not supported for notifications", "Event", name);
                }
            }

            CheckFilter(entry.Filter);
            if (entry.Id is { } id && !ids.Add(id))
            {
                throw S3Errors.InvalidArgument("Same ID used for multiple configurations. IDs must be unique", "Id", id);
            }

            CheckArn(entry, bucketRegion);
        }

        CheckOverlaps(entries);
        AssignIds(configuration, ids);
    }

    /// <summary>A filter's rules: <c>prefix</c> and <c>suffix</c> (in any case), each at most once.</summary>
    private static void CheckFilter(NotificationConfigurationFilter? filter)
    {
        var rules = filter?.Key?.FilterRules;
        if (rules is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var name = rule.Name?.ToUpperInvariant() switch
            {
                "PREFIX" => "Prefix",
                "SUFFIX" => "Suffix",
                _ => throw S3Errors.InvalidArgument("filter rule name must be either prefix or suffix", "FilterRule.Name", rule.Name),
            };
            if (!seen.Add(name))
            {
                throw S3Errors.InvalidArgument(
                    name == "Prefix" ? "Cannot specify more than one prefix rule in a filter." : "Cannot specify more than one suffix rule in a filter.", "FilterRule.Name", rule.Name);
            }

            if ((rule.Value ?? string.Empty).Length > 1024)
            {
                throw S3Errors.InvalidArgument("The value of the filter rule is too long", "FilterRule.Value", rule.Value);
            }

            rule.Name = name;
            rule.Value ??= string.Empty;
        }
    }

    /// <summary>
    /// A destination ARN: a standard SQS queue or SNS topic in the bucket's region. A FIFO queue is
    /// refused by name, and a FIFO topic fails the topic ARN pattern; Lambda functions are checked
    /// only when the destinations are validated.
    /// </summary>
    private static void CheckArn(Entry entry, string bucketRegion)
    {
        var value = entry.Arn!;
        switch (entry.Kind)
        {
            case "Queue":
                if (!Arn.TryParse(value, out var queue) || queue.Service != "sqs" || queue.Resource.Length == 0)
                {
                    throw S3Errors.InvalidArgument("The ARN is not well formed", "Queue", value);
                }

                if (queue.Resource.EndsWith(".fifo", StringComparison.Ordinal))
                {
                    throw S3Errors.InvalidArgument("FIFO SQS queues are not supported.", "Queue", value);
                }

                CheckRegion(queue, value, "Queue", bucketRegion);
                break;
            case "Topic":
                if (!TopicArn.IsMatch(value) || !Arn.TryParse(value, out var topic))
                {
                    throw S3Errors.InvalidArgument(
                        "The ARN failed to satisfy constraint: Member must satisfy regular expression pattern: " + TopicArnPattern, "Topic", value);
                }

                CheckRegion(topic, value, "Topic", bucketRegion);
                break;
            default:
                if (!Arn.TryParse(value, out var function) || function.Service != "lambda")
                {
                    throw S3Errors.InvalidArgument("The ARN is not well formed", "CloudFunction", value);
                }

                break;
        }
    }

    private static void CheckRegion(Arn arn, string value, string kind, string bucketRegion)
    {
        if (!string.Equals(arn.Region, bucketRegion, StringComparison.Ordinal))
        {
            throw S3Errors.InvalidArgument("The notification destination service region is not valid for the bucket location constraint", kind, value);
        }
    }

    /// <summary>
    /// Two configurations overlap when they share an event type (a wildcard covers its family) and
    /// some key would pass both filters: their prefixes overlap (one starts with the other, or
    /// either has none) and so do their suffixes.
    /// </summary>
    private static void CheckOverlaps(List<Entry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                var (a, b) = (entries[i], entries[j]);
                if (!FiltersOverlap(a.Filter, b.Filter))
                {
                    continue;
                }

                foreach (var first in a.Events!)
                {
                    foreach (var second in b.Events!)
                    {
                        if (!EventsOverlap(first, second))
                        {
                            continue;
                        }

                        var unfiltered = Rules(a.Filter).Count == 0 && Rules(b.Filter).Count == 0;
                        throw S3Errors.InvalidArgument(
                            unfiltered
                                ? "Configurations overlap. Configurations on the same bucket cannot share a common event type."
                                : "Configuration is ambiguously defined. Cannot have overlapping suffixes in two rules if the prefixes are overlapping for the same event type.",
                            "Event",
                            $"{first}, {second}");
                    }
                }
            }
        }
    }

    private static bool EventsOverlap(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) || Covers(a, b) || Covers(b, a);

    /// <summary>Whether the wildcard <paramref name="pattern"/> (<c>s3:ObjectCreated:*</c>) covers <paramref name="name"/>.</summary>
    private static bool Covers(string pattern, string name) =>
        pattern.EndsWith(":*", StringComparison.Ordinal) && name.StartsWith(pattern[..^1], StringComparison.Ordinal);

    private static bool FiltersOverlap(NotificationConfigurationFilter? a, NotificationConfigurationFilter? b)
    {
        var (prefixA, suffixA) = PrefixAndSuffix(a);
        var (prefixB, suffixB) = PrefixAndSuffix(b);
        return (prefixA.StartsWith(prefixB, StringComparison.Ordinal) || prefixB.StartsWith(prefixA, StringComparison.Ordinal))
            && (suffixA.EndsWith(suffixB, StringComparison.Ordinal) || suffixB.EndsWith(suffixA, StringComparison.Ordinal));
    }

    private static List<FilterRule> Rules(NotificationConfigurationFilter? filter) => filter?.Key?.FilterRules ?? [];

    /// <summary>A filter's prefix and suffix; empty for a rule it doesn't have.</summary>
    private static (string Prefix, string Suffix) PrefixAndSuffix(NotificationConfigurationFilter? filter)
    {
        var prefix = string.Empty;
        var suffix = string.Empty;
        foreach (var rule in Rules(filter))
        {
            if (string.Equals(rule.Name, "Prefix", StringComparison.OrdinalIgnoreCase))
            {
                prefix = rule.Value ?? string.Empty;
            }
            else
            {
                suffix = rule.Value ?? string.Empty;
            }
        }

        return (prefix, suffix);
    }

    /// <summary>S3 gives each configuration sent without an id one of its own.</summary>
    private static void AssignIds(NotificationConfiguration configuration, HashSet<string> taken)
    {
        string NewId()
        {
            string id;
            do
            {
                id = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
            }
            while (!taken.Add(id));
            return id;
        }

        foreach (var topic in configuration.TopicConfigurations ?? [])
        {
            topic.Id ??= NewId();
        }

        foreach (var queue in configuration.QueueConfigurations ?? [])
        {
            queue.Id ??= NewId();
        }

        foreach (var function in configuration.LambdaFunctionConfigurations ?? [])
        {
            function.Id ??= NewId();
        }
    }

    /// <summary>
    /// Whether a configuration delivers an event: one of its event types is the event's
    /// (<c>s3:</c> plus <paramref name="eventName"/>, e.g. <c>ObjectCreated:Put</c>) or a wildcard
    /// over it, and the key passes its filter.
    /// </summary>
    public static bool Matches(List<string>? events, NotificationConfigurationFilter? filter, string eventName, string key)
    {
        var type = "s3:" + eventName;
        if (events is null || !events.Exists(e => string.Equals(e, type, StringComparison.Ordinal) || Covers(e, type)))
        {
            return false;
        }

        var (prefix, suffix) = PrefixAndSuffix(filter);
        return key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal of destinations S3 could not validate: one <c>ArgumentName</c>/<c>ArgumentValue</c>
    /// pair per destination, numbered from 1, naming its ARN and why.
    /// </summary>
    public static S3Exception Unvalidated(IReadOnlyList<(string Arn, string Reason)> failures)
    {
        var details = new List<(string, string?)>();
        for (var i = 0; i < failures.Count; i++)
        {
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            details.Add(("ArgumentName" + n, failures[i].Arn));
            details.Add(("ArgumentValue" + n, failures[i].Reason));
        }

        return new S3Exception("InvalidArgument", "Unable to validate the following destination configurations", HttpStatusCode.BadRequest, [.. details]);
    }
}
