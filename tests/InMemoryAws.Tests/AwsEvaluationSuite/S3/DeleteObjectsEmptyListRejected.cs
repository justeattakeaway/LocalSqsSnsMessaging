// Translated from behavioral/s3/check_delete_objects_empty_list_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-objects-empty-list-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectsEmptyListRejected
{
    private const string MalformedXmlMessage = "The XML you provided was not well-formed or did not validate against our published schema";

    /// <summary>DeleteObjects with zero keys is a malformed request and must be rejected with MalformedXML.</summary>
    [Test]
    [DisplayName("s3.delete-objects-empty-list-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        // --- Positive control: a well-formed DeleteObjects on this same bucket really works, so a
        // target that rejects every DeleteObjects call cannot pass merely because everything is denied.
        const string controlKey = "control/a.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = controlKey, ContentBody = "control" });
        var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [new KeyVersion { Key = controlKey }], Quiet = false });
        var deleted = (resp.DeletedObjects ?? []).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        Ensure(deleted.SetEquals([controlKey]), $"expected {Py.Repr(controlKey)} in Deleted, got {Py.Repr(deleted.ToList())}");
        Ensure((resp.DeleteErrors ?? []).Count == 0, $"expected no per-key errors, got {Py.Repr((resp.DeleteErrors ?? []).Select(e => e.Code).ToList())}");
        var controlErr = await Err(() => s3.GetObjectAsync(bucket, controlKey), "GetObject of the deleted control key");
        Ensure(controlErr.Code == "NoSuchKey", $"expected NoSuchKey reading back the control key after delete, got {controlErr}");
        Ensure(controlErr.Message == "The specified key does not exist.", controlErr.Message ?? "None");

        // --- Break exactly one thing: an empty Objects list is malformed and can never become valid,
        // so this negative has no restore leg by construction.
        const string survivorKey = "survivor/b.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = survivorKey, ContentBody = "survivor" });

        var exc = await FidelityContext.CaptureError(() => s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [] }));
        Ensure(exc is not null, "DeleteObjects with zero keys was accepted; the empty-batch rejection is not enforced");
        Ensure(exc.Code == "MalformedXML", $"expected MalformedXML for an empty delete batch, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == MalformedXmlMessage, exc.ToString());
        Ensure(exc.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for an empty delete batch, got {(int)exc.StatusCode}");

        // --- Effect absent, via an independent read at the destination: the malformed request must
        // not have touched the bucket's real contents.
        var remaining = (await S3CoreFidelity.ListAllKeys(s3, bucket)).ToHashSet(StringComparer.Ordinal);
        Ensure(
            remaining.SetEquals([survivorKey]),
            $"an empty DeleteObjects must have no effect; expected only {Py.Repr(survivorKey)} to survive, "
            + $"bucket now holds {Py.Repr(remaining.Order(StringComparer.Ordinal).ToList())}");
    }

    /// <summary>Call fn(); return the botocore Error dict it raised. A success raises AssertionError.</summary>
    private static async Task<AwsError> Err(Func<Task> fn, string what) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail($"{what} succeeded; expected a refusal");
}
