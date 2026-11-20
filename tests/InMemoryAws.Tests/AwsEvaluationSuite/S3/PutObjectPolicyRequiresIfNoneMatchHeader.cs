// Translated from behavioral/s3/check_put_object_policy_requires_if_none_match_header.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-policy-requires-if-none-match-header</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectPolicyRequiresIfNoneMatchHeader
{
    private const string Check = "put-object-policy-requires-if-none-match-header";

    /// <summary>
    /// A bucket policy denying PutObject whenever the if-none-match header is absent
    /// rejects a plain PutObject, and the identical write with If-None-Match: * succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-policy-requires-if-none-match-header")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var caller = ctx.CallerArn;
        var bucket = await S3Fidelity.MakeTaggedBucket(ctx, "sim2real-behav-putcond-", Check);
        const string key = "object.txt";
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "DenyPutWithoutIfNoneMatch",
                ["Effect"] = "Deny",
                ["Principal"] = new JsonObject { ["AWS"] = ctx.Account },
                ["Action"] = "s3:PutObject",
                ["Resource"] = $"arn:aws:s3:::{bucket}/*",
                ["Condition"] = new JsonObject { ["Null"] = new JsonObject { ["s3:if-none-match"] = "true" } },
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy.ToJsonString() });
        var wantDenied = (
            "AccessDenied",
            $"User: {caller} is not authorized to perform: s3:PutObject on resource: " +
            $"\"arn:aws:s3:::{bucket}/{key}\" with an explicit deny in a resource-based policy");
        await S3Fidelity.AwaitPolicy(
            ctx,
            n => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = $"probe-{n}.txt", ContentBody = "probe" }),
            expectedCode: "AccessDenied");

        var denied = await S3Fidelity.Error(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "plain-write" }));
        Ensure((denied.Code, denied.Message) == wantDenied, $"expected {S3Fidelity.Repr(wantDenied)}, got {S3Fidelity.Repr((denied.Code, denied.Message))}");
        var absent = await S3Fidelity.HeadError(s3, bucket, key);
        Ensure(absent.Code == "404", $"the denied write must leave no object, got {S3Fidelity.Repr(absent)}");

        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "conditional-write", IfNoneMatch = "*" });
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            head.ETag == put.ETag,
            "the conditional write must succeed and be readable back; " +
            $"head ETag {Py.Repr(head.ETag)} != put ETag {Py.Repr(put.ETag)}");
    }
}
