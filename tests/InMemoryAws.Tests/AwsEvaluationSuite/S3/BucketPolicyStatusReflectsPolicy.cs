// Translated from behavioral/s3/check_bucket_policy_status_reflects_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-policy-status-reflects-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketPolicyStatusReflectsPolicy
{
    /// <summary>
    /// GetBucketPolicyStatus fails with NoSuchBucketPolicy before any policy, reports IsPublic=true for a
    /// Principal "*" grant conditioned only on a secret Referer, and keeps IsPublic=true after Block Public
    /// Access is re-enabled: the status describes the policy document.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-policy-status-reflects-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-polstat-");

        // With no bucket policy attached yet, GetBucketPolicyStatus itself errors out
        // on real S3 (there is no policy to compute a status for).
        var initial = await S3Fidelity.Error(() => s3.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest { BucketName = name }));
        Ensure(initial.Code == "NoSuchBucketPolicy", $"expected NoSuchBucketPolicy for a bucket with no policy attached, got {Py.Repr(initial.Code)}");
        Ensure(initial.Message == "The bucket policy does not exist", initial.Message ?? "None");

        // Lift only BlockPublicPolicy and RestrictPublicBuckets so the policy below can be attached.
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = name,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = false,
                RestrictPublicBuckets = false,
            },
        });

        // S3 counts a grant conditioned on aws:Referer as public, and the per-run secret keeps the
        // grant usable by nobody else.
        var referer = S3Fidelity.TokenUrlSafe();
        var publicPolicy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "PublicRead",
                ["Effect"] = "Allow",
                ["Principal"] = "*",
                ["Action"] = "s3:GetObject",
                ["Resource"] = $"arn:aws:s3:::{name}/*",
                ["Condition"] = new JsonObject { ["StringEquals"] = new JsonObject { ["aws:Referer"] = referer } },
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = name, Policy = publicPolicy.ToJsonString() });

        var status = await s3.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest { BucketName = name });
        Ensure(status.PolicyStatus.IsPublic == true, $"expected IsPublic=True after attaching a Principal:* GetObject policy, got {status.PolicyStatus.IsPublic}");

        // Restrict public access again -- this changes whether the public grant
        // in the policy can actually be exercised, but it does not rewrite the
        // policy document, so the policy-status computation is unchanged: it must
        // keep reporting IsPublic=true.
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = name,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = true,
                RestrictPublicBuckets = true,
            },
        });

        // Give any (unexpected) asynchronous re-evaluation a moment, but the
        // expected outcome is that the status is stable and remains public.
        GetBucketPolicyStatusResponse? status2 = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            status2 = await s3.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest { BucketName = name });
            if (status2.PolicyStatus.IsPublic == true)
            {
                break;
            }

            await ctx.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            status2!.PolicyStatus.IsPublic == true,
            "expected IsPublic to remain True after re-enabling the public access block " +
            $"(status reflects the policy document, not current enforcement), got {status2.PolicyStatus.IsPublic}");
    }
}
