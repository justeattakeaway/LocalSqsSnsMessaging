// Translated from behavioral/s3/check_bucket_policy_deny_enforcement.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-policy-deny-enforcement</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketPolicyDenyEnforcement
{
    /// <summary>
    /// An explicit Deny bucket policy (Principal "*") makes GetObject and PutObject fail with
    /// AccessDenied and the exact explicit-deny messages, even for the owner; the denied PutObject leaves
    /// the body unchanged, and both calls work again once the Deny is removed.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-policy-deny-enforcement")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "deny-me.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "secret-data" });

        // positive control: the object is readable before any Deny exists
        var precontrol = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(Encoding.UTF8.GetString(precontrol) == "secret-data", $"expected object readable before any Deny, got {Encoding.UTF8.GetString(precontrol)}");

        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "DenyAllGetPut",
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = new JsonArray("s3:GetObject", "s3:PutObject"),
                ["Resource"] = $"arn:aws:s3:::{bucket}/*",
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy.ToJsonString() });
        var callerArn = ctx.CallerArn;
        string ExplicitDeny(string action) =>
            $"User: {callerArn} is not authorized to perform: {action} on resource: " +
            $"\"arn:aws:s3:::{bucket}/{key}\" with an explicit deny in a resource-based policy";

        try
        {
            // a fresh bucket policy reaches every S3 front end eventually, not at once; poll the
            // read (it has no side effect) until the Deny is in force, bounded, raising on timeout
            var refused = await S3Fidelity.PollUntil(
                () => FidelityContext.CaptureError(() => s3.GetObjectAsync(bucket, key)),
                error => error is not null,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(2),
                _ => "call kept succeeding for 60.0s");
            Ensure(refused!.Code == "AccessDenied", $"expected AccessDenied for GetObject under explicit Deny, got {refused.Code}");
            Ensure(refused.Message == ExplicitDeny("s3:GetObject"), refused.Message ?? "None");

            var denied = await S3Fidelity.Error(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "overwrite" }));
            Ensure(denied.Code == "AccessDenied", $"expected AccessDenied for PutObject under explicit Deny, got {denied.Code}");
            Ensure(denied.Message == ExplicitDeny("s3:PutObject"), denied.Message ?? "None");
        }
        finally
        {
            // remove the self-blocking policy so teardown's own delete path isn't put at risk
            await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        }

        // the denied PutObject must have had no side effect -- the deny blocked the mutation
        // itself, not just returned an error while still committing the write underneath it
        var body = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(body == "secret-data", $"expected object unchanged by denied PutObject, got {body}");

        // restore is real: with the Deny gone, the identical calls that were just rejected succeed
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "overwrite-after-restore" });
        var restored = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(restored == "overwrite-after-restore", $"expected write to succeed after Deny removed, got {restored}");
    }
}
