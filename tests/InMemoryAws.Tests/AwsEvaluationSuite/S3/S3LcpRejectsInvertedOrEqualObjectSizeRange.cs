// Translated from behavioral/s3/check_s3_lcp_rejects_inverted_or_equal_object_size_range.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-lcp-rejects-inverted-or-equal-object-size-range</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3LcpRejectsInvertedOrEqualObjectSizeRange
{
    /// <summary>
    /// PutBucketLifecycleConfiguration refuses an And filter whose ObjectSizeLessThan does not
    /// exceed ObjectSizeGreaterThan (inverted or equal), and leaves the prior configuration unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-lcp-rejects-inverted-or-equal-object-size-range")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await NewBucket(ctx, s3);

        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = BaselineRules() } });
        var baseline = BaselineRules().Select(S3Fidelity.Boto).ToList();
        var settled = await Settled(
            () => LifecycleRules(s3, bucket),
            rules => Py.Equal(rules, baseline),
            description: $"the baseline configuration on {bucket}");
        Ensure(Py.Equal(settled, baseline), $"baseline did not settle to {Py.Repr(baseline)}, got {Py.Repr(settled)}");

        const string expectedMessage = "'ObjectSizeLessThan' has to be a value greater than 'ObjectSizeGreaterThan'.";

        var inverted = new LifecycleConfiguration { Rules = [SizeRule("inverted", 1000, 100)] };
        var (code, message) = await Error(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = inverted }));
        Ensure(code == "InvalidRequest", $"inverted size range got code {Py.Repr(code)}, expected InvalidRequest");
        Ensure(message == expectedMessage, $"inverted size range message {Py.Repr(message)} != {Py.Repr(expectedMessage)}");

        var rules = await LifecycleRules(s3, bucket);
        Ensure(
            Py.Equal(rules, baseline),
            $"after the rejected inverted-range PUT, lifecycle rules are {Py.Repr(rules)}; "
            + $"expected the baseline {Py.Repr(baseline)} to be unchanged");

        var equalRange = new LifecycleConfiguration { Rules = [SizeRule("equalrange", 1000, 1000)] };
        (code, message) = await Error(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = equalRange }));
        Ensure(code == "InvalidRequest", $"equal size range got code {Py.Repr(code)}, expected InvalidRequest");
        Ensure(message == expectedMessage, $"equal size range message {Py.Repr(message)} != {Py.Repr(expectedMessage)}");

        rules = await LifecycleRules(s3, bucket);
        Ensure(
            Py.Equal(rules, baseline),
            $"after the rejected equal-range PUT, lifecycle rules are {Py.Repr(rules)}; "
            + $"expected the baseline {Py.Repr(baseline)} to be unchanged");

        // Positive control: the same shape with a true gap (100 < 1000) is accepted and replaces the
        // baseline, proving the rejection above was about the range and not the And/size filter itself.
        var validRules = new List<LifecycleRule> { SizeRule("validrange", 100, 1000) };
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = validRules } });
        var valid = validRules.Select(S3Fidelity.Boto).ToList();
        settled = await Settled(
            () => LifecycleRules(s3, bucket),
            got => Py.Equal(got, valid),
            description: $"the valid-range configuration on {bucket}");
        Ensure(Py.Equal(settled, valid), $"valid size range did not settle to {Py.Repr(valid)}, got {Py.Repr(settled)}");
    }

    private static List<LifecycleRule> BaselineRules() =>
    [
        new LifecycleRule
        {
            Id = "baseline",
            Status = LifecycleRuleStatus.Enabled,
            Filter = new LifecycleFilter { Prefix = "keep/" },
            Expiration = new LifecycleRuleExpiration { Days = 10 },
        },
    ];

    private static LifecycleRule SizeRule(string id, long greaterThan, long lessThan) => new()
    {
        Id = id,
        Status = LifecycleRuleStatus.Enabled,
        Filter = new LifecycleFilter { And = new LifecycleRuleAndOperator { ObjectSizeGreaterThan = greaterThan, ObjectSizeLessThan = lessThan } },
        Expiration = new LifecycleRuleExpiration { Days = 5 },
    };

    /// <summary>Create a uniquely-named bucket and register its teardown; return its name.</summary>
    private static async Task<string> NewBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-lcp-");
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(async () =>
        {
            if (await FidelityContext.CaptureError(() => s3.DeleteBucketAsync(name)) is { Code: not "NoSuchBucket" } error)
            {
                throw error.Exception;
            }
        });
        return name;
    }

    /// <summary>The bucket's current lifecycle Rules list, or None when no configuration is set.</summary>
    private static async Task<List<Dictionary<string, object?>>?> LifecycleRules(IAmazonS3 s3, string bucket)
    {
        var (rules, code) = await S3Fidelity.LifecycleRules(s3, bucket);
        if (code is not null && code != "NoSuchLifecycleConfiguration")
        {
            throw Fail($"GetBucketLifecycleConfiguration failed: {code}");
        }

        return rules;
    }

    /// <summary>
    /// Poll until `streak` consecutive reads satisfy predicate; return the last read.
    /// Lifecycle reads are eventually consistent and not monotonic, so one matching read is not
    /// convergence.
    /// </summary>
    private static async Task<T> Settled<T>(Func<Task<T>> probe, Func<T, bool> predicate, string description, double timeoutS = 90.0, double intervalS = 1.0, int streak = 5)
    {
        var clock = FidelityTime.StartNew();
        var run = 0;
        T last = default!;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            run = predicate(last) ? run + 1 : 0;
            if (run >= streak)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not settled within {Py.Repr(timeoutS)}s. Last read: {Py.Repr(last)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static Task<(string? Code, string? Message)> Error(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected the call to be rejected, but it succeeded");
}
