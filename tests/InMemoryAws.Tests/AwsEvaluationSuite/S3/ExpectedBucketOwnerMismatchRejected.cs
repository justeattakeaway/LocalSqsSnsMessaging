// Translated from behavioral/s3/check_expected_bucket_owner_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.expected-bucket-owner-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExpectedBucketOwnerMismatchRejected
{
    private const string MismatchMessage = "Access Denied";

    /// <summary>
    /// x-amz-expected-bucket-owner is enforced: a mismatched owner is AccessDenied even for the real owner.
    /// The header is a precondition independent of the IAM decision, so it is asserted on both a
    /// bucket-scoped and an object-scoped operation.
    /// </summary>
    [Test]
    [DisplayName("s3.expected-bucket-owner-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var account = ctx.Account;
        var wrong = OtherAccount(account);
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "owned.txt";
        const string body = "owner-guarded";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = body });

        // --- Control: the TRUE owner id is accepted on both a bucket op and an object op.
        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, ExpectedBucketOwner = account });
        Ensure(
            (resp.S3Objects ?? []).Select(o => o.Key).SequenceEqual([key]),
            $"ListObjectsV2 with the correct ExpectedBucketOwner must succeed, got {resp.S3Objects?.Count} object(s)");
        var got = Encoding.UTF8.GetString(await S3Fidelity.ReadBody(await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ExpectedBucketOwner = account })));
        Ensure(got == body, $"GetObject with the correct ExpectedBucketOwner must return {Py.Repr(body)}, got {Py.Repr(got)}");

        // --- Break exactly one thing: the same calls with a wrong (but well-formed) owner id.
        var exc = await FidelityContext.CaptureError(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, ExpectedBucketOwner = wrong }));
        Ensure(
            exc is not null,
            $"ListObjectsV2 with ExpectedBucketOwner={wrong} (the bucket is owned by {account}) " +
            "succeeded; the confused-deputy guard is not enforced on bucket ops");
        Ensure(exc.Code == "AccessDenied", $"expected AccessDenied for a mismatched ExpectedBucketOwner, got {Py.Repr(exc.Code)}");
        Ensure(exc.StatusCode == HttpStatusCode.Forbidden, $"expected HTTP 403 for a mismatched ExpectedBucketOwner, got {(int)exc.StatusCode}");
        Ensure(exc.Message == MismatchMessage, exc.ToString());

        exc = await FidelityContext.CaptureError(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ExpectedBucketOwner = wrong }));
        Ensure(exc is not null, "GetObject with a mismatched ExpectedBucketOwner succeeded; the guard is not enforced on object ops");
        Ensure(exc.Code == "AccessDenied", $"expected AccessDenied on the object path too, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == MismatchMessage, exc.ToString());

        // --- The guard also blocks WRITES, not just reads: the mismatch must not mutate anything.
        exc = await FidelityContext.CaptureError(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "written-by-wrong-owner.txt", ContentBody = "x", ExpectedBucketOwner = wrong,
        }));
        Ensure(exc is not null, "PutObject with a mismatched ExpectedBucketOwner succeeded");
        Ensure(exc.Code == "AccessDenied", $"expected AccessDenied for a guarded write, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == MismatchMessage, exc.ToString());

        exc = await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, ExpectedBucketOwner = wrong }));
        Ensure(
            exc is not null,
            "DeleteObject with a mismatched ExpectedBucketOwner succeeded; a guarded delete that runs " +
            "anyway is unrecoverable");
        Ensure(exc.Code == "AccessDenied", $"expected AccessDenied for a guarded delete, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == MismatchMessage, exc.ToString());

        // --- Effect absent: no write landed and no delete happened.
        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).ToHashSet();
        Ensure(
            listed.SetEquals([key]),
            $"the guarded write/delete changed the bucket; expected exactly {{{Py.Repr(key)}}}, got [{string.Join(", ", listed.Order(StringComparer.Ordinal).Select(Py.Repr))}]");
        Ensure(Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key)) == body, "the guarded object's body changed");

        // --- Control still works: omitting the header, and passing the true owner, both still succeed.
        got = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(got == body, $"GetObject with no ExpectedBucketOwner header must return {Py.Repr(body)}, got {Py.Repr(got)}");
        var length = (await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ExpectedBucketOwner = account })).ContentLength;
        Ensure(
            length == body.Length,
            $"HeadObject with the correct ExpectedBucketOwner must report ContentLength {body.Length}, got {length}");
    }

    /// <summary>The documentation account id 111122223333, a well-formed id that is not <paramref name="account"/>.</summary>
    private static string OtherAccount(string account)
    {
        Ensure(account.Length == 12 && account.All(char.IsAsciiDigit), $"unexpected account id shape: {Py.Repr(account)}");
        return "111122223333";
    }
}
