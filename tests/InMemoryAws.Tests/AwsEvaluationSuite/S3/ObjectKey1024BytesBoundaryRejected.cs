// Translated from behavioral/s3/check_object_key_1024_bytes_boundary_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-key-1024-bytes-boundary-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectKey1024BytesBoundaryRejected
{
    private const string KeyTooLongMessage = "Your key is too long";

    /// <summary>
    /// The S3 object-key limit is exactly 1024 UTF-8 BYTES: 1024 is accepted, 1025 is KeyTooLongError.
    /// The limit counts bytes, not characters, so a 1024-character multibyte key is refused; both sides
    /// of the boundary are asserted.
    /// </summary>
    [Test]
    [DisplayName("s3.object-key-1024-bytes-boundary-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        // --- Control: exactly 1024 single-byte characters is legal, stored, readable and listable.
        var atLimit = new string('a', 1024);
        Ensure(Encoding.UTF8.GetByteCount(atLimit) == 1024, $"test setup error: expected a 1024-byte key, got {Encoding.UTF8.GetByteCount(atLimit)}");
        await Put(s3, bucket, atLimit, "at-limit");
        var got = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, atLimit));
        Ensure(got == "at-limit", $"1024-byte key did not round-trip its body, got {Py.Repr(got)}");
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(listed.Contains(atLimit), "a 1024-byte key was stored but is missing from ListObjectsV2");

        // --- Control: the limit is counted in BYTES. 512 two-byte characters == 1024 bytes == legal.
        var multibyteAtLimit = new string('é', 512);
        Ensure(multibyteAtLimit.Length == 512, $"test setup error: expected a 512-character key, got {multibyteAtLimit.Length}");
        Ensure(
            Encoding.UTF8.GetByteCount(multibyteAtLimit) == 1024,
            $"test setup error: expected a 1024-byte key, got {Encoding.UTF8.GetByteCount(multibyteAtLimit)}");
        await Put(s3, bucket, multibyteAtLimit, "multibyte-at-limit");
        got = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, multibyteAtLimit));
        Ensure(got == "multibyte-at-limit", $"512-char/1024-byte key did not round-trip its body, got {Py.Repr(got)}");

        // --- Break exactly one thing: one byte over the limit.
        var overLimit = new string('a', 1025);
        var exc = await FidelityContext.CaptureError(() => Put(s3, bucket, overLimit, "over-limit"));
        Ensure(exc is not null, "PutObject with a 1025-byte key was accepted; the 1024-byte contract limit is not enforced");
        Ensure(exc.Code == "KeyTooLongError", $"expected KeyTooLongError for a 1025-byte key, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == KeyTooLongMessage, $"unexpected KeyTooLongError message: {Py.Repr(exc.Message)}");
        Ensure((int)exc.StatusCode == 400, $"expected HTTP 400 for KeyTooLongError, got {(int)exc.StatusCode}");

        // --- Effect absent: the rejected put created nothing.
        exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, overLimit));
        Ensure(exc is not null, "the rejected 1025-byte key is readable via HeadObject; the write was not rolled back");
        var listedAfter = (await S3CoreFidelity.ListAllKeys(s3, bucket)).ToHashSet(StringComparer.Ordinal);
        Ensure(!listedAfter.Contains(overLimit), "the rejected 1025-byte key appears in ListObjectsV2");
        Ensure(
            listedAfter.SetEquals([atLimit, multibyteAtLimit]),
            $"bucket contents changed unexpectedly: {Py.Repr(listedAfter.Select(k => k.Length).Order().ToList())}");

        // --- Byte-counted, not char-counted: 513 two-byte characters == 1026 bytes == too long,
        // even though it is only 513 characters.
        var multibyteOver = new string('é', 513);
        Ensure(
            multibyteOver.Length == 513 && Encoding.UTF8.GetByteCount(multibyteOver) == 1026,
            "test setup error: expected a 513-character/1026-byte key, got "
            + $"{multibyteOver.Length} chars / {Encoding.UTF8.GetByteCount(multibyteOver)} bytes");
        exc = await FidelityContext.CaptureError(() => Put(s3, bucket, multibyteOver, "x"));
        Ensure(
            exc is not null,
            "a 513-character / 1026-BYTE key was accepted; the emulator is counting characters, not UTF-8 bytes");
        Ensure(exc.Code == "KeyTooLongError", $"expected KeyTooLongError for a 1026-byte (513-char) key, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == KeyTooLongMessage, $"unexpected KeyTooLongError message: {Py.Repr(exc.Message)}");

        // --- Control still works: the at-limit object is untouched by the rejections.
        var final = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, atLimit));
        Ensure(final == "at-limit", $"the at-limit object was altered by the rejected puts, got {Py.Repr(final)}");
    }

    private static Task Put(IAmazonS3 s3, string bucket, string key, string body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Encoding.UTF8.GetBytes(body)) });
}
