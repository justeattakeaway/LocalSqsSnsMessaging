// Translated from behavioral/s3/check_virtual_hosted_and_path_style_clients_refuse_a_nonexistent_bucket_identically.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.virtual-hosted-and-path-style-clients-refuse-a-nonexistent-bucket-identically</summary>
[Category("AwsEvaluationSuite")]
public sealed class VirtualHostedAndPathStyleClientsRefuseANonexistentBucketIdentically
{
    private const string Key = "cross-addr/missing.bin";

    /// <summary>
    /// GetObject and HeadBucket on a bucket that does not exist are refused NoSuchBucket with the
    /// same code and message whether addressed virtual-hosted or path-style, while a bucket the check
    /// created answers both addressing forms with 200.
    /// </summary>
    [Test]
    [DisplayName("s3.virtual-hosted-and-path-style-clients-refuse-a-nonexistent-bucket-identically")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var missing = FidelityContext.UniqueName("sim2real-behav-bkt-absent-");
        var vclient = ctx.S3Client(pathStyle: false, maxAttempts: 1);
        var pclient = ctx.S3Client(pathStyle: true, maxAttempts: 1);

        (string? Code, string? Message) noBucket = ("NoSuchBucket", "The specified bucket does not exist");
        (string? Code, string? Message) headNotFound = ("404", "Not Found");
        foreach (var (client, label) in new[] { (vclient, "virtual-hosted"), (pclient, "path-style") })
        {
            var got = Refusal(
                await Error(async () =>
                {
                    using var _ = await client.GetObjectAsync(missing, Key);
                }),
                $"{label} GetObject on an absent bucket");
            Ensure(got == noBucket, $"{label} GetObject on an absent bucket: expected {S3Fidelity.Repr(noBucket)}, got {S3Fidelity.Repr(got)}");
            var headError = await Error(() => client.HeadBucketAsync(new HeadBucketRequest { BucketName = missing }));
            Refusal(headError, $"{label} HeadBucket on an absent bucket");
            var headGot = S3Fidelity.HeadError(headError);
            Ensure(
                headGot == headNotFound,
                $"{label} HeadBucket on an absent bucket: expected {S3Fidelity.Repr(headNotFound)}, got {S3Fidelity.Repr(headGot)}");

            // positive control: the same calls against the bucket the check created succeed
            var head = await client.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket });
            Ensure(head.HttpStatusCode == HttpStatusCode.OK, $"{label} HeadBucket on the existing bucket answered {(int)head.HttpStatusCode}");
            var listed = (await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? [];
            Ensure(listed.Count == 0, $"{label} ListObjectsV2 on the fresh existing bucket listed {Py.Repr(listed.Select(o => o.Key).ToList())}");
        }
    }

    /// <summary><c>_error(fn)</c>: the error a call raised; a call that succeeds fails the check.</summary>
    private static async Task<AwsError> Error(Func<Task> call) =>
        await FidelityContext.CaptureError(call) ?? throw Fail("the call succeeded; expected a refusal");

    /// <summary><c>_refusal(got, what)</c>: (code, message) of a refusal, after grading its HTTP 4xx status.</summary>
    private static (string? Code, string? Message) Refusal(AwsError error, string what)
    {
        var status = (int)error.StatusCode;
        Ensure(status is >= 400 and < 500, $"{what}: answered HTTP {status}, expected 4xx");
        return (error.Code, error.Message ?? "");
    }
}
