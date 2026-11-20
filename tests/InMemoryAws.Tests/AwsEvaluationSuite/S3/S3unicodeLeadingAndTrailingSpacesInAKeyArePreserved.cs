// Translated from behavioral/s3/check_s3unicode_leading_and_trailing_spaces_in_a_key_are_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-leading-and-trailing-spaces-in-a-key-are-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeLeadingAndTrailingSpacesInAKeyArePreserved
{
    /// <summary>
    /// Leading, trailing, both and no spaces make four distinct keys: S3 never trims whitespace.
    /// Each key has a distinct body, so a trimming store would collapse them and cross the bodies.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-leading-and-trailing-spaces-in-a-key-are-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string prefix = "sp/";
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{prefix} x.txt"] = "body-LEADING-space"u8.ToArray(),
            [$"{prefix}x .txt"] = "body-TRAILING-space"u8.ToArray(),
            [$"{prefix} x .txt"] = "body-BOTH-spaces"u8.ToArray(),
            [$"{prefix}x.txt"] = "body-NO-space"u8.ToArray(),
        };

        // --- Setup assertion: the four spellings really are four distinct strings that share one
        //     stripped form, so a trimming implementation must collapse them.
        Ensure(keys.Count == 4, $"test setup error: expected 4 distinct keys, got {keys.Count}");
        var stripped = keys.Keys.Select(k => k.Replace(" ", "", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Ensure(
            stripped.SetEquals([$"{prefix}x.txt"]),
            $"test setup error: the four keys do not share one space-free form, they reduce to {Py.Repr(stripped.ToList())}");

        foreach (var (key, body) in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
        }

        // --- All four identities exist, byte-exactly, and nothing else does.
        var listed = Sorted(await S3Fidelity.ListKeys(ctx, bucket, prefix));
        Ensure(
            listed.SequenceEqual(Sorted(keys.Keys)),
            $"the four space-variant keys are not four distinct objects: expected {Py.Repr(Sorted(keys.Keys))}, got {Py.Repr(listed)} -- a shorter listing means the keys were trimmed and objects were overwritten");
        var listedBytes = Sorted(listed.Select(k => Convert.ToHexString(Encoding.UTF8.GetBytes(k))));
        Ensure(
            listedBytes.SequenceEqual(Sorted(keys.Keys.Select(k => Convert.ToHexString(Encoding.UTF8.GetBytes(k))))),
            $"the listed keys' bytes differ from the written keys': listed {Py.Repr(listedBytes)}");

        // --- Each key returns ITS OWN body. A collapse crosses the pairing.
        foreach (var (key, body) in keys)
        {
            var got = await S3Fidelity.GetBytes(s3, bucket, key);
            Ensure(
                got.AsSpan().SequenceEqual(body),
                $"the key {Py.Repr(key)} returned the wrong body: expected {Py.Repr(body)}, got {Py.Repr(got)} -- the space-variant keys are aliases of one another");
        }

        // --- A server-side Prefix that INCLUDES the leading space selects only the two keys that
        //     start with one. This cannot be faked client-side.
        var leadPrefix = $"{prefix} ";
        var leadMatched = Sorted(await S3Fidelity.ListKeys(ctx, bucket, leadPrefix));
        var leadExpected = Sorted([$"{prefix} x.txt", $"{prefix} x .txt"]);
        Ensure(
            leadMatched.SequenceEqual(leadExpected),
            $"a server-side Prefix of {Py.Repr(leadPrefix)} did not select exactly the two leading-space keys: got {Py.Repr(leadMatched)}");

        // --- Transition: deleting the space-free key leaves the three space-bearing ones whole.
        await s3.DeleteObjectAsync(bucket, $"{prefix}x.txt");
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, $"{prefix}x.txt"));
        Ensure(exc is not null, $"the key {Py.Repr(prefix + "x.txt")} survived its own delete");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 for the deleted space-free key, got {(int)exc.StatusCode}");
        var survivors = Sorted(await S3Fidelity.ListKeys(ctx, bucket, prefix));
        var expected = Sorted(keys.Keys.Where(k => k != $"{prefix}x.txt"));
        Ensure(
            survivors.SequenceEqual(expected),
            $"deleting the space-free key disturbed the space-bearing ones: expected {Py.Repr(expected)}, got {Py.Repr(survivors)}");
        foreach (var key in expected)
        {
            var got = await S3Fidelity.GetBytes(s3, bucket, key);
            Ensure(got.AsSpan().SequenceEqual(keys[key]), $"the surviving key {Py.Repr(key)} lost its body: expected {Py.Repr(keys[key])}, got {Py.Repr(got)}");
        }
    }

    private static List<string> Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];
}
