// Translated from behavioral/s3/check_bucket_policy_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-policy-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketPolicyRoundtrip
{
    /// <summary>
    /// PutBucketPolicy then GetBucketPolicy returns the statement verbatim, and after
    /// DeleteBucketPolicy an independent read confirms the policy is actually gone.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-policy-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "sim2real-test",
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = "s3:GetBucketLogging",
                ["Resource"] = $"arn:aws:s3:::{bucket}",
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy.ToJsonString() });

        var resp = await s3.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket });
        var got = JsonNode.Parse(resp.Policy)!.AsObject();
        Ensure(got["Version"]?.GetValue<string>() == "2012-10-17", $"expected Version '2012-10-17', got {Py.Repr(got["Version"]?.ToString())}");
        var statements = got["Statement"]!.AsArray();
        Ensure(statements.Count == 1, $"expected exactly 1 statement, got {statements.Count}");
        var gotStatement = statements[0];
        var wantStatement = policy["Statement"]![0];
        Ensure(JsonNode.DeepEquals(gotStatement, wantStatement), $"expected statement to roundtrip verbatim as {wantStatement!.ToJsonString()}, got {gotStatement?.ToJsonString()}");

        // Remove the policy, then prove the delete took effect via an independent read.
        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        // The .NET SDK answers NoSuchBucketPolicy as an empty success; the refusal is read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?policy");
        Ensure(error.Code == "NoSuchBucketPolicy", $"expected NoSuchBucketPolicy after delete, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The bucket policy does not exist", error.Message ?? "None");
    }
}
