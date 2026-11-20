// Translated from behavioral/s3/check_public_access_block_rejects_public_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.public-access-block-rejects-public-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublicAccessBlockRejectsPublicPolicy
{
    /// <summary>
    /// With BlockPublicPolicy enabled, a PutBucketPolicy that grants public (Principal="*") access must be
    /// REJECTED, not silently accepted; a policy that stays non-public must still be accepted under the
    /// same block, so the rejection is proven to be about publicness and not merely PutBucketPolicy itself.
    /// </summary>
    [Test]
    [DisplayName("s3.public-access-block-rejects-public-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var accountId = ctx.Account;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = true,
                RestrictPublicBuckets = true,
            },
        });

        var publicPolicy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "PublicReadAttempt",
                ["Effect"] = "Allow",
                ["Principal"] = "*",
                ["Action"] = "s3:GetObject",
                ["Resource"] = $"arn:aws:s3:::{bucket}/*",
            }),
        };
        var callerArn = ctx.CallerArn;
        var code = await S3Fidelity.Error(
            () => s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = publicPolicy.ToJsonString() }),
            "expected the call to be rejected, but it SUCCEEDED");
        var want = (
            "AccessDenied",
            $"User: {callerArn} is not authorized to perform: s3:PutBucketPolicy on resource: " +
            $"\"arn:aws:s3:::{bucket}\" because public policies are prevented by the " +
            "BlockPublicPolicy setting in S3 Block Public Access.");
        Ensure((code.Code, code.Message) == want, $"expected AccessDenied for a public policy under BlockPublicPolicy, got {S3Fidelity.Repr((code.Code, code.Message))}");

        // The bucket must NOT have accidentally acquired the public policy.
        // The .NET SDK answers NoSuchBucketPolicy as an empty success; the refusal is read off the wire.
        var noPolicy = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?policy");
        Ensure(
            (noPolicy.Code, noPolicy.Message) == ("NoSuchBucketPolicy", "The bucket policy does not exist"),
            $"expected no policy to have been stored, got {S3Fidelity.Repr((noPolicy.Code, noPolicy.Message))}");

        // Positive control: a non-public policy (principal is this account, not "*") is still accepted
        // under the identical BlockPublicPolicy=True block — without this, a target that rejects every
        // PutBucketPolicy call regardless of content would pass the assertions above just as well.
        var privatePolicy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AccountReadAttempt",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{accountId}:root" },
                ["Action"] = "s3:GetObject",
                ["Resource"] = $"arn:aws:s3:::{bucket}/*",
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = privatePolicy.ToJsonString() });
        var stored = JsonNode.Parse((await s3.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket })).Policy);
        Ensure(JsonNode.DeepEquals(stored, privatePolicy), $"expected the non-public policy to be stored as-is, got {stored?.ToJsonString()}");
    }
}
