using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the lifecycle and bucket-configuration ports share: lifecycle rules as the dicts boto3
/// hands the checks (so they compare with <see cref="Py.Equal"/> as the Python's <c>==</c> does),
/// reading the lifecycle configuration or the error it raises, and a response's raw headers
/// (botocore's <c>ResponseMetadata.HTTPHeaders</c>).
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>s3.get_bucket_lifecycle_configuration(Bucket=bucket)["Rules"]</c> as boto3 dicts, or the
    /// error code it raises.
    /// </summary>
    public static async Task<(List<Dictionary<string, object?>>? Rules, string? ErrorCode)> LifecycleRules(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var (response, error) = await FidelityContext.Capture(() => s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket }));
        if (response is null)
        {
            return (null, error?.Code);
        }

        // The .NET SDK answers NoSuchLifecycleConfiguration as a configuration with no rules, which S3
        // never stores (a configuration holds at least one rule).
        return response.Configuration?.Rules is { Count: > 0 } rules ? ([.. rules.Select(Boto)], null) : (null, "NoSuchLifecycleConfiguration");
    }

    /// <summary>
    /// The storage-class checks' <c>_canon(rules)</c>: each rule's ID, Status, Filter and Transitions
    /// (sorted by Days), the rules sorted by ID.
    /// </summary>
    public static List<Dictionary<string, object?>> CanonTransitions(IEnumerable<Dictionary<string, object?>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return [.. rules.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ID"] = r["ID"],
            ["Status"] = r["Status"],
            ["Filter"] = r.GetValueOrDefault("Filter"),
            ["Transitions"] = ((r.GetValueOrDefault("Transitions") as List<Dictionary<string, object?>>) ?? [])
                .OrderBy(t => (int)t["Days"]!).ToList(),
        }).OrderBy(r => (string)r["ID"]!, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The storage-class checks' <c>_poll_rules</c>: lifecycle configuration propagates eventually,
    /// so poll for the stored value (canonical, or the error code), never time it.
    /// </summary>
    public static async Task<object?> PollCanonRules(IAmazonS3 s3, string bucket, List<Dictionary<string, object?>> expected, int attempts = 20, double delay = 3.0)
    {
        object? seen = null;
        for (var i = 0; i < attempts; i++)
        {
            var (rules, code) = await LifecycleRules(s3, bucket);
            seen = rules is null ? code : CanonTransitions(rules);
            if (Py.Equal(seen, expected))
            {
                return seen;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(delay));
        }

        return seen;
    }

    /// <summary>The storage-class checks' <c>_rules(s3, bucket)</c>: the canonical stored rules.</summary>
    public static async Task<List<Dictionary<string, object?>>> CanonRules(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var response = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        return CanonTransitions((response.Configuration?.Rules ?? []).Select(Boto));
    }

    /// <summary>
    /// The storage-class checks' <c>_poll(ctx, fn, done)</c>: calls <paramref name="fn"/> every 2 s until
    /// <paramref name="done"/> holds or a 60 s deadline capped by the time left.
    /// </summary>
    public static async Task<T> Poll<T>(FidelityContext ctx, Func<Task<T>> fn, Func<T, bool> done, double timeoutS = 60.0)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(fn);
        ArgumentNullException.ThrowIfNull(done);
        var deadline = Math.Min(timeoutS, ctx.TimeLeftSeconds);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var result = await fn();
            if (done(result) || clock.Elapsed.TotalSeconds >= deadline)
            {
                return result;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A lifecycle rule as boto3 parses it: a dict with only the members the response carried.</summary>
    public static Dictionary<string, object?> Boto(LifecycleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rule.Expiration is { } expiration)
        {
            var e = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (expiration.Date is { } date)
            {
                e["Date"] = date;
            }

            if (expiration.Days is { } days)
            {
                e["Days"] = days;
            }

            if (expiration.ExpiredObjectDeleteMarker is { } marker)
            {
                e["ExpiredObjectDeleteMarker"] = marker;
            }

            dict["Expiration"] = e;
        }

        if (rule.Id is { } id)
        {
            dict["ID"] = id;
        }

        if (rule.Prefix is { } prefix)
        {
            dict["Prefix"] = prefix;
        }

        if (rule.Filter is { } filter)
        {
            dict["Filter"] = Boto(filter);
        }

        if (rule.Status is { } status)
        {
            dict["Status"] = status.Value;
        }

        if (rule.Transitions is { Count: > 0 } transitions)
        {
            dict["Transitions"] = transitions.Select(t =>
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (t.Date is { } date)
                {
                    d["Date"] = date;
                }

                if (t.Days is { } days)
                {
                    d["Days"] = days;
                }

                if (t.StorageClass is { } storageClass)
                {
                    d["StorageClass"] = storageClass.Value;
                }

                return d;
            }).ToList();
        }

        if (rule.NoncurrentVersionExpiration is { } noncurrent)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (noncurrent.NoncurrentDays is { } days)
            {
                d["NoncurrentDays"] = days;
            }

            if (noncurrent.NewerNoncurrentVersions is { } newer)
            {
                d["NewerNoncurrentVersions"] = newer;
            }

            dict["NoncurrentVersionExpiration"] = d;
        }

        if (rule.AbortIncompleteMultipartUpload is { } abort)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (abort.DaysAfterInitiation is { } days)
            {
                d["DaysAfterInitiation"] = days;
            }

            dict["AbortIncompleteMultipartUpload"] = d;
        }

        return dict;
    }

    /// <summary>A lifecycle filter as boto3 parses it.</summary>
    public static Dictionary<string, object?> Boto(LifecycleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
#pragma warning disable CS0618 // The SDK may fill either the predicate or the members.
        switch (filter.LifecycleFilterPredicate)
        {
            case LifecyclePrefixPredicate p:
                dict["Prefix"] = p.Prefix;
                break;
            case LifecycleTagPredicate t:
                dict["Tag"] = Boto(t.Tag);
                break;
            case LifecycleObjectSizeGreaterThanPredicate g:
                dict["ObjectSizeGreaterThan"] = g.ObjectSizeGreaterThan;
                break;
            case LifecycleObjectSizeLessThanPredicate l:
                dict["ObjectSizeLessThan"] = l.ObjectSizeLessThan;
                break;
            case LifecycleAndOperator and:
                var a = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var operand in and.Operands ?? [])
                {
                    switch (operand)
                    {
                        case LifecyclePrefixPredicate p:
                            a["Prefix"] = p.Prefix;
                            break;
                        case LifecycleTagPredicate t:
                            if (!a.TryGetValue("Tags", out var tags))
                            {
                                a["Tags"] = tags = new List<object?>();
                            }

                            ((List<object?>)tags!).Add(Boto(t.Tag));
                            break;
                        case LifecycleObjectSizeGreaterThanPredicate g:
                            a["ObjectSizeGreaterThan"] = g.ObjectSizeGreaterThan;
                            break;
                        case LifecycleObjectSizeLessThanPredicate l:
                            a["ObjectSizeLessThan"] = l.ObjectSizeLessThan;
                            break;
                    }
                }

                dict["And"] = a;
                break;
        }
#pragma warning restore CS0618

        if (filter.Prefix is { } prefix)
        {
            dict["Prefix"] = prefix;
        }

        if (filter.Tag is { } tag)
        {
            dict["Tag"] = Boto(tag);
        }

        if (filter.ObjectSizeGreaterThan is { } greater)
        {
            dict["ObjectSizeGreaterThan"] = greater;
        }

        if (filter.ObjectSizeLessThan is { } less)
        {
            dict["ObjectSizeLessThan"] = less;
        }

        if (filter.And is { } andOperator)
        {
            var a = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (andOperator.Prefix is { } andPrefix)
            {
                a["Prefix"] = andPrefix;
            }

            if (andOperator.Tags is { Count: > 0 } tags)
            {
                a["Tags"] = tags.Select(t => (object?)Boto(t)).ToList();
            }

            if (andOperator.ObjectSizeGreaterThan is { } g)
            {
                a["ObjectSizeGreaterThan"] = g;
            }

            if (andOperator.ObjectSizeLessThan is { } l)
            {
                a["ObjectSizeLessThan"] = l;
            }

            dict["And"] = a;
        }

        return dict;
    }

    /// <summary>
    /// A metrics configuration's <c>Filter</c> as boto3 parses it: <c>{"Prefix": ...}</c>,
    /// <c>{"Tag": {...}}</c>, <c>{"AccessPointArn": ...}</c> or <c>{"And": {"Prefix": ..., "Tags": [...]}}</c>;
    /// null when the configuration has no filter.
    /// </summary>
    public static Dictionary<string, object?>? Boto(MetricsFilter? filter)
    {
        if (filter?.MetricsFilterPredicate is not { } predicate)
        {
            return null;
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (predicate)
        {
            case MetricsPrefixPredicate p:
                dict["Prefix"] = p.Prefix;
                break;
            case MetricsTagPredicate t:
                dict["Tag"] = Boto(t.Tag);
                break;
            case MetricsAccessPointArnPredicate a:
                dict["AccessPointArn"] = a.AccessPointArn;
                break;
            case MetricsAndOperator and:
                var a2 = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var operand in and.Operands ?? [])
                {
                    switch (operand)
                    {
                        case MetricsPrefixPredicate p:
                            a2["Prefix"] = p.Prefix;
                            break;
                        case MetricsTagPredicate t:
                            if (!a2.TryGetValue("Tags", out var tags))
                            {
                                a2["Tags"] = tags = new List<object?>();
                            }

                            ((List<object?>)tags!).Add(Boto(t.Tag));
                            break;
                        case MetricsAccessPointArnPredicate ap:
                            a2["AccessPointArn"] = ap.AccessPointArn;
                            break;
                    }
                }

                dict["And"] = a2;
                break;
        }

        return dict;
    }

    /// <summary>A tag as boto3 parses it: <c>{"Key": ..., "Value": ...}</c>.</summary>
    public static Dictionary<string, object?> Boto(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["Key"] = tag.Key, ["Value"] = tag.Value };
    }

    /// <summary>
    /// Runs <paramref name="call"/> on <paramref name="s3"/> and returns its response with the raw
    /// response headers, names lower-cased: botocore's <c>ResponseMetadata.HTTPHeaders</c>.
    /// </summary>
    public static async Task<(T Response, Dictionary<string, string> Headers)> WithHeaders<T>(AmazonS3Client s3, Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(call);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        void OnResponse(object sender, ResponseEventArgs e)
        {
            if (e is WebServiceResponseEventArgs response)
            {
                headers.Clear();
                foreach (var (name, value) in response.ResponseHeaders)
                {
                    headers[name.ToLowerInvariant()] = value;
                }
            }
        }

        s3.AfterResponseEvent += OnResponse;
        try
        {
            var result = await call();
            return (result, headers);
        }
        finally
        {
            s3.AfterResponseEvent -= OnResponse;
        }
    }

    /// <summary>
    /// The expiry date in an <c>x-amz-expiration</c> header value, such as
    /// <c>expiry-date="Fri, 21 Dec 2012 00:00:00 GMT", rule-id="..."</c>.
    /// </summary>
    public static DateTimeOffset ExpiryDate(string expirationHeader)
    {
        ArgumentNullException.ThrowIfNull(expirationHeader);
        const string marker = "expiry-date=\"";
        var start = expirationHeader.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = expirationHeader.IndexOf('"', start);
        return DateTimeOffset.Parse(expirationHeader[start..end], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
    }
}
