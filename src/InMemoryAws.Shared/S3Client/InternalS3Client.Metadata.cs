using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// S3 Metadata (Create/Get/Delete BucketMetadataConfiguration and the Update...TableConfiguration
/// operations). The journal, inventory and annotation tables would live in S3 Tables, which the cloud
/// doesn't emulate: it keeps each configuration and the tables' status as S3 answers them. A table is
/// <c>CREATING</c> for <see cref="InMemoryAwsCloud.S3MetadataTableCreationDelay"/>, then <c>ACTIVE</c>.
/// </summary>
/// <remarks>
/// Deleting a configuration leaves its journal table in the AWS managed table bucket, as S3 does, so a
/// configuration created again for the same bucket fails (<c>FAILED</c>,
/// <c>JournalTableAlreadyExists</c>) once its journal table would have been created.
/// </remarks>
internal sealed partial class InternalS3Client
{
    private const string JournalBeingCreated =
        "You can't perform this operation while the journal table is being created. Wait for your journal table status to transition to ACTIVE, and then try again.";

    private const string JournalNotActive =
        "You can't perform this operation on a journal table that doesn't have an ACTIVE status. Wait for your journal table status to transition to ACTIVE, and then try again. If creation of the journal table fails, you must re-create the metadata configuration for your general purpose bucket.";

    /// <summary>Creates a bucket's metadata configuration; a bucket holds one (<c>MetadataConfigurationAlreadyExists</c>).</summary>
    public override Task<CreateBucketMetadataConfigurationOutput> CreateBucketMetadataConfigurationAsync(CreateBucketMetadataConfigurationRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.MetadataConfiguration;
        var journal = configuration?.JournalTableConfiguration ?? throw S3Errors.MalformedXml();
        var expiration = CheckRecordExpiration(journal.RecordExpiration);
        var inventoryState = configuration.InventoryTableConfiguration is { } inventory ? CheckConfigurationState(inventory.ConfigurationState) : null;
        string? annotationState = null;
        if (configuration.AnnotationTableConfiguration is { } annotation)
        {
            annotationState = CheckConfigurationState(annotation.ConfigurationState);
            CheckAnnotationRole(annotationState, annotation.Role);
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (bucket.MetadataConfiguration is not null)
            {
                throw new S3Exception("MetadataConfigurationAlreadyExists", "The metadata configuration already exists", HttpStatusCode.Conflict);
            }

            var ready = Now + _cloud.S3MetadataTableCreationDelay;
            var tableBucket = $"arn:aws:s3tables:{bucket.Region}:{_cloud.CurrentAccountId}:bucket/aws-s3";
            bucket.MetadataConfiguration = new S3MetadataConfiguration
            {
                TableBucketArn = tableBucket,
                Namespace = "b_" + bucket.Name,
                JournalTableArn = $"{tableBucket}/table/{Guid.NewGuid()}",
                JournalExpiration = expiration,
                JournalReadyAt = ready,
                JournalFails = bucket.MetadataJournalTableLeftOver,
                InventoryState = inventoryState,
                InventoryReadyAt = ready,
                InventoryTableArn = $"{tableBucket}/table/{Guid.NewGuid()}",
                AnnotationState = annotationState,
                AnnotationRole = configuration.AnnotationTableConfiguration?.Role,
                AnnotationReadyAt = ready,
                AnnotationTableArn = $"{tableBucket}/table/{Guid.NewGuid()}",
            };
            bucket.MetadataJournalTableLeftOver = true;
            return Task.FromResult(new CreateBucketMetadataConfigurationOutput());
        }
    }

    /// <summary>The configuration and its tables' status; <c>MetadataConfigurationNotFound</c> when there is none.</summary>
    public override Task<GetBucketMetadataConfigurationOutput> GetBucketMetadataConfigurationAsync(GetBucketMetadataConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var configuration = MetadataConfiguration(GetBucket(request.Bucket));
            var now = Now;
            var journalStatus = configuration.JournalStatus(now);
            var result = new MetadataConfigurationResult
            {
                DestinationResult = new DestinationResult
                {
                    TableBucketType = "aws",
                    TableBucketArn = configuration.TableBucketArn,
                    TableNamespace = configuration.Namespace,
                },
                JournalTableConfigurationResult = new JournalTableConfigurationResult
                {
                    TableStatus = journalStatus,
                    TableName = "journal",
                    TableArn = configuration.JournalTableArn,
                    RecordExpiration = new RecordExpiration { Expiration = configuration.JournalExpiration.Expiration, Days = configuration.JournalExpiration.Days },
                    Error = journalStatus == "FAILED"
                        ? new ErrorDetails
                        {
                            ErrorCode = "JournalTableAlreadyExists",
                            ErrorMessage = "A journal table already exists in the AWS managed table bucket's namespace. Delete the journal table, and then try again. To create a new S3 Metadata table, you must delete the S3 Metadata configuration for this bucket, and then create a new S3 Metadata configuration.",
                        }
                        : null,
                },
            };
            if (configuration.InventoryState is { } inventoryState)
            {
                result.InventoryTableConfigurationResult = inventoryState == "ENABLED"
                    ? new InventoryTableConfigurationResult
                    {
                        ConfigurationState = inventoryState,
                        TableStatus = configuration.TableStatus(configuration.InventoryReadyAt, now),
                        TableName = "inventory",
                        TableArn = configuration.InventoryTableArn,
                    }
                    : new InventoryTableConfigurationResult { ConfigurationState = inventoryState };
            }

            if (configuration.AnnotationState is { } annotationState)
            {
                result.AnnotationTableConfigurationResult = annotationState == "ENABLED"
                    ? new AnnotationTableConfigurationResult
                    {
                        ConfigurationState = annotationState,
                        TableStatus = configuration.TableStatus(configuration.AnnotationReadyAt, now),
                        TableName = "annotation",
                        TableArn = configuration.AnnotationTableArn,
                        Role = configuration.AnnotationRole,
                    }
                    : new AnnotationTableConfigurationResult { ConfigurationState = annotationState };
            }

            return Task.FromResult(new GetBucketMetadataConfigurationOutput
            {
                GetBucketMetadataConfigurationResult = new GetBucketMetadataConfigurationResult { MetadataConfigurationResult = result },
            });
        }
    }

    /// <summary>Deletes the configuration. Its tables stay in the AWS managed table bucket.</summary>
    public override Task<DeleteBucketMetadataConfigurationOutput> DeleteBucketMetadataConfigurationAsync(DeleteBucketMetadataConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            MetadataConfiguration(bucket);
            bucket.MetadataConfiguration = null;
            return Task.FromResult(new DeleteBucketMetadataConfigurationOutput());
        }
    }

    /// <summary>Changes the journal table's record expiration; the journal table must be <c>ACTIVE</c>.</summary>
    public override Task<UpdateBucketMetadataJournalTableConfigurationOutput> UpdateBucketMetadataJournalTableConfigurationAsync(UpdateBucketMetadataJournalTableConfigurationRequest request, CancellationToken cancellationToken)
    {
        var updates = request.JournalTableConfiguration ?? throw S3Errors.MalformedXml();
        var expiration = CheckRecordExpiration(updates.RecordExpiration);
        lock (Store.Gate)
        {
            var configuration = MetadataConfiguration(GetBucket(request.Bucket));
            if (configuration.JournalStatus(Now) != "ACTIVE")
            {
                throw S3Errors.InvalidRequest(JournalNotActive);
            }

            configuration.JournalExpiration = expiration;
            return Task.FromResult(new UpdateBucketMetadataJournalTableConfigurationOutput());
        }
    }

    /// <summary>Enables or disables the live inventory table; the journal table must be <c>ACTIVE</c>.</summary>
    public override Task<UpdateBucketMetadataInventoryTableConfigurationOutput> UpdateBucketMetadataInventoryTableConfigurationAsync(UpdateBucketMetadataInventoryTableConfigurationRequest request, CancellationToken cancellationToken)
    {
        var state = CheckConfigurationState((request.InventoryTableConfiguration ?? throw S3Errors.MalformedXml()).ConfigurationState);
        lock (Store.Gate)
        {
            var configuration = MetadataConfiguration(GetBucket(request.Bucket));
            CheckJournalActiveForUpdate(configuration);
            if (state == "ENABLED" && configuration.InventoryState != "ENABLED")
            {
                configuration.InventoryReadyAt = Now + _cloud.S3MetadataTableCreationDelay;
            }

            configuration.InventoryState = state;
            return Task.FromResult(new UpdateBucketMetadataInventoryTableConfigurationOutput());
        }
    }

    /// <summary>
    /// Enables (with the role S3 assumes to manage it) or disables the annotation table; the journal
    /// table must be <c>ACTIVE</c>.
    /// </summary>
    public override Task<UpdateBucketMetadataAnnotationTableConfigurationOutput> UpdateBucketMetadataAnnotationTableConfigurationAsync(UpdateBucketMetadataAnnotationTableConfigurationRequest request, CancellationToken cancellationToken)
    {
        var updates = request.AnnotationTableConfiguration ?? throw S3Errors.MalformedXml();
        var state = CheckConfigurationState(updates.ConfigurationState);
        lock (Store.Gate)
        {
            var configuration = MetadataConfiguration(GetBucket(request.Bucket));
            CheckJournalActiveForUpdate(configuration);
            CheckAnnotationRole(state, updates.Role);
            if (state == "ENABLED" && configuration.AnnotationState != "ENABLED")
            {
                configuration.AnnotationReadyAt = Now + _cloud.S3MetadataTableCreationDelay;
            }

            configuration.AnnotationState = state;
            configuration.AnnotationRole = state == "ENABLED" ? updates.Role : null;
            return Task.FromResult(new UpdateBucketMetadataAnnotationTableConfigurationOutput());
        }
    }

    private static S3MetadataConfiguration MetadataConfiguration(S3Bucket bucket) =>
        bucket.MetadataConfiguration
            ?? throw new S3Exception("MetadataConfigurationNotFound", "The metadata configuration was not found", HttpStatusCode.NotFound);

    /// <summary>The inventory and annotation tables change only once the journal table is <c>ACTIVE</c>.</summary>
    private void CheckJournalActiveForUpdate(S3MetadataConfiguration configuration)
    {
        switch (configuration.JournalStatus(Now))
        {
            case "CREATING":
                throw S3Errors.InvalidRequest(JournalBeingCreated);
            case "FAILED":
                throw S3Errors.InvalidRequest(JournalNotActive);
        }
    }

    /// <summary>
    /// A journal table's record expiration: <c>ENABLED</c> with <c>Days</c> from 7 to 2147483647
    /// (<c>InvalidRequest</c>), or <c>DISABLED</c>.
    /// </summary>
    private static (string Expiration, int? Days) CheckRecordExpiration(RecordExpiration? expiration)
    {
        switch (expiration?.Expiration)
        {
            case "ENABLED":
                if (expiration.Days is not >= 7)
                {
                    throw S3Errors.InvalidRequest(
                        "When RecordExpiration is set to ENABLED, the Days value must be specified. This value must be a whole number from 7 to 2147483647. Specify a valid Days value, and then try again.");
                }

                return ("ENABLED", expiration.Days);
            case "DISABLED":
                return ("DISABLED", null);
            default:
                throw S3Errors.MalformedXml();
        }
    }

    private static string CheckConfigurationState(string? state) =>
        state is "ENABLED" or "DISABLED" ? state : throw S3Errors.MalformedXml();

    /// <summary>An enabled annotation table needs the role S3 assumes to manage it.</summary>
    private static void CheckAnnotationRole(string state, string? role)
    {
        if (state == "ENABLED" && string.IsNullOrEmpty(role))
        {
            throw S3Errors.InvalidRequest("When AnnotationTableConfiguration is set to ENABLED, Role must be specified. Specify a valid Role ARN, and then try again.");
        }
    }
}

/// <summary>A bucket's S3 Metadata configuration: its tables' settings and when each is created.</summary>
internal sealed class S3MetadataConfiguration
{
    public required string TableBucketArn { get; init; }

    public required string Namespace { get; init; }

    public required string JournalTableArn { get; init; }

    public (string Expiration, int? Days) JournalExpiration { get; set; }

    public DateTime JournalReadyAt { get; init; }

    /// <summary>Whether creating the journal table fails: a journal table from an earlier configuration is still there.</summary>
    public bool JournalFails { get; init; }

    /// <summary><c>ENABLED</c> or <c>DISABLED</c>; null when never configured.</summary>
    public string? InventoryState { get; set; }

    public DateTime InventoryReadyAt { get; set; }

    public required string InventoryTableArn { get; init; }

    /// <summary><c>ENABLED</c> or <c>DISABLED</c>; null when never configured.</summary>
    public string? AnnotationState { get; set; }

    public string? AnnotationRole { get; set; }

    public DateTime AnnotationReadyAt { get; set; }

    public required string AnnotationTableArn { get; init; }

    /// <summary><c>CREATING</c> until the journal table is created, then <c>ACTIVE</c>, or <c>FAILED</c>.</summary>
    public string JournalStatus(DateTime now) => now < JournalReadyAt ? "CREATING" : JournalFails ? "FAILED" : "ACTIVE";

    /// <summary>An inventory or annotation table's status: <c>CREATING</c> until it (and the journal table) is created.</summary>
    public string TableStatus(DateTime readyAt, DateTime now) =>
        now < readyAt || now < JournalReadyAt ? "CREATING" : JournalFails ? "FAILED" : "ACTIVE";
}

/// <summary>What a bucket keeps for S3 Metadata.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>The bucket's metadata configuration; null when it has none.</summary>
    public S3MetadataConfiguration? MetadataConfiguration { get; set; }

    /// <summary>Whether a metadata configuration created a journal table, which stays after the configuration is deleted.</summary>
    public bool MetadataJournalTableLeftOver { get; set; }
}
