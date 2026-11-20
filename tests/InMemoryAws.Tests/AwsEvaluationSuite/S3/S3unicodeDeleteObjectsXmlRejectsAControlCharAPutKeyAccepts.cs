// Translated from behavioral/s3/check_s3unicode_delete_objects_xml_rejects_a_control_char_a_put_key_accepts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryAws.Http;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-delete-objects-xml-rejects-a-control-char-a-put-key-accepts</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeDeleteObjectsXmlRejectsAControlCharAPutKeyAccepts
{
    /// <summary>
    /// A vertical-tab (U+000B) key works in PutObject/DeleteObject; DeleteObjects says MalformedXML.
    /// U+000B is not an XML 1.0 Char; tab, LF and emoji keys batch-delete fine; the VT object survives.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-delete-objects-xml-rejects-a-control-char-a-put-key-accepts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string emoji = "\U0001f600";
        const string prefix = "del/";
        const string keyVt = $"{prefix}a\u000bb.txt";       // VERTICAL TAB: illegal in XML 1.0
        const string keyTab = $"{prefix}a\tb.txt";          // TAB: legal in XML 1.0
        const string keyLf = $"{prefix}a\nb.txt";           // LINE FEED: legal in XML 1.0
        const string keyAstral = $"{prefix}a{emoji}b.txt";
        string[] legal = [keyTab, keyLf, keyAstral];
        var body = "batch-delete-payload"u8.ToArray();

        foreach (var key in new[] { keyVt }.Concat(legal))
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
        }

        // --- PutObject accepted all four, including the vertical tab: the key itself is legal.
        var stored = Sorted(await S3Fidelity.ListKeys(ctx, bucket, prefix));
        var all = Sorted([keyVt, .. legal]);
        Ensure(stored.SequenceEqual(all), $"PutObject did not store all four control-character keys byte-exactly: expected {Py.Repr(all)}, got {Py.Repr(stored)}");
        Ensure(
            (await s3.GetObjectMetadataAsync(bucket, keyVt)).ContentLength == body.Length,
            $"the vertical-tab key {Py.Repr(keyVt)} is not addressable via HeadObject, so the asymmetry this check measures cannot be established");

        // --- Positive control, in the batch API: the three XML-legal keys delete in one DeleteObjects
        //     call and are echoed back byte-exactly.
        var ok = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [.. legal.Select(k => new KeyVersion { Key = k })],
            Quiet = false,
        });
        Ensure((ok.DeleteErrors ?? []).Count == 0, $"DeleteObjects reported per-key errors for XML-legal keys: {Py.Repr((ok.DeleteErrors ?? []).Select(e => (e.Key, e.Code)).ToList())}");
        var echoed = Sorted((ok.DeletedObjects ?? []).Select(d => d.Key));
        Ensure(echoed.SequenceEqual(Sorted(legal)), $"DeleteObjects did not echo the XML-legal keys byte-exactly: expected {Py.Repr(Sorted(legal))}, got {Py.Repr(echoed)}");
        foreach (var key in legal)
        {
            var gone = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
            Ensure(gone is not null, $"the batch delete reported {Py.Repr(key)} deleted but it still exists");
            Ensure((int)gone!.StatusCode == 404, $"expected HTTP 404 for the batch-deleted key {Py.Repr(key)}, got {(int)gone.StatusCode}");
        }

        // --- Break exactly one thing: the same batch API, one key holding a vertical tab.
        // (the .NET SDK's XML writer refuses U+000B before sending; botocore writes it into the body
        // as is, so the request goes raw)
        var (status, code, message) = await RawDeleteObjects(
            ctx,
            bucket,
            $"<Delete xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Object><Key>{keyVt}</Key></Object><Quiet>false</Quiet></Delete>");
        Ensure(
            status >= 300,
            $"DeleteObjects accepted the vertical-tab key {Py.Repr(keyVt)} in its XML body; real S3 refuses it, and any response echoing U+000B would not be well-formed XML");
        Ensure(code == "MalformedXML", $"expected MalformedXML for a DeleteObjects body containing U+000B, got {Py.Repr(code)}");
        Ensure(
            message == "The XML you provided was not well-formed or did not validate against our published schema",
            $"({Py.Repr(code)}, {Py.Repr(message)})");
        Ensure(status == 400, $"expected HTTP 400 for MalformedXML, got {status}");

        // --- Effect absent: the rejected batch deleted nothing.
        var left = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(left.SequenceEqual([keyVt]), $"after the rejected batch the prefix should still hold exactly {Py.Repr(keyVt)}, got {Py.Repr(left)}");
        var survivor = await S3Fidelity.GetBytes(s3, bucket, keyVt);
        Ensure(
            survivor.AsSpan().SequenceEqual(body),
            $"the vertical-tab object was damaged by the rejected batch delete: expected {Py.Repr(body)}, got {Py.Repr(survivor)}");

        // --- The asymmetry, stated: the SAME key deletes cleanly through the URL-path transport.
        await s3.DeleteObjectAsync(bucket, keyVt);
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, keyVt));
        Ensure(
            exc is not null,
            $"DeleteObject did not remove {Py.Repr(keyVt)}; the vertical tab is unrepresentable in the batch API but must be deletable through the URL path");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 after DeleteObject on the vertical-tab key, got {(int)exc.StatusCode}");
        var empty = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(empty.Count == 0, $"the prefix is not empty after every key was deleted: {Py.Repr(empty)}");
    }

    private static List<string> Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// <c>s3.delete_objects(...)</c> with the XML body botocore writes, sent raw (path style, with the
    /// <c>Content-MD5</c> DeleteObjects requires): the status and S3's error code and message.
    /// </summary>
    private static async Task<(int Status, string? Code, string? Message)> RawDeleteObjects(FidelityContext ctx, string bucket, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{bucket}?delete", UriKind.Relative))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/xml");
        request.Content.Headers.TryAddWithoutValidation("Content-MD5", Convert.ToBase64String(S3Fidelity.Md5(bytes)));
        using var response = await ctx.RawHttpClient(AwsServiceType.S3).SendAsync(request, ctx.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return ((int)response.StatusCode, null, null);
        }

        var error = System.Xml.Linq.XElement.Parse(text);
        return ((int)response.StatusCode, error.Element("Code")?.Value, error.Element("Message")?.Value);
    }
}
