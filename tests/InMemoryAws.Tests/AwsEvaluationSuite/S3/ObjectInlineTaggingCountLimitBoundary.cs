// Translated from behavioral/s3/check_object_inline_tagging_count_limit_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-inline-tagging-count-limit-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectInlineTaggingCountLimitBoundary
{
    private static readonly (string? Code, string? Message) TooManyTags = ("BadRequest", "Object tags cannot be greater than 10");

    /// <summary>
    /// PutObject with 11 inline tags is rejected with its exact code and message and leaves no
    /// object, while exactly 10 inline tags are stored and read back by GetObjectTagging.
    /// </summary>
    [Test]
    [DisplayName("s3.object-inline-tagging-count-limit-boundary")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var atLimitHeader = string.Join("&", Enumerable.Range(0, 10).Select(i => $"k{i}=v{i}"));
        const string atLimitKey = "at-limit.txt";
        await PutTagged(s3, bucket, atLimitKey, atLimitHeader);
        var got = await Tags(s3, bucket, atLimitKey);
        var want = Enumerable.Range(0, 10).ToDictionary(i => $"k{i}", i => (object?)$"v{i}", StringComparer.Ordinal);
        Ensure(Py.Equal(got, want), $"expected all 10 tags to be stored, got {Py.Repr(got)}");

        var overLimitHeader = string.Join("&", Enumerable.Range(0, 11).Select(i => $"k{i}=v{i}"));
        const string rejectedKey = "over-limit.txt";
        await ExpectError(() => PutTagged(s3, bucket, rejectedKey, overLimitHeader), TooManyTags);

        var listed = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = rejectedKey })).S3Objects ?? [];
        Ensure(listed.Count == 0, $"rejected PutObject unexpectedly created {Py.Repr(rejectedKey)}: {Py.Repr(listed.Select(o => o.Key).ToList())}");
    }

    /// <summary><c>s3.put_object(Bucket=..., Key=..., Body=b"content", Tagging=header)</c>.</summary>
    private static Task<PutObjectResponse> PutTagged(IAmazonS3 s3, string bucket, string key, string tagging)
    {
        // The SDK writes TagSet as the x-amz-tagging header, query-encoded as the check writes it.
        var tagSet = tagging.Split('&').Select(pair => pair.Split('=', 2)).Select(kv => new Tag { Key = kv[0], Value = kv[1] }).ToList();
        return s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "content", TagSet = tagSet });
    }

    /// <summary><c>{t["Key"]: t["Value"] for t in s3.get_object_tagging(...).get("TagSet", [])}</c>.</summary>
    private static async Task<Dictionary<string, object?>> Tags(IAmazonS3 s3, string bucket, string key) =>
        ((await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging ?? [])
            .ToDictionary(t => t.Key, t => (object?)t.Value, StringComparer.Ordinal);

    /// <summary><c>expect_error(fn, code, message)</c>: <paramref name="call"/> fails with exactly this code and message.</summary>
    private static async Task ExpectError(Func<Task> call, (string? Code, string? Message) expected)
    {
        var error = await FidelityContext.CaptureError(call)
            ?? throw Fail($"expected {S3Fidelity.Repr(expected)}, but the call succeeded");
        var got = (error.Code, error.Message);
        Ensure(got == expected, $"expected {S3Fidelity.Repr(expected)}, got {S3Fidelity.Repr(got)}");
    }
}
