// Translated from behavioral/s3/check_copy_object_under_if_none_match_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-under-if-none-match-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectUnderIfNoneMatchPolicy
{
    private const string Check = "copy-object-under-if-none-match-policy";
    private const string PreconditionMessage = "At least one of the pre-conditions you specified did not hold";
    private const string NotImplementedMessage = "A header you provided implies functionality that is not implemented";

    /// <summary>
    /// A bucket policy denying PutObject without if-none-match also governs CopyObject's
    /// destination write: no header is denied, If-None-Match: * succeeds onto a new key and
    /// fails the conditional check onto an existing one, and a specific ETag value is unimplemented.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-under-if-none-match-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var caller = ctx.CallerArn;
        var bucket = await S3Fidelity.MakeTaggedBucket(ctx, "sim2real-behav-cpcond-", Check);
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

        const string srcKey = "source.txt";
        var srcEtag = await PutConditionally(s3, bucket, srcKey, "source-bytes");
        const string existingKey = "existing.txt";
        var existingEtag = await PutConditionally(s3, bucket, existingKey, "pre-existing-bytes");
        const string newKey = "new.txt";

        await S3Fidelity.AwaitPolicy(
            ctx,
            n => s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = $"probe-{n}.txt" }),
            expectedCode: "AccessDenied");

        var denied = await S3Fidelity.Error(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = newKey,
        }));
        var wantDenied = (
            "AccessDenied",
            $"User: {caller} is not authorized to perform: s3:PutObject on resource: " +
            $"\"arn:aws:s3:::{bucket}/{newKey}\" with an explicit deny in a resource-based policy");
        Ensure((denied.Code, denied.Message) == wantDenied, $"expected {S3Fidelity.Repr(wantDenied)}, got {S3Fidelity.Repr((denied.Code, denied.Message))}");
        var absent = await S3Fidelity.HeadError(s3, bucket, newKey);
        Ensure(absent.Code == "404", $"the denied copy must create no destination, got {S3Fidelity.Repr(absent)}");

        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = newKey, IfNoneMatch = "*",
        });
        var newHead = await s3.GetObjectMetadataAsync(bucket, newKey);
        Ensure(
            newHead.ContentLength == "source-bytes".Length,
            "If-None-Match: * onto an absent key must create it from the source, " +
            $"got ContentLength {newHead.ContentLength}");

        var clash = await S3Fidelity.Error(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = existingKey, IfNoneMatch = "*",
        }));
        Ensure(
            (clash.Code, clash.Message) == ("PreconditionFailed", PreconditionMessage),
            $"If-None-Match: * onto an existing key must fail the conditional check, got {S3Fidelity.Repr((clash.Code, clash.Message))}");
        Ensure((await s3.GetObjectMetadataAsync(bucket, existingKey)).ETag == existingEtag, "the clashing copy must leave the existing destination untouched");

        var unimplemented = await S3Fidelity.Error(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = "unimplemented.txt", IfNoneMatch = srcEtag,
        }));
        Ensure(
            (unimplemented.Code, unimplemented.Message) == ("NotImplemented", NotImplementedMessage),
            "a specific ETag value in If-None-Match is a header CopyObject does not " +
            $"implement, got {S3Fidelity.Repr((unimplemented.Code, unimplemented.Message))}");
        var left = await S3Fidelity.HeadError(s3, bucket, "unimplemented.txt");
        Ensure(left.Code == "404", $"the unimplemented copy must create no destination, got {S3Fidelity.Repr(left)}");
    }

    /// <summary>PutObject under the if-none-match-enforcing policy, returning the new ETag.</summary>
    private static async Task<string> PutConditionally(Amazon.S3.IAmazonS3 s3, string bucket, string key, string body) =>
        (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = body, IfNoneMatch = "*" })).ETag;
}
