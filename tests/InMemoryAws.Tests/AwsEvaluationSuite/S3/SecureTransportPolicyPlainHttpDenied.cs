// Translated from behavioral/s3/check_secure_transport_policy_plain_http_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.secure-transport-policy-plain-http-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class SecureTransportPolicyPlainHttpDenied
{
    private const string UnexpectedSuccess = "UNEXPECTED_SUCCESS";

    /// <summary>
    /// A Deny-unless-aws:SecureTransport bucket policy makes signed plain-HTTP Get, Put and List
    /// AccessDenied while HTTPS keeps working; plain HTTP works before the policy and after deletion.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/example-bucket-policies.html
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.SecureTransport, "aws:SecureTransport is always true, so the Deny-unless-TLS bucket policy never refuses the plain-HTTP requests")]
    [DisplayName("s3.secure-transport-policy-plain-http-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // ctx.client("s3", endpoint_url=<the same endpoint with the http scheme>)
        var httpS3 = ctx.S3Client(plainHttp: true);
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-tls-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "a.txt", ContentBody = "alpha" });

        // Baseline: plain HTTP is served when no policy forbids it.
        var baseline = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(httpS3, bucket, "a.txt"));
        Ensure(baseline == "alpha", $"plain-HTTP GetObject with no policy returned {Py.Repr(baseline)}");

        var arn = $"arn:aws:s3:::{bucket}";
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = bucket,
            Policy = new JsonObject
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new JsonArray(new JsonObject
                {
                    ["Sid"] = "DenyInsecureTransport",
                    ["Effect"] = "Deny",
                    ["Principal"] = "*",
                    ["Action"] = "s3:*",
                    ["Resource"] = new JsonArray(arn, $"{arn}/*"),
                    ["Condition"] = new JsonObject { ["Bool"] = new JsonObject { ["aws:SecureTransport"] = "false" } },
                }),
            }.ToJsonString(),
        });
        ctx.OnCleanup(() => s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket }));

        await Settle(
            () => ErrorCode(() => httpS3.GetObjectAsync(bucket, "a.txt")),
            "AccessDenied",
            "plain-HTTP GetObject under the policy");

        // The AccessDenied message names the caller's own ARN, so it is read from STS, not pinned.
        var put = await S3Fidelity.Error(
            () => httpS3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "b.txt", ContentBody = "beta" }),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(put.Code == "AccessDenied", $"plain-HTTP PutObject should be exactly AccessDenied, got {Py.Repr(put.Code)}");
        var callerArn = ctx.CallerArn;
        var expected =
            $"User: {callerArn} is not authorized to perform: s3:PutObject on resource: " +
            $"\"{arn}/b.txt\" with an explicit deny in a resource-based policy";
        Ensure(put.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(put.Message)}");

        var listCode = await ErrorCode(() => httpS3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }));
        Ensure(listCode == "AccessDenied", $"plain-HTTP ListObjectsV2 (bucket-ARN resource) should be exactly AccessDenied, got {Py.Repr(listCode)}");

        var absent = (await S3Fidelity.HeadError(s3, bucket, "b.txt")).Code;
        Ensure(absent == "404", $"the refused plain-HTTP PutObject created b.txt anyway: {Py.Repr(absent)}");

        // Positive control under the same policy: HTTPS is untouched.
        var tls = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, "a.txt"));
        Ensure(tls == "alpha", $"HTTPS GetObject under the policy returned {Py.Repr(tls)}");
        var keys = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).ToList();
        Ensure(keys.SequenceEqual(["a.txt"]), $"HTTPS listing under the policy returned [{string.Join(", ", keys.Select(Py.Repr))}]");

        // Repair: remove the policy and the very request that was refused now succeeds over HTTP.
        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });

        // A deleted Deny usually stops applying in ~4 s but can stay enforced past 90 s.
        await Settle(
            () => ErrorCode(() => httpS3.GetObjectAsync(bucket, "a.txt")),
            UnexpectedSuccess,
            "plain-HTTP GetObject after deleting the policy",
            timeoutSeconds: 420);
        await httpS3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "b.txt", ContentBody = "beta" });
        var landed = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, "b.txt"));
        Ensure(landed == "beta", $"plain-HTTP PutObject after repair stored {Py.Repr(landed)}");
    }

    /// <summary>The exact AWS error code, or a sentinel no code can equal when the call succeeds.</summary>
    private static async Task<string?> ErrorCode(Func<Task> call) =>
        await FidelityContext.CaptureError(call) is { } error ? error.Code : UnexpectedSuccess;

    /// <summary>Poll until probe() returns <paramref name="want"/> on <paramref name="streak"/> consecutive calls (policy propagation).</summary>
    private static async Task Settle(Func<Task<string?>> probe, string want, string description, int streak = 3, double timeoutSeconds = 300, double intervalSeconds = 2)
    {
        var clock = FidelityTime.StartNew();
        var seen = new List<string?>();
        var run = 0;
        while (true)
        {
            var got = await probe();
            seen.Add(got);
            run = got == want ? run + 1 : 0;
            if (run >= streak)
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw Fail(
                    $"{description}: wanted {Py.Repr(want)} {streak}x in a row within {timeoutSeconds}s; " +
                    $"last observations [{string.Join(", ", seen.TakeLast(10).Select(Py.Repr))}]");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }
}
