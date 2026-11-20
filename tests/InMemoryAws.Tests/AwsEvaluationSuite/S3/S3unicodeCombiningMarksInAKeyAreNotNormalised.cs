// Translated from behavioral/s3/check_s3unicode_combining_marks_in_a_key_are_not_normalised.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-combining-marks-in-a-key-are-not-normalised</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeCombiningMarksInAKeyAreNotNormalised
{
    /// <summary>
    /// NFC and NFD spellings of the same key are two DIFFERENT objects: S3 does not normalise keys.
    /// Both bodies read back, the mark-stripped ASCII spelling is a 404, and deleting one form leaves
    /// the other intact, which a normalising store cannot do.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-combining-marks-in-a-key-are-not-normalised")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string prefix = "norm/";
        // Built from codepoints, not written as literal characters: a literal decomposed sequence
        // in a source file is exactly the thing an editor, a formatter or a copy-paste silently
        // normalises away, and a check whose NFD key has quietly become NFC asserts nothing about
        // normalisation. This spelling cannot be folded by anything that touches the file.
        var nfc = "caf" + (char)0x00E9;                  // LATIN SMALL LETTER E WITH ACUTE
        var nfd = "caf" + (char)0x0065 + (char)0x0301;   // LATIN SMALL LETTER E + COMBINING ACUTE ACCENT
        var keyNfc = $"{prefix}{nfc}.txt";
        var keyNfd = $"{prefix}{nfd}.txt";
        var bodyNfc = "body-stored-under-the-PRECOMPOSED-key"u8.ToArray();
        var bodyNfd = "body-stored-under-the-DECOMPOSED-key"u8.ToArray();

        // --- Setup assertions: the two keys are canonical equivalents that differ in bytes.
        Ensure(nfc != nfd, "test setup error: the two spellings are identical strings");
        Ensure(nfd.Normalize(NormalizationForm.FormC) == nfc, $"test setup error: NFC({Py.Repr(nfd)}) is {Py.Repr(nfd.Normalize(NormalizationForm.FormC))}, not {Py.Repr(nfc)}");
        Ensure(nfc.Normalize(NormalizationForm.FormD) == nfd, $"test setup error: NFD({Py.Repr(nfc)}) is {Py.Repr(nfc.Normalize(NormalizationForm.FormD))}, not {Py.Repr(nfd)}");
        Ensure(
            Encoding.UTF8.GetByteCount(nfc) == 5 && Encoding.UTF8.GetByteCount(nfd) == 6,
            $"test setup error: expected 5 and 6 UTF-8 bytes, got {Encoding.UTF8.GetByteCount(nfc)} and {Encoding.UTF8.GetByteCount(nfd)}");

        // --- Write both. Distinct bodies, so a collapse is visible as a lost body and not just a
        //     lost key.
        await Put(s3, bucket, keyNfc, bodyNfc);
        await Put(s3, bucket, keyNfd, bodyNfd);

        // --- Both identities exist, and the listing reports each spelling byte-exactly.
        var listed = (await S3Fidelity.ListKeys(ctx, bucket, prefix)).Order(StringComparer.Ordinal).ToList();
        var expected = new[] { keyNfc, keyNfd }.Order(StringComparer.Ordinal).ToList();
        Ensure(
            listed.SequenceEqual(expected),
            $"the NFC and NFD keys are not two distinct objects in ListObjectsV2: expected {Py.Repr(expected)}, got {Py.Repr(listed)} -- a listing of length 1 means the keys were normalised and one object was overwritten");
        var listedBytes = listed.Select(k => Convert.ToHexString(Encoding.UTF8.GetBytes(k))).Order(StringComparer.Ordinal).ToList();
        var expectedBytes = expected.Select(k => Convert.ToHexString(Encoding.UTF8.GetBytes(k))).Order(StringComparer.Ordinal).ToList();
        Ensure(listedBytes.SequenceEqual(expectedBytes), $"the listed keys' UTF-8 bytes differ from the written keys': listed {Py.Repr(listedBytes)}");

        // --- Each key returns ITS OWN body. Aliasing would cross the two.
        var gotNfc = await S3Fidelity.GetBytes(s3, bucket, keyNfc);
        Ensure(gotNfc.AsSpan().SequenceEqual(bodyNfc), $"the precomposed key returned the wrong body: expected {Py.Repr(bodyNfc)}, got {Py.Repr(gotNfc)}");
        var gotNfd = await S3Fidelity.GetBytes(s3, bucket, keyNfd);
        Ensure(gotNfd.AsSpan().SequenceEqual(bodyNfd), $"the decomposed key returned the wrong body: expected {Py.Repr(bodyNfd)}, got {Py.Repr(gotNfd)}");

        // --- Effect absent: the mark-stripped ASCII spelling is not an alias for either object.
        var keyStripped = $"{prefix}cafe.txt";
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, keyStripped));
        Ensure(exc is not null, $"the accent-stripped key {Py.Repr(keyStripped)} resolves to an object; combining marks are being discarded from keys");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 for the accent-stripped key, got {(int)exc.StatusCode}");

        // --- Transition: deleting one normalisation form leaves the other whole.
        await s3.DeleteObjectAsync(bucket, keyNfd);
        exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, keyNfd));
        Ensure(exc is not null, $"the decomposed key {Py.Repr(keyNfd)} survived its own delete");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 for the deleted decomposed key, got {(int)exc.StatusCode}");
        var survivor = await S3Fidelity.GetBytes(s3, bucket, keyNfc);
        Ensure(survivor.AsSpan().SequenceEqual(bodyNfc), $"deleting the decomposed key damaged the precomposed one: expected {Py.Repr(bodyNfc)}, got {Py.Repr(survivor)}");
        var after = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(after.SequenceEqual([keyNfc]), $"after deleting only the decomposed key the listing should hold exactly {Py.Repr(keyNfc)}, got {Py.Repr(after)}");
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string key, byte[] body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
}
