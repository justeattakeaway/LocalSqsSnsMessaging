// Translated from behavioral/s3/check_op_contract_delete_bucket_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-bucket-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteBucketPolicy
{
    /// <summary>
    /// DeleteBucketPolicy removes the attached bucket policy; a missing bucket is rejected with NoSuchBucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-bucket-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var accountId = ctx.Account;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // --- success path: attach a policy, delete it, confirm it's gone ---
        var policy = $$"""
            {"Version": "2012-10-17", "Statement": [{"Sid": "AllowOwnAccountGet", "Effect": "Allow", "Principal": {"AWS": "arn:aws:iam::{{accountId}}:root"}, "Action": "s3:GetObject", "Resource": "arn:aws:s3:::{{bucket}}/*"}]}
            """;
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy });
        using (var got = JsonDocument.Parse((await s3.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket })).Policy))
        {
            var sid = got.RootElement.GetProperty("Statement")[0].GetProperty("Sid").GetString();
            Ensure(sid == "AllowOwnAccountGet", $"expected Sid 'AllowOwnAccountGet', got {Py.Repr(sid)}");
        }

        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        // The .NET SDK answers GetBucketPolicy's NoSuchBucketPolicy as an empty success; read the
        // refusal botocore surfaces off the wire.
        var raw = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?policy", "expected a ClientError, but the call SUCCEEDED");
        var (code, message) = (raw.Code, raw.Message);
        Ensure(code == "NoSuchBucketPolicy", $"expected NoSuchBucketPolicy, got {Py.Repr(code)}");
        Ensure(message == "The bucket policy does not exist", $"unexpected message {Py.Repr(message)}");

        // --- error boundary: operating against a bucket that does not exist ---
        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = ghostBucket }));
        Ensure(code == "NoSuchBucket", $"expected NoSuchBucket, got {Py.Repr(code)}");
        Ensure(message == "The specified bucket does not exist", $"unexpected message {Py.Repr(message)}");
    }

    /// <summary>
    /// The (Error.Code, Error.Message) of fn(); a code-less HEAD error yields the HTTP status. Raises
    /// AssertionError if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
