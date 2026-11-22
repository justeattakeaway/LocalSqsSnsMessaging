// Translated from behavioral/s3/check_bucket_policy_over_twenty_kb_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-policy-over-twenty-kb-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketPolicyOverTwentyKbRejected
{
    /// <summary>Bucket policies are limited to a fixed, non-adjustable 20480-byte (20 KB) normalized size.</summary>
    private const int MaxBytes = 20480;

    /// <summary>S3's refusal of a bucket policy document past that cap, naming the cap itself.</summary>
    private static readonly (string? Code, string? Message) PolicyTooLarge =
        ("MalformedPolicy", $"Normalized policy document exceeds the maximum allowed size of {MaxBytes} bytes");

    /// <summary>
    /// PutBucketPolicy accepts a small policy and rejects one whose normalized size exceeds the
    /// fixed, non-adjustable 20480-byte cap, without replacing the policy already in place.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-policy-over-twenty-kb-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        var small = Policy(ctx, bucket);
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = small });
        ctx.OnCleanup(() => s3.DeleteBucketPolicyAsync(bucket));
        var stored = (await s3.GetBucketPolicyAsync(bucket)).Policy;
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(small)), $"a small policy did not round-trip: {Py.Repr(stored)}");

        // Comfortably past the 20480-byte cap, clear of AWS's own normalization of this request shape.
        var oversized = Policy(ctx, bucket, fillerLen: 2 * MaxBytes);
        var got = await S3Fidelity.ErrorPair(() => s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = oversized }));
        Ensure(got == PolicyTooLarge, $"expected {S3Fidelity.Repr(PolicyTooLarge)} for an oversized policy, got {S3Fidelity.Repr(got)}");

        var survived = (await s3.GetBucketPolicyAsync(bucket)).Policy;
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(survived), JsonNode.Parse(small)),
            $"the rejected oversized PutBucketPolicy must not have replaced the existing policy, got {Py.Repr(survived)}");
    }

    /// <summary><c>_policy(ctx, bucket, filler_len=...)</c>: a valid self-granting bucket policy, optionally padded with a filler Sid.</summary>
    private static string Policy(FidelityContext ctx, string bucket, int fillerLen = 0) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = new string('f', fillerLen),
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{ctx.Account}:root" },
                ["Action"] = "s3:GetObject",
                ["Resource"] = $"arn:aws:s3:::{bucket}/*",
            }),
        });
}
