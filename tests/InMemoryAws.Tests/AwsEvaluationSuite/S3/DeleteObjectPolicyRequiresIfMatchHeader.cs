// Translated from behavioral/s3/check_delete_object_policy_requires_if_match_header.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-object-policy-requires-if-match-header</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectPolicyRequiresIfMatchHeader
{
    private const string Check = "delete-object-policy-requires-if-match-header";
    private const string WrongETag = "\"deadbeefdeadbeefdeadbeefdeadbeef\"";
    private const string PreconditionMessage = "At least one of the pre-conditions you specified did not hold";

    /// <summary>
    /// A bucket policy denying DeleteObject whenever the if-match header is absent
    /// rejects a plain delete, rejects a delete with a stale ETag, and accepts a delete
    /// carrying the object's current ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-object-policy-requires-if-match-header")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var caller = ctx.CallerArn;
        var bucket = await S3Fidelity.MakeTaggedBucket(ctx, "sim2real-behav-delifm-", Check);
        const string key = "object.txt";
        var etag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "conditionally-deleted" })).ETag;
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "DenyDeleteWithoutIfMatch",
                ["Effect"] = "Deny",
                ["Principal"] = new JsonObject { ["AWS"] = ctx.Account },
                ["Action"] = "s3:DeleteObject",
                ["Resource"] = $"arn:aws:s3:::{bucket}/*",
                ["Condition"] = new JsonObject { ["Null"] = new JsonObject { ["s3:if-match"] = "true" } },
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy.ToJsonString() });
        var wantDenied = (
            "AccessDenied",
            $"User: {caller} is not authorized to perform: s3:DeleteObject on resource: " +
            $"\"arn:aws:s3:::{bucket}/{key}\" with an explicit deny in a resource-based policy");

        // The deny applies to an absent key too, so propagation is proven without touching the object.
        await S3Fidelity.AwaitPolicy(
            ctx,
            n => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = $"probe-{n}.txt" }),
            expectedCode: "AccessDenied");

        var denied = await S3Fidelity.Error(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }));
        Ensure((denied.Code, denied.Message) == wantDenied, $"expected {S3Fidelity.Repr(wantDenied)}, got {S3Fidelity.Repr((denied.Code, denied.Message))}");
        Ensure((await s3.GetObjectMetadataAsync(bucket, key)).ETag == etag, "the plain delete must be denied before it reaches the object");

        var stale = await S3Fidelity.Error(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, IfMatch = WrongETag }));
        Ensure(
            (stale.Code, stale.Message) == ("PreconditionFailed", PreconditionMessage),
            $"a present but stale If-Match must fail the conditional check, got {S3Fidelity.Repr((stale.Code, stale.Message))}");
        Ensure((await s3.GetObjectMetadataAsync(bucket, key)).ETag == etag, "the stale-ETag delete must leave the object untouched");

        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, IfMatch = etag });
        var gone = await S3Fidelity.HeadError(s3, bucket, key);
        Ensure(gone.Code == "404", $"a delete carrying the current ETag must succeed, got {S3Fidelity.Repr(gone)}");
    }
}
