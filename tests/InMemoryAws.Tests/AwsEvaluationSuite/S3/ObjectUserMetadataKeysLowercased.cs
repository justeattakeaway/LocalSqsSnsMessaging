// Translated from behavioral/s3/check_object_user_metadata_keys_lowercased.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-user-metadata-keys-lowercased</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectUserMetadataKeysLowercased
{
    /// <summary>
    /// User metadata keys are normalised to lower case (they travel as HTTP headers) while values are
    /// preserved verbatim.
    /// </summary>
    [Test]
    [DisplayName("s3.object-user-metadata-keys-lowercased")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "usermeta.txt";
        var put = new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Encoding.UTF8.GetBytes("x")) };
        put.Metadata.Add("MixedCase", "PreservedValue");
        put.Metadata.Add("another-KEY", "v2");
        await s3.PutObjectAsync(put);

        // The .NET SDK keeps metadata keys with their x-amz-meta- prefix; botocore strips it.
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var meta = head.Metadata.Keys.ToDictionary(
            k => k.StartsWith("x-amz-meta-", StringComparison.Ordinal) ? k["x-amz-meta-".Length..] : k,
            k => head.Metadata[k],
            StringComparer.Ordinal);
        var names = meta.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(names.SequenceEqual(["another-key", "mixedcase"]), $"expected lower-cased metadata keys, got {Py.Repr(names)}");
        Ensure(meta["mixedcase"] == "PreservedValue", "metadata values must NOT be case-folded");
        Ensure(meta["another-key"] == "v2", $"expected value 'v2' preserved verbatim, got {Py.Repr(meta["another-key"])}");
    }
}
