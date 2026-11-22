// Translated from behavioral/s3/check_last_modified_whole_second_precision_agrees_and_advances.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.last-modified-whole-second-precision-agrees-and-advances</summary>
[Category("AwsEvaluationSuite")]
public sealed class LastModifiedWholeSecondPrecisionAgreesAndAdvances
{
    private const string Key = "ts.txt";

    /// <summary>
    /// Bound on the server-clock barrier. LastModified has one-second resolution, so two writes inside
    /// one server-second tie; the barrier waits for the S3 server's own clock to pass a given second.
    /// </summary>
    private const double ClockDeadlineS = 30.0;

    /// <summary>
    /// ListObjectsV2's XML, GetObjectAttributes and HeadObject report an object's LastModified at
    /// whole-second precision and agree to the second; a later PutObject strictly advances all three.
    /// </summary>
    [Test]
    [DisplayName("s3.last-modified-whole-second-precision-agrees-and-advances")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, ContentBody = "first" });
        var (xmlText1, xmlLm1, attrsLm1, headLm1) = await ReadLastModified(ctx, s3, bucket, Key);

        Ensure(xmlText1.EndsWith(".000Z", StringComparison.Ordinal), $"ListObjectsV2 XML LastModified {Py.Repr(xmlText1)} does not end in '.000Z'");
        Ensure(SubSecond(attrsLm1) == 0, $"GetObjectAttributes LastModified {Py.Repr(Iso(attrsLm1))} carries a sub-second component");
        Ensure(SubSecond(headLm1) == 0, $"HeadObject Last-Modified {Py.Repr(Iso(headLm1))} carries a sub-second component");
        Ensure(
            xmlLm1 == attrsLm1 && attrsLm1 == headLm1,
            $"the three reads disagree to the second: list={Py.Repr(Iso(xmlLm1))} attrs={Py.Repr(Iso(attrsLm1))} head={Py.Repr(Iso(headLm1))}");

        await AfterSecond(ctx, bucket, headLm1);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, ContentBody = "second" });
        var (xmlText2, xmlLm2, attrsLm2, headLm2) = await ReadLastModified(ctx, s3, bucket, Key);

        Ensure(xmlText2.EndsWith(".000Z", StringComparison.Ordinal), $"ListObjectsV2 XML LastModified {Py.Repr(xmlText2)} does not end in '.000Z'");
        Ensure(SubSecond(attrsLm2) == 0, $"GetObjectAttributes LastModified {Py.Repr(Iso(attrsLm2))} carries a sub-second component");
        Ensure(SubSecond(headLm2) == 0, $"HeadObject Last-Modified {Py.Repr(Iso(headLm2))} carries a sub-second component");
        Ensure(
            xmlLm2 == attrsLm2 && attrsLm2 == headLm2,
            $"the three reads disagree to the second after the second write: list={Py.Repr(Iso(xmlLm2))} attrs={Py.Repr(Iso(attrsLm2))} head={Py.Repr(Iso(headLm2))}");

        Ensure(xmlLm2 > xmlLm1, $"ListObjectsV2 LastModified did not advance: {Py.Repr(Iso(xmlLm1))} -> {Py.Repr(Iso(xmlLm2))}");
        Ensure(attrsLm2 > attrsLm1, $"GetObjectAttributes LastModified did not advance: {Py.Repr(Iso(attrsLm1))} -> {Py.Repr(Iso(attrsLm2))}");
        Ensure(headLm2 > headLm1, $"HeadObject Last-Modified did not advance: {Py.Repr(Iso(headLm1))} -> {Py.Repr(Iso(headLm2))}");
    }

    /// <summary>
    /// <c>_after_second(s3, bucket, stamp)</c>: waits until the server clock is strictly past
    /// <paramref name="stamp"/>'s second — a bounded poll, not a sleep.
    /// </summary>
    private static async Task AfterSecond(FidelityContext ctx, string bucket, DateTime stamp)
    {
        var deadline = FidelityTime.StartNew();
        DateTime? last = null;
        while (deadline.Elapsed.TotalSeconds < ClockDeadlineS)
        {
            last = await S3Fidelity.ServerNow(ctx, ctx.Region, bucket);
            if (WholeSecond(last.Value) > WholeSecond(stamp))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.25), ctx.CancellationToken);
        }

        throw Fail($"the server clock never passed {Iso(stamp)} within {ClockDeadlineS}s; last Date header read {Py.Repr(last is null ? null : Iso(last.Value))}");
    }

    /// <summary>
    /// <c>_read_last_modified(s3, bucket, key)</c>: the raw XML text, the XML LastModified, and
    /// GetObjectAttributes' and HeadObject's LastModified.
    /// </summary>
    private static async Task<(string XmlText, DateTime XmlLm, DateTime AttrsLm, DateTime HeadLm)> ReadLastModified(
        FidelityContext ctx, IAmazonS3 s3, string bucket, string key)
    {
        var xmlText = await ListObjectsV2XmlLastModified(ctx, bucket, key);
        var xmlLm = ParseXmlTimestamp(xmlText);
        var attrsLm = Utc((await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [ObjectAttributes.ETag],
        })).LastModified);
        var headLm = Utc((await s3.GetObjectMetadataAsync(bucket, key)).LastModified);
        return (xmlText, xmlLm, attrsLm, headLm);
    }

    /// <summary><c>_list_objects_v2_xml_last_modified(...)</c>: the raw XML <c>LastModified</c> text for <paramref name="key"/>.</summary>
    private static async Task<string> ListObjectsV2XmlLastModified(FidelityContext ctx, string bucket, string key)
    {
        var root = await S3Fidelity.RawListObjectsV2(ctx, bucket, prefix: string.Empty);
        foreach (var contents in root.Elements(S3Fidelity.S3Xml + "Contents"))
        {
            if ((string?)contents.Element(S3Fidelity.S3Xml + "Key") == key)
            {
                var text = (string?)contents.Element(S3Fidelity.S3Xml + "LastModified");
                Ensure(!string.IsNullOrEmpty(text), $"ListObjectsV2 XML has no LastModified for key {Py.Repr(key)}");
                return text;
            }
        }

        throw Fail($"key {Py.Repr(key)} is not present in the ListObjectsV2 XML");
    }

    /// <summary><c>_parse_xml_timestamp(text)</c>: an S3 XML LastModified (ISO-8601, 'Z'-suffixed) as a UTC instant.</summary>
    private static DateTime ParseXmlTimestamp(string text)
    {
        Ensure(text.EndsWith('Z'), $"XML LastModified {Py.Repr(text)} is not UTC ('Z')-suffixed");
        return DateTime.SpecifyKind(DateTime.Parse(text[..^1], CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc);
    }

    private static DateTime Utc(DateTime? value) => S3Fidelity.Utc(value);

    private static DateTime WholeSecond(DateTime value) => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>Python's <c>dt.microsecond</c> (here, any sub-second ticks).</summary>
    private static long SubSecond(DateTime value) => value.Ticks % TimeSpan.TicksPerSecond;

    /// <summary>Python's <c>dt.isoformat()</c> for a UTC instant.</summary>
    private static string Iso(DateTime value) =>
        value.ToString(SubSecond(value) == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "+00:00";
}
