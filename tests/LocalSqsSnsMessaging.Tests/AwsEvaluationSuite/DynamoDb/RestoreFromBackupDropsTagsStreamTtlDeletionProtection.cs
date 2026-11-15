// Translated from behavioral/dynamodb/check_restore_from_backup_drops_tags_stream_ttl_deletion_protection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.restore-from-backup-drops-tags-stream-ttl-deletion-protection</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreFromBackupDropsTagsStreamTtlDeletionProtection
{
    private const string Check = "restore-from-backup-drops-tags-stream-ttl-deletion-protection";
    private const string TtlAttr = "expiresAt";

    private static List<KeySchemaElement> KeySchema() => [new("pk", KeyType.HASH), new("sk", KeyType.RANGE)];

    private static List<AttributeDefinition> AttrDefs() =>
    [
        new("pk", ScalarAttributeType.S),
        new("sk", ScalarAttributeType.S),
        new("gsipk", ScalarAttributeType.S),
        new("gsisk", ScalarAttributeType.S),
        new("lsisk", ScalarAttributeType.S),
    ];

    private static List<KeySchemaElement> GsiKeySchema() => [new("gsipk", KeyType.HASH), new("gsisk", KeyType.RANGE)];

    private static List<KeySchemaElement> LsiKeySchema() => [new("pk", KeyType.HASH), new("lsisk", KeyType.RANGE)];

    private static List<Dictionary<string, string>> Boto(IEnumerable<KeySchemaElement>? schema) =>
        [.. (schema ?? []).Select(k => new Dictionary<string, string>(StringComparer.Ordinal) { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType.Value })];

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS = 3.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {Py.Repr(last)}");
    }

    private static async Task<TableDescription> WaitTableSettled(IAmazonDynamoDB ddb, string name, double timeoutS = 240)
    {
        async Task<TableDescription?> Probe()
        {
            var t = (await ddb.DescribeTableAsync(name)).Table;

            // A LocalSecondaryIndexDescription carries no IndexStatus (boto3's .get() is None), so only GSIs can be busy.
            var busy = (t.GlobalSecondaryIndexes ?? []).Where(i => i.IndexStatus is not null && i.IndexStatus != IndexStatus.ACTIVE).ToList();
            return t.TableStatus == TableStatus.ACTIVE && busy.Count == 0 ? t : null;
        }

        return (await PollUntil(Probe, t => t is not null, timeoutS: timeoutS, description: $"{name} ACTIVE with settled indexes"))!;
    }

    private static async Task DisableDeletionProtection(IAmazonDynamoDB ddb, string name)
    {
        var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest { TableName = name, DeletionProtectionEnabled = false }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Disable deletion protection (idempotent), then delete; retry while CREATING/UPDATING.</summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string name, double timeoutS = 240.0)
    {
        await DisableDeletionProtection(ddb, name);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task SafeDeleteBackup(IAmazonDynamoDB ddb, string backupArn)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteBackupAsync(new DeleteBackupRequest { BackupArn = backupArn }));
        if (error is not null && error.Code != "BackupNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// A table restored via RestoreTableFromBackup carries the source's key schema, GSIs
    /// and LSIs, but not its tags, stream specification, TTL setting or deletion protection.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_RestoreTableFromBackup.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.restore-from-backup-drops-tags-stream-ttl-deletion-protection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var start = FidelityTime.StartNew();
        double RemainingBudget(double total = 1700.0, double floor = 30.0) => Math.Max(floor, total - start.Elapsed.TotalSeconds);

        var ddb = ctx.Dynamo;
        var source = FidelityContext.UniqueName("sim2real-behav-rfb-settings-src-");
        ctx.OnCleanup(() => SafeDeleteTable(ddb, source));
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = source,
            KeySchema = KeySchema(),
            AttributeDefinitions = AttrDefs(),
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes = [new GlobalSecondaryIndex { IndexName = "gsi1", KeySchema = GsiKeySchema(), Projection = new Projection { ProjectionType = ProjectionType.ALL } }],
            LocalSecondaryIndexes = [new LocalSecondaryIndex { IndexName = "lsi1", KeySchema = LsiKeySchema(), Projection = new Projection { ProjectionType = ProjectionType.ALL } }],
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            DeletionProtectionEnabled = true,
            Tags = [new Tag { Key = "sim2real", Value = Check }, new Tag { Key = "team", Value = "payments" }],
        });
        var sourceDescribed = await WaitTableSettled(ddb, source);
        Ensure(
            sourceDescribed.StreamSpecification?.StreamEnabled == true,
            $"expected the source table's stream enabled, got {Py.Repr(sourceDescribed.StreamSpecification?.StreamEnabled)}");
        Ensure(
            sourceDescribed.DeletionProtectionEnabled == true,
            $"expected the source table's deletion protection on, got {Py.Repr(sourceDescribed.DeletionProtectionEnabled)}");
        await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = source,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = TtlAttr },
        });
        await PollUntil(
            async () => (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = source })).TimeToLiveDescription.TimeToLiveStatus.Value,
            s => s == "ENABLED",
            timeoutS: 90,
            description: "source TTL ENABLED");

        async Task<CreateBackupResponse?> AttemptCreateBackup()
        {
            var backupName = FidelityContext.UniqueName("sim2real-behav-rfb-settings-bk-");
            var (resp, error) = await FidelityContext.Capture(() => ddb.CreateBackupAsync(new CreateBackupRequest { TableName = source, BackupName = backupName }));
            if (error is not null)
            {
                if (error.Code == "ContinuousBackupsUnavailableException")
                {
                    return null;
                }

                throw error.Exception;
            }

            return resp;
        }

        var backupArn = (await PollUntil(AttemptCreateBackup, r => r is not null, timeoutS: 90, intervalS: 5, description: "CreateBackup accepted"))!.BackupDetails.BackupArn;
        ctx.OnCleanup(() => SafeDeleteBackup(ddb, backupArn));
        await PollUntil(
            async () => (await ddb.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backupArn })).BackupDescription.BackupDetails.BackupStatus.Value,
            s => s == "AVAILABLE",
            timeoutS: 120,
            description: "backup AVAILABLE");
        var target = FidelityContext.UniqueName("sim2real-behav-rfb-settings-dst-");
        await ddb.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest { TargetTableName = target, BackupArn = backupArn });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, target));
        var described = await WaitTableSettled(ddb, target, timeoutS: RemainingBudget());
        Ensure(
            Py.Equal(Boto(described.KeySchema), Boto(KeySchema())),
            $"expected the restored table to carry the source key schema {Py.Repr(Boto(KeySchema()))}, got {Py.Repr(Boto(described.KeySchema))}");
        var gsiSchemas = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => Boto(i.KeySchema), StringComparer.Ordinal);
        Ensure(
            Py.Equal(gsiSchemas.GetValueOrDefault("gsi1"), Boto(GsiKeySchema())),
            $"expected the GSI to carry over with schema {Py.Repr(Boto(GsiKeySchema()))}, got {Py.Repr(gsiSchemas)}");
        var lsiSchemas = (described.LocalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => Boto(i.KeySchema), StringComparer.Ordinal);
        Ensure(
            Py.Equal(lsiSchemas.GetValueOrDefault("lsi1"), Boto(LsiKeySchema())),
            $"expected the LSI to carry over with schema {Py.Repr(Boto(LsiKeySchema()))}, got {Py.Repr(lsiSchemas)}");
        var targetArn = described.TableArn;
        var targetTags = (await ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = targetArn })).Tags ?? [];
        Ensure(
            targetTags.Count == 0,
            $"expected the restored table to carry no tags from the source, got {Py.Repr(targetTags.Select(t => new Dictionary<string, string> { ["Key"] = t.Key, ["Value"] = t.Value }).ToList())}");
        var streamEnabled = described.StreamSpecification?.StreamEnabled ?? false;
        Ensure(
            !streamEnabled,
            $"expected the restored table to not inherit the source stream specification, got {Py.Repr(described.StreamSpecification is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["StreamEnabled"] = described.StreamSpecification.StreamEnabled, ["StreamViewType"] = described.StreamSpecification.StreamViewType?.Value })}");
        Ensure(described.LatestStreamArn is null, $"expected no stream ARN on the restored table, got {Py.Repr(described.LatestStreamArn)}");
        var targetTtl = (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = target })).TimeToLiveDescription;
        Ensure(
            targetTtl.TimeToLiveStatus == TimeToLiveStatus.DISABLED,
            $"expected TTL to not carry over to the restored table, got {Py.Repr(new Dictionary<string, object?> { ["TimeToLiveStatus"] = targetTtl.TimeToLiveStatus?.Value, ["AttributeName"] = targetTtl.AttributeName })}");
        Ensure(
            !(described.DeletionProtectionEnabled ?? false),
            $"expected deletion protection to not carry over to the restored table, got {Py.Repr(described.DeletionProtectionEnabled)}");
    }
}
