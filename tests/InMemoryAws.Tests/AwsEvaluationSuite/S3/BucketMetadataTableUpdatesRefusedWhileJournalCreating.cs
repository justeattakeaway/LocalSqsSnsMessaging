// Translated from behavioral/s3/check_bucket_metadata_table_updates_refused_while_journal_creating.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-table-updates-refused-while-journal-creating</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataTableUpdatesRefusedWhileJournalCreating
{
    /// <summary>Fresh buckets tried before the check gives up on observing the CREATING window.</summary>
    private const int Attempts = 3;

    /// <summary>Wall bound on all attempts; the runner's remaining time bounds it further.</summary>
    private const double DeadlineS = 120.0;

    /// <summary>The code Outcome reports for a call the target accepted.</summary>
    private const string Accepted = "accepted";

    private static readonly (string, string) BeingCreated = (
        "InvalidRequest",
        "You can't perform this operation while the journal table is being created. Wait for your " +
        "journal table status to transition to ACTIVE, and then try again.");

    private static readonly Dictionary<string, (string Code, string Message)> Refusals = new(StringComparer.Ordinal)
    {
        ["UpdateBucketMetadataJournalTableConfiguration"] = (
            "InvalidRequest",
            "You can't perform this operation on a journal table that doesn't have an ACTIVE status. " +
            "Wait for your journal table status to transition to ACTIVE, and then try again. If " +
            "creation of the journal table fails, you must re-create the metadata configuration for " +
            "your general purpose bucket."),
        ["UpdateBucketMetadataInventoryTableConfiguration"] = BeingCreated,
        ["UpdateBucketMetadataAnnotationTableConfiguration"] = BeingCreated,
    };

    /// <summary>
    /// While a bucket's metadata journal table is CREATING, updating the journal, inventory or
    /// annotation table configuration is refused with InvalidRequest and the configuration keeps its
    /// requested journal expiration.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-table-updates-refused-while-journal-creating")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var clock = FidelityTime.StartNew();
        var deadline = Bound(ctx, DeadlineS);
        var calls = new Dictionary<string, Func<string, Task>>(StringComparer.Ordinal)
        {
            ["UpdateBucketMetadataJournalTableConfiguration"] = b => S3Fidelity.UpdateJournal(s3, b, "ENABLED", 14),
            ["UpdateBucketMetadataInventoryTableConfiguration"] = b => S3Fidelity.UpdateInventory(s3, b, "ENABLED"),
            ["UpdateBucketMetadataAnnotationTableConfiguration"] = b => S3Fidelity.UpdateAnnotationTable(s3, b, "DISABLED"),
        };
        var pending = calls.Keys.ToList();
        var tried = new List<string>();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (pending.Count == 0 || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metacreating-");
            ctx.OnCleanup(() => SafeDeleteConfiguration(s3, bucket));
            await S3Fidelity.CreateMetadataConfiguration(s3, bucket, "ENABLED", 7);
            foreach (var op in pending.ToList())
            {
                var journal = await Journal(s3, bucket);
                if (journal.TableStatus != "CREATING")
                {
                    tried.Add($"{bucket}: journal {journal.TableStatus} before {op}");
                    break;
                }

                var (code, message) = await Outcome(() => calls[op](bucket));
                if ((code, message) == Refusals[op])
                {
                    var after = await Journal(s3, bucket);
                    Ensure(
                        after.RecordExpiration.Expiration?.Value == "ENABLED" && after.RecordExpiration.Days == 7,
                        $"the refused {op} changed the journal expiration: {S3Fidelity.Repr(after.RecordExpiration)}");
                    pending.Remove(op);
                    continue;
                }

                var still = (await Journal(s3, bucket)).TableStatus;
                Ensure(
                    still != "CREATING",
                    $"{op} while the journal table stayed CREATING answered {Py.Repr(code)} {Py.Repr(message)}, " +
                    $"expected {S3Fidelity.Repr(Refusals[op])}");
                Ensure(code == Accepted, $"{op} answered {Py.Repr(code)} {Py.Repr(message)} once the journal table was {Py.Repr(still)}");
                tried.Add($"{bucket}: {op} answered {Py.Repr(code)} after the journal left CREATING ({Py.Repr(still)})");
                break;
            }
        }

        Ensure(
            pending.Count == 0,
            $"never observed the refusal of {Py.Repr(pending)} while the journal table was CREATING in " +
            $"{Attempts} fresh buckets: {Py.Repr(tried)}");
    }

    private static async Task<JournalTableConfigurationResult> Journal(IAmazonS3 s3, string bucket) =>
        (await S3Fidelity.MetadataResult(s3, bucket)).JournalTableConfigurationResult;

    /// <summary>(code, message) of the refusal <paramref name="call"/> raises, or (Accepted, the response) when it is accepted.</summary>
    private static async Task<(string? Code, string? Message)> Outcome(Func<Task> call)
    {
        var error = await FidelityContext.CaptureError(call);
        return error is null ? (Accepted, "<the response>") : (error.Code, error.Message);
    }

    /// <summary>
    /// Delete the configuration once its journal table has settled, so the table it leaves is listed
    /// when the tables are deleted.
    /// </summary>
    private static async Task SafeDeleteConfiguration(IAmazonS3 s3, string bucket, double timeoutS = 300.0)
    {
        var clock = FidelityTime.StartNew();
        var error = await FidelityContext.CaptureError(async () =>
        {
            while ((await Journal(s3, bucket)).TableStatus == "CREATING")
            {
                if (clock.Elapsed.TotalSeconds >= timeoutS)
                {
                    throw Fail($"journal table of {bucket} stayed CREATING for {timeoutS}s");
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(5));
            }

            await S3Fidelity.DeleteMetadataConfiguration(s3, bucket);
        });
        if (error is not null && error.Code != "MetadataConfigurationNotFound")
        {
            throw error.Exception;
        }
    }

    private static double Bound(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 30.0));
    }
}
