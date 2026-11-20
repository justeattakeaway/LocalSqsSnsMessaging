// Translated from behavioral/s3/check_range_read_unsatisfiable_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryAws.Http;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.range-read-unsatisfiable-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RangeReadUnsatisfiableRejected
{
    private const int Size = 100;

    /// <summary>
    /// An unsatisfiable Range is 416 InvalidRange; an over-long Range is clamped and an unparseable one is
    /// ignored (200, whole object). The 416 carries no Content-Range header, so the object size needs a
    /// separate HeadObject.
    /// </summary>
    [Test]
    [DisplayName("s3.range-read-unsatisfiable-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "range-target.bin";
        var body = Enumerable.Range(0, Size).Select(i => (byte)(i % 256)).ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });

        // --- Control: a fully satisfiable range is a 206 with exactly the requested window.
        using (var resp = await Get(s3, bucket, key, "bytes=10-19"))
        {
            Ensure(resp.HttpStatusCode == HttpStatusCode.PartialContent, $"a satisfiable range must be 206, got {(int)resp.HttpStatusCode}");
            Ensure((await S3Fidelity.ReadBody(resp)).AsSpan().SequenceEqual(body.AsSpan(10, 10)), "assert resp[\"Body\"].read() == body[10:20]");
            Ensure(resp.ContentRange == $"bytes 10-19/{Size}", $"unexpected ContentRange {Py.Repr(resp.ContentRange)}");
        }

        // --- CLAMPED, not rejected: last-byte-pos beyond EOF is legal and truncates to EOF.
        using (var resp = await Get(s3, bucket, key, $"bytes=90-{Size * 10}"))
        {
            Ensure(
                resp.HttpStatusCode == HttpStatusCode.PartialContent,
                $"a range whose END exceeds the object size must be CLAMPED to a 206, not rejected; got {(int)resp.HttpStatusCode}");
            Ensure((await S3Fidelity.ReadBody(resp)).AsSpan().SequenceEqual(body.AsSpan(90)), "an over-long range must return bytes 90..EOF");
            Ensure(resp.ContentRange == $"bytes 90-{Size - 1}/{Size}", $"an over-long range must report the clamped window, got {Py.Repr(resp.ContentRange)}");
            Ensure(resp.ContentLength == 10, $"expected 10 clamped bytes, got {resp.ContentLength}");
        }

        // --- Break exactly one thing: first-byte-pos AT the object size is unsatisfiable.
        var exc = await FidelityContext.CaptureError(() => Get(s3, bucket, key, $"bytes={Size}-"));
        Ensure(
            exc is not null,
            $"a range starting at byte {Size} of a {Size}-byte object was accepted; an unsatisfiable "
            + "range must be refused, not answered with an empty body");
        var code = exc!.Code;
        var status = (int)exc.StatusCode;
        Ensure(code == "InvalidRange", $"expected InvalidRange, got {Py.Repr(code)}");
        Ensure(status == 416, $"expected HTTP 416 for InvalidRange, got {status}");
        Ensure(
            exc.Message == "The requested range is not satisfiable",
            "the 416 body must attribute itself to the range; real AWS says exactly \"The requested "
            + $"range is not satisfiable\" and this said {Py.Repr(exc.Message)}");
        // (the .NET SDK keeps no headers of an error response; the same request again, raw)
        using (var raw = await RawRanged(ctx, HttpMethod.Get, bucket, key, $"bytes={Size}-"))
        {
            // On real AWS S3's 416 carries NO Content-Range header.
            // The full header set on the refusal is x-amz-request-id, x-amz-id-2, content-type,
            // transfer-encoding, date, server — nothing else. RFC 7233 §4.4 says a 416 SHOULD send
            // "Content-Range: bytes */<complete-length>", and S3 does not, so a client CANNOT learn the
            // object's real size from the refusal and must issue a separate HeadObject (asserted at the
            // end of this check). Pinned as an absence on purpose: an emulator built from the RFC rather
            // than from S3 will helpfully add the header, and code that reads `Content-Range` off a 416
            // to discover the size then works locally and breaks against AWS.
            Ensure(
                (int)raw.StatusCode == 416 && !S3CoreFidelity.HasHeader(raw, "content-range"),
                $"real S3 sends no Content-Range on a 416, got {Py.Repr(S3CoreFidelity.Header(raw, "content-range"))}; the size "
                + "is discoverable only via HeadObject");
            Ensure(
                S3CoreFidelity.Header(raw, "content-type") == "application/xml",
                $"the 416 is an XML error document, got {Py.Repr(S3CoreFidelity.Header(raw, "content-type"))}");
        }

        // --- The same unsatisfiable range on HEAD: also 416, but with no body to parse, so the code
        // is the bare status rather than InvalidRange.
        var excHead = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = key,
            Range = $"bytes={Size + 500}-{Size + 600}",
        }));
        Ensure(excHead is not null, "HeadObject with an unsatisfiable range succeeded");
        Ensure((int)excHead!.StatusCode == 416, $"HeadObject must refuse an unsatisfiable range with 416 too, got {(int)excHead.StatusCode}");
        using (var rawHead = await RawRanged(ctx, HttpMethod.Head, bucket, key, $"bytes={Size + 500}-{Size + 600}"))
        {
            Ensure(!S3CoreFidelity.HasHeader(rawHead, "content-range"), "the HEAD 416 sends no Content-Range either");
        }

        // --- Same class, past the end rather than at it: still 416, not a 200 or a 404.
        exc = await FidelityContext.CaptureError(() => Get(s3, bucket, key, $"bytes={Size + 500}-{Size + 600}"));
        Ensure(exc is not null, "a range entirely past EOF was accepted");
        Ensure(exc!.Code == "InvalidRange", $"expected InvalidRange for a range entirely past EOF, got {Py.Repr(exc.Code)}");

        // --- IGNORED, not rejected: an unparseable Range header yields the whole object.
        using (var resp = await Get(s3, bucket, key, "bytes=not-a-range"))
        {
            Ensure(
                resp.HttpStatusCode == HttpStatusCode.OK,
                $"an unparseable Range header must be ignored and answered with a full 200, got {(int)resp.HttpStatusCode}");
            Ensure((await S3Fidelity.ReadBody(resp)).AsSpan().SequenceEqual(body), "an ignored Range must return the complete object");
            Ensure(resp.ContentRange is null, $"a full 200 response must not carry ContentRange, got {Py.Repr(resp.ContentRange)}");
        }

        // --- Effect absent / control still works: the rejections did not disturb the object, and
        // HeadObject is the ONLY way to recover the size the 416 refused to disclose.
        Ensure((await S3Fidelity.GetBytes(s3, bucket, key)).AsSpan().SequenceEqual(body), "assert s3.get_object(...)[\"Body\"].read() == body");
        Ensure((await s3.GetObjectMetadataAsync(bucket, key)).ContentLength == Size, "assert s3.head_object(...)[\"ContentLength\"] == _SIZE");
    }

    private static Task<GetObjectResponse> Get(IAmazonS3 s3, string bucket, string key, string range) =>
        s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = new ByteRange(range) });

    private static async Task<HttpResponseMessage> RawRanged(FidelityContext ctx, HttpMethod method, string bucket, string key, string range)
    {
        using var request = new HttpRequestMessage(method, new Uri($"{bucket}/{Uri.EscapeDataString(key)}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Range", range);
        return await ctx.RawHttpClient(AwsServiceType.S3).SendAsync(request, ctx.CancellationToken);
    }
}
