using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the S3 Metadata ports share. The checks' teardown also deletes the journal and inventory tables a
/// configuration leaves in the AWS managed table bucket (<c>_delete_metadata_tables</c>, through S3
/// Tables); the cloud models no S3 Tables, so there is nothing to delete and the ports leave it out.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>s3.create_bucket_metadata_configuration(Bucket=bucket, MetadataConfiguration={"JournalTableConfiguration":
    /// {"RecordExpiration": {"Expiration": expiration[, "Days": days]}}[, "InventoryTableConfiguration":
    /// {"ConfigurationState": inventory}]})</c>.
    /// </summary>
    public static Task<CreateBucketMetadataConfigurationResponse> CreateMetadataConfiguration(
        IAmazonS3 s3, string bucket, string expiration = "DISABLED", int? days = null, string? inventory = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var configuration = new MetadataConfiguration
        {
            JournalTableConfiguration = new JournalTableConfiguration
            {
                RecordExpiration = new RecordExpiration { Expiration = expiration, Days = days },
            },
        };
        if (inventory is not null)
        {
            configuration.InventoryTableConfiguration = new InventoryTableConfiguration { ConfigurationState = inventory };
        }

        return s3.CreateBucketMetadataConfigurationAsync(new CreateBucketMetadataConfigurationRequest
        {
            BucketName = bucket,
            MetadataConfiguration = configuration,
        });
    }

    /// <summary><c>s3.get_bucket_metadata_configuration(Bucket=bucket)["GetBucketMetadataConfigurationResult"]["MetadataConfigurationResult"]</c>.</summary>
    public static async Task<MetadataConfigurationResult> MetadataResult(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var response = await s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket });
        Ensure(response.GetBucketMetadataConfigurationResult is not null, "the response carries no GetBucketMetadataConfigurationResult");
        return response.GetBucketMetadataConfigurationResult.MetadataConfigurationResult;
    }

    /// <summary><c>s3.update_bucket_metadata_journal_table_configuration(Bucket=..., JournalTableConfiguration={"RecordExpiration": ...})</c>.</summary>
    public static Task<UpdateBucketMetadataJournalTableConfigurationResponse> UpdateJournal(IAmazonS3 s3, string bucket, string expiration, int? days = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.UpdateBucketMetadataJournalTableConfigurationAsync(new UpdateBucketMetadataJournalTableConfigurationRequest
        {
            BucketName = bucket,
            JournalTableConfiguration = new JournalTableConfigurationUpdates
            {
                RecordExpiration = new RecordExpiration { Expiration = expiration, Days = days },
            },
        });
    }

    /// <summary><c>s3.update_bucket_metadata_inventory_table_configuration(Bucket=..., InventoryTableConfiguration={"ConfigurationState": state})</c>.</summary>
    public static Task<UpdateBucketMetadataInventoryTableConfigurationResponse> UpdateInventory(IAmazonS3 s3, string bucket, string state)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.UpdateBucketMetadataInventoryTableConfigurationAsync(new UpdateBucketMetadataInventoryTableConfigurationRequest
        {
            BucketName = bucket,
            InventoryTableConfiguration = new InventoryTableConfigurationUpdates { ConfigurationState = state },
        });
    }

    /// <summary><c>s3.update_bucket_metadata_annotation_table_configuration(Bucket=..., AnnotationTableConfiguration={...})</c>.</summary>
    public static Task<UpdateBucketMetadataAnnotationTableConfigurationResponse> UpdateAnnotationTable(IAmazonS3 s3, string bucket, string state, string? role = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.UpdateBucketMetadataAnnotationTableConfigurationAsync(new UpdateBucketMetadataAnnotationTableConfigurationRequest
        {
            BucketName = bucket,
            AnnotationTableConfiguration = new AnnotationTableConfigurationUpdates { ConfigurationState = state, Role = role },
        });
    }

    /// <summary><c>s3.delete_bucket_metadata_configuration(Bucket=bucket)</c>.</summary>
    public static Task<DeleteBucketMetadataConfigurationResponse> DeleteMetadataConfiguration(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.DeleteBucketMetadataConfigurationAsync(new DeleteBucketMetadataConfigurationRequest { BucketName = bucket });
    }

    /// <summary>
    /// The checks' <c>_safe(s3.delete_bucket_metadata_configuration, Bucket=bucket)</c>: deletes the
    /// configuration, ignoring one (or a bucket) already gone.
    /// </summary>
    public static async Task SafeDeleteMetadataConfiguration(IAmazonS3 s3, string bucket)
    {
        if (await FidelityContext.CaptureError(() => DeleteMetadataConfiguration(s3, bucket)) is { } error
            && error.Code is not ("MetadataConfigurationNotFound" or "NoSuchBucket"))
        {
            throw error.Exception;
        }
    }

    /// <summary>A record expiration as boto3 hands it to a check: <c>{'Expiration': 'ENABLED', 'Days': 7}</c>.</summary>
    public static string Repr(RecordExpiration? expiration) =>
        expiration is null
            ? "None"
            : expiration.Days is { } days
                ? $"{{'Expiration': {Py.Repr(expiration.Expiration?.Value)}, 'Days': {days}}}"
                : $"{{'Expiration': {Py.Repr(expiration.Expiration?.Value)}}}";

    /// <summary>
    /// The checks' <c>_poll_until(probe, predicate, timeout_s=..., interval_s=...)</c>: polls on virtual
    /// time; raises "condition not met within {timeout_s}s; last result: ..." when it runs out.
    /// </summary>
    public static Task<T> PollMetadata<T>(Func<Task<T>> probe, Func<T, bool> done, double timeoutS, double intervalS, string? description = null) =>
        PollUntil(
            probe,
            done,
            TimeSpan.FromSeconds(timeoutS),
            TimeSpan.FromSeconds(intervalS),
            last => description is null
                ? $"condition not met within {timeoutS}s; last result: {last}"
                : $"{description} not met within {timeoutS}s. Last result: {last}");
}

/// <summary>Retries and probes the S3 Metadata ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// The checks' <c>_update_..._with_retry</c>: retries <paramref name="call"/> while it is refused with
    /// <c>InvalidRequest</c> (the journal table still settling), within a bounded window; re-raises the
    /// last refusal when the window closes, and any other error at once.
    /// </summary>
    public static async Task RetryOnInvalidRequest(Func<Task> call, double timeoutS, double intervalS)
    {
        ArgumentNullException.ThrowIfNull(call);
        var clock = FidelityTime.StartNew();
        AwsError? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await FidelityContext.CaptureError(call);
            if (last is null)
            {
                return;
            }

            if (last.Code != "InvalidRequest")
            {
                throw last.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw (Exception?)last?.Exception ?? Fail("the retry window closed before the first attempt");
    }

    /// <summary>
    /// The checks' <c>_retry_until_success(fn, timeout_s=..., interval_s=...)</c>: retries through any
    /// error within a bounded window, failing with the last one.
    /// </summary>
    public static async Task RetryUntilSuccess(Func<Task> call, double timeoutS, double intervalS)
    {
        ArgumentNullException.ThrowIfNull(call);
        var clock = FidelityTime.StartNew();
        AwsError? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await FidelityContext.CaptureError(call);
            if (last is null)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"operation kept failing for {timeoutS}s: {last}");
    }

    /// <summary>The journal table result's <c>TableStatus</c>.</summary>
    public static async Task<string?> JournalStatus(IAmazonS3 s3, string bucket) =>
        (await MetadataResult(s3, bucket)).JournalTableConfigurationResult?.TableStatus;

    /// <summary><c>repr()</c> of a journal table result, as the checks print it.</summary>
    public static string Repr(JournalTableConfigurationResult? journal) =>
        journal is null
            ? "None"
            : $"{{'TableStatus': {Py.Repr(journal.TableStatus)}, 'TableName': {Py.Repr(journal.TableName)}, 'TableArn': {Py.Repr(journal.TableArn)}, " +
              $"'RecordExpiration': {Repr(journal.RecordExpiration)}{(journal.Error is { } e ? $", 'Error': {Repr(e)}" : string.Empty)}}}";

    /// <summary><c>repr()</c> of a table's error details.</summary>
    public static string Repr(ErrorDetails? error) =>
        error is null ? "None" : $"{{'ErrorCode': {Py.Repr(error.ErrorCode)}, 'ErrorMessage': {Py.Repr(error.ErrorMessage)}}}";

    /// <summary><c>repr()</c> of an inventory table result.</summary>
    public static string Repr(InventoryTableConfigurationResult? inventory) =>
        inventory is null
            ? "None"
            : $"{{'ConfigurationState': {Py.Repr(inventory.ConfigurationState?.Value)}, 'TableStatus': {Py.Repr(inventory.TableStatus)}, 'TableName': {Py.Repr(inventory.TableName)}}}";

    /// <summary><c>repr()</c> of an annotation table result.</summary>
    public static string Repr(AnnotationTableConfigurationResult? annotation) =>
        annotation is null
            ? "None"
            : $"{{'ConfigurationState': {Py.Repr(annotation.ConfigurationState?.Value)}, 'TableStatus': {Py.Repr(annotation.TableStatus)}, 'Role': {Py.Repr(annotation.Role)}}}";

    /// <summary><c>repr()</c> of a whole metadata configuration result.</summary>
    public static string Repr(MetadataConfigurationResult? result) =>
        result is null
            ? "None"
            : $"{{'JournalTableConfigurationResult': {Repr(result.JournalTableConfigurationResult)}, " +
              $"'InventoryTableConfigurationResult': {Repr(result.InventoryTableConfigurationResult)}, " +
              $"'AnnotationTableConfigurationResult': {Repr(result.AnnotationTableConfigurationResult)}}}";
}

/// <summary>The metadata-table checks' shared steps.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// The checks' <c>_create_metadata_configuration</c>: creates the DISABLED-expiration journal
    /// configuration, failing on an access refusal with the checks' message.
    /// </summary>
    public static async Task CreateMetadataConfigurationOrFail(IAmazonS3 s3, string bucket)
    {
        if (await FidelityContext.CaptureError(() => CreateMetadataConfiguration(s3, bucket)) is { } error)
        {
            if (error.Code is "AccessDenied" or "AccessDeniedException")
            {
                throw Fail(
                    "test role lacks the s3tables:* permissions CreateBucketMetadataConfiguration " +
                    "needs to provision the AWS-managed table bucket");
            }

            throw error.Exception;
        }
    }

    /// <summary>
    /// The checks' <c>_poll_journal_until(s3, bucket, terminal_statuses, timeout_s, interval_s)</c>:
    /// the journal table result once its status is one of <paramref name="terminal"/>.
    /// </summary>
    public static async Task<JournalTableConfigurationResult> PollJournalUntil(
        IAmazonS3 s3, string bucket, string[] terminal, double timeoutS = 180.0, double intervalS = 5.0)
    {
        var clock = FidelityTime.StartNew();
        JournalTableConfigurationResult? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = (await MetadataResult(s3, bucket)).JournalTableConfigurationResult;
            if (terminal.Contains(last.TableStatus))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"status never reached {Py.Repr(terminal)}; last response={Repr(last)}");
    }
}
