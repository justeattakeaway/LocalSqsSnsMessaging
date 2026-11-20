// Translated from behavioral/s3/check_op_contract_get_bucket_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-get-bucket-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetBucketPolicy
{
    /// <summary>
    /// GetBucketPolicy returns the attached policy verbatim; an unpolicied bucket raises NoSuchBucketPolicy.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-get-bucket-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var accountId = ctx.Account;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // --- error boundary first: fresh bucket has no policy ---
        // The .NET SDK answers GetBucketPolicy's NoSuchBucketPolicy as an empty success; read the
        // refusal botocore surfaces off the wire.
        var raw = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?policy", "expected a ClientError, but the call SUCCEEDED");
        var code = (raw.Code, raw.Message);
        Ensure(code == ("NoSuchBucketPolicy", "The bucket policy does not exist"), $"expected NoSuchBucketPolicy, got {code}");

        // --- success path: attach a policy, read it back verbatim ---
        var policy = $$"""
            {"Version": "2012-10-17", "Statement": [{"Sid": "AllowOwnAccountGet", "Effect": "Allow", "Principal": {"AWS": "arn:aws:iam::{{accountId}}:root"}, "Action": "s3:GetObject", "Resource": "arn:aws:s3:::{{bucket}}/*"}]}
            """;
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy });
        using var got = JsonDocument.Parse((await s3.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket })).Policy);
        var stmt = got.RootElement.GetProperty("Statement")[0];
        string? Member(string name) => stmt.TryGetProperty(name, out var value) ? value.ToString() : null;
        Ensure(Member("Sid") == "AllowOwnAccountGet", $"expected Sid 'AllowOwnAccountGet', got {Py.Repr(Member("Sid"))}");
        Ensure(Member("Action") == "s3:GetObject", $"expected Action 's3:GetObject', got {Py.Repr(Member("Action"))}");
        var expectedResource = $"arn:aws:s3:::{bucket}/*";
        Ensure(Member("Resource") == expectedResource, $"expected Resource {Py.Repr(expectedResource)}, got {Py.Repr(Member("Resource"))}");
    }

    /// <summary>
    /// The (Error.Code, Error.Message) of fn(); a code-less HEAD error yields the HTTP status. Raises
    /// AssertionError if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
