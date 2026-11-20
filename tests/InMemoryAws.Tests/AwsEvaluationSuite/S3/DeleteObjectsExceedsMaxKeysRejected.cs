// Translated from behavioral/s3/check_delete_objects_exceeds_max_keys_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-objects-exceeds-max-keys-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectsExceedsMaxKeysRejected
{
    private const int MaxKeys = 1000;

    /// <summary>
    /// DeleteObjects caps a request at 1000 keys: 1000 is accepted, 1001 is MalformedXML and deletes nothing.
    /// The over-cap request is refused whole, so not one of its entries is applied: deleting entries before
    /// noticing the 1001st would be data loss no error code can undo.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-objects-exceeds-max-keys-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        // Real objects that must survive the rejected request. Non-existent filler keys make the
        // 1000/1001-entry payloads cheap: DeleteObjects reports absent keys as successfully deleted.
        List<string> realKeys = [.. Enumerable.Range(0, 3).Select(i => $"real/keep-{i}.txt")];
        foreach (var k in realKeys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, InputStream = new MemoryStream(Encoding.UTF8.GetBytes("keep-me")) });
        }

        // n Object entries: the 3 real keys first, then non-existent filler up to n.
        List<KeyVersion> Entries(int n)
        {
            List<string> keys = [.. realKeys, .. Enumerable.Range(0, n - realKeys.Count).Select(i => $"filler/absent-{i}.txt")];
            Ensure(keys.Count == n, "assert len(keys) == n");
            return [.. keys.Select(k => new KeyVersion { Key = k })];
        }

        // --- Break exactly one thing: one entry over the cap.
        var over = Entries(MaxKeys + 1);
        var exc = await FidelityContext.CaptureError(() => s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = over, Quiet = true }))
            ?? throw Fail($"DeleteObjects with {MaxKeys + 1} entries (the {MaxKeys}-key request cap) succeeded; expected a refusal");
        var status = (int)exc.StatusCode;
        Ensure(exc.Code == "MalformedXML", $"expected MalformedXML for an over-cap DeleteObjects request, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == "The XML you provided was not well-formed or did not validate against our published schema", exc.ToString());
        Ensure(status == 400, $"expected HTTP 400 for an over-cap DeleteObjects request, got {status}");

        // --- Effect absent, and this is the whole point: the request is refused as a unit, so the
        // 3 real keys it named are untouched. A per-entry emulator fails here, not above.
        var surviving = (await S3CoreFidelity.ListAllKeys(s3, bucket)).ToHashSet(StringComparer.Ordinal);
        Ensure(
            surviving.SetEquals(realKeys),
            $"an over-cap DeleteObjects must delete NOTHING; expected {Py.Repr(realKeys)} to survive, "
            + $"bucket now holds {Py.Repr(surviving.Order(StringComparer.Ordinal).ToList())} — the request was applied entry-by-entry before "
            + "failing, which is unrecoverable data loss");
        foreach (var k in realKeys)
        {
            Ensure(Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, k)) == "keep-me", "assert get_object(...).read() == b'keep-me'");
        }

        // --- Control: exactly at the cap is accepted, and it really deletes.
        var atCap = Entries(MaxKeys);
        var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = atCap, Quiet = false });
        var errors = resp.DeleteErrors ?? [];
        Ensure(errors.Count == 0, $"a {MaxKeys}-entry DeleteObjects reported per-key errors: {errors.Count}");
        var deleted = (resp.DeletedObjects ?? []).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        Ensure(
            deleted.IsSupersetOf(realKeys),
            $"expected the real keys {Py.Repr(realKeys)} in Deleted, got {deleted.Count} entries "
            + $"missing {Py.Repr(realKeys.Except(deleted).ToList())}");
        Ensure(
            deleted.Count == MaxKeys,
            $"a non-quiet {MaxKeys}-entry delete must report all {MaxKeys} keys as deleted "
            + $"(absent keys included), got {deleted.Count}");

        // --- Effect present for the accepted request: the bucket is now empty.
        var remaining = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(remaining.Count == 0, $"expected an empty bucket after the at-cap delete, got {Py.Repr(remaining)}");
    }
}
