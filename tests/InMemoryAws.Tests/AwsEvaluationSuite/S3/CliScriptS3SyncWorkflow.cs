// Translated from behavioral/s3/check_cli_script_s3_sync_workflow.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cli-script-s3-sync-workflow</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliScriptS3SyncWorkflow
{
    /// <summary>
    /// Bash script uploads multiple objects under a prefix plus a decoy outside it, lists the
    /// prefix, and verifies the exact key set — not just a count, so a target that ignores
    /// --prefix and returns everything in the bucket cannot pass by accident.
    /// </summary>
    [Test]
    [DisplayName("s3.cli-script-s3-sync-workflow")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3CoreFidelity.CreateXsvcBucket(ctx);
        var s3 = ctx.S3;
        // for i in $(seq 1 3); do
        //     echo "content-$i" > /tmp/sim2real-s3-upload-$i.txt
        //     aws s3api put-object --bucket <bucket> --key "batch/$i.txt" --body /tmp/sim2real-s3-upload-$i.txt
        // done
        for (var i = 1; i <= 3; i++)
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = $"batch/{i}.txt",
                InputStream = new MemoryStream(Encoding.UTF8.GetBytes($"content-{i}\n")),
            });
        }

        // echo "outside-prefix" > /tmp/sim2real-s3-decoy.txt
        // aws s3api put-object --bucket <bucket> --key "other/decoy.txt" --body /tmp/sim2real-s3-decoy.txt
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "other/decoy.txt",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("outside-prefix\n")),
        });

        // aws s3api list-objects-v2 --bucket <bucket> --prefix "batch/" --query "sort(Contents[].Key)" --output json
        // (the CLI follows continuation tokens itself, then applies the query)
        var keys = await S3CoreFidelity.ListAllKeys(s3, bucket, "batch/");
        keys.Sort(StringComparer.Ordinal);
        Ensure(
            keys.SequenceEqual(["batch/1.txt", "batch/2.txt", "batch/3.txt"]),
            $"Expected exactly the batch/ keys (decoy outside the prefix excluded), got {Py.Repr(keys)}");
    }
}
