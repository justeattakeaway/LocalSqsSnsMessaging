// Translated from behavioral/s3/check_error_no_such_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.error-no-such-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorNoSuchKey
{
    private const string ErrNoSuchKey = "NoSuchKey";

    /// <summary>
    /// GetObject for a key that was never written returns NoSuchKey, on a bucket where GetObject
    /// for a real key demonstrably works — otherwise "rejects a missing key" is indistinguishable
    /// from "rejects every GetObject".
    /// </summary>
    [Test]
    [DisplayName("s3.error-no-such-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string controlKey = "control/exists.txt";
        const string controlBody = "sim2real-positive-control";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = controlKey, InputStream = new MemoryStream(Encoding.UTF8.GetBytes(controlBody)) });
        var got = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, controlKey));
        Ensure(got == controlBody, $"positive control: expected {Py.Repr(controlBody)}, got {Py.Repr(got)}");

        const string missingKey = "control/never-written.txt";
        var err = await ErrorCode(() => S3Fidelity.GetBytes(s3, bucket, missingKey));
        Ensure(err.Code == ErrNoSuchKey, $"expected {ErrNoSuchKey}, got {Py.Repr(err.Code)}");
        Ensure(err.Message == "The specified key does not exist.", $"expected the exact {ErrNoSuchKey} message, got {Py.Repr(err.Message)}");

        // HeadObject is a distinct operation from GetObject; the miss must be visible through it too.
        var head = await ErrorCode(() => s3.GetObjectMetadataAsync(bucket, missingKey));
        Ensure(S3CoreFidelity.IsBareHead404(head), $"expected 404 from HeadObject on a missing key, got {Py.Repr(head.Code)}");
        Ensure(S3CoreFidelity.BotocoreMessage(head) == "Not Found", $"expected 'Not Found' from HeadObject on a missing key, got {Py.Repr(S3CoreFidelity.BotocoreMessage(head))}");
    }

    /// <summary>
    /// Call fn(); return its botocore Error (code, message). An unexpected SUCCESS raises, because
    /// an emulator accepting a request real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> ErrorCode(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
}
