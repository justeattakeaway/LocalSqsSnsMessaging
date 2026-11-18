// Translated from behavioral/dynamodb/check_ddbglobal_legacy_2017_global_table_apis_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbglobal-legacy-2017-global-table-apis-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbglobalLegacy2017GlobalTableApisRejected
{
    private const string CheckId = "ddbglobal-legacy-2017-global-table-apis-rejected";
    private const string ErrValidation = "ValidationException";
    private const string ErrGtNotFound = "GlobalTableNotFoundException";

    /// <summary>The withdrawn global-tables version; only it injected aws:rep:* attributes into replicated items.</summary>
    private const string LegacyVersion = "2017.11.29";

    private const string LegacyMessage = "One or more parameter values were invalid: DynamoDB global tables version 2017.11.29 is not supported. We recommend using DynamoDB global tables version 2019.11.21, instead of version 2017.11.29 (Legacy).";

    /// <summary>
    /// CreateGlobalTable and UpdateGlobalTable refuse version 2017.11.29 with ValidationException
    /// even when its precondition is met, and nothing replicates: no global table, no replicas, each
    /// region keeps only its own item, and no aws:rep:* attributes appear.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbglobal-legacy-2017-global-table-apis-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var regionA = ctx.Region;
        var regionB = DerivedSecondRegion(ctx);
        var ddbA = ctx.Dynamo;
        var ddbB = ctx.DynamoClient(region: regionB);

        // ONE name, deliberately: version 2017.11.29 required identically-named tables in every
        // participating region, and the point of this check is that the precondition is met.
        var table = FidelityContext.UniqueName("sim2real-behav-ddbglobal-legacy-");
        var itemA = Item(("pk", S("shared-key")), ("origin", S(regionA)), ("payload", S("written-in-region-a")), ("n", N("11")));
        var itemB = Item(("pk", S("shared-key")), ("origin", S(regionB)), ("payload", S("written-in-region-b")), ("n", N("22")));
        foreach (var (client, item) in new[] { (ddbA, itemA), (ddbB, itemB) })
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,

                // The 2017.11.29 precondition, verbatim: streams on, NEW_AND_OLD_IMAGES.
                StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
                Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            });

            // Registered before the next call so neither region's table is stranded by a failing assert.
            ctx.OnCleanup(() => DeleteTableIgnore(client, table));
            await WaitTableActive(client, table);
            await client.PutItemAsync(table, item);
        }

        // POSITIVE CONTROL: both regions are live and serving the data plane, so every absence
        // asserted below is an absence of REPLICATION rather than of a working table.
        var gotA = await GetItem(ddbA, table, "shared-key");
        Ensure(Py.Equal(gotA, itemA), $"region {regionA} read back {Py.Repr(gotA)}, wrote {Py.Repr(itemA)}");
        var gotB = await GetItem(ddbB, table, "shared-key");
        Ensure(Py.Equal(gotB, itemB), $"region {regionB} read back {Py.Repr(gotB)}, wrote {Py.Repr(itemB)}");

        var (code, message) = await Refusal(() => ddbA.CreateGlobalTableAsync(new CreateGlobalTableRequest
        {
            GlobalTableName = table,
            ReplicationGroup = [new Replica { RegionName = regionA }, new Replica { RegionName = regionB }],
        }));
        Ensure(
            code == ErrValidation,
            $"CreateGlobalTable over two identically-named stream-enabled tables returned {Py.Repr(code)}, "
            + $"expected {Py.Repr(ErrValidation)}; message was {Py.Repr(message)}");
        Ensure(message == LegacyMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure(
            message.Contains($"{LegacyVersion} is not supported", StringComparison.Ordinal),
            $"CreateGlobalTable was refused, but not for the withdrawn-version reason: {Py.Repr(message)}");

        (code, message) = await Refusal(() => ddbA.UpdateGlobalTableAsync(new UpdateGlobalTableRequest
        {
            GlobalTableName = table,
            ReplicaUpdates = [new ReplicaUpdate { Create = new CreateReplicaAction { RegionName = regionB } }],
        }));
        Ensure(
            code == ErrValidation,
            $"UpdateGlobalTable returned {Py.Repr(code)}, expected {Py.Repr(ErrValidation)}; "
            + $"message was {Py.Repr(message)}");
        Ensure(message == LegacyMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure(
            message.Contains($"{LegacyVersion} is not supported", StringComparison.Ordinal),
            $"UpdateGlobalTable was refused, but not for the withdrawn-version reason: {Py.Repr(message)}");

        // EFFECT ABSENT, part 1: no global table was created by the refused calls.
        (code, message) = await Refusal(() => ddbA.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = table }));
        Ensure(
            code == ErrGtNotFound,
            $"DescribeGlobalTable returned {Py.Repr(code)} after the refusals, expected "
            + $"{Py.Repr(ErrGtNotFound)} -- a refused CreateGlobalTable must leave nothing behind; "
            + $"message was {Py.Repr(message)}");
        Ensure(
            message == $"Global table not found: Global table with name: '{table}' does not exist.",
            $"expected AWS's exact GlobalTableNotFoundException message, got {Py.Repr(message)}");

        // Scoped to this check's unique name: the account may hold other global tables.
        var listed = (await AllGlobalTableNames(ddbA)).Where(n => n == table).ToList();
        Ensure(listed.Count == 0, $"ListGlobalTables in {regionA} reports {Py.Repr(listed)} after the refusals");

        // EFFECT ABSENT, part 2: no 2019.11.21 replica relationship was created either, in either
        // direction -- a target that "helpfully" upgraded the refused call would show one here.
        foreach (var (client, region) in new[] { (ddbA, regionA), (ddbB, regionB) })
        {
            var described = (await client.DescribeTableAsync(table)).Table;
            Ensure(
                described.Replicas is not { Count: > 0 },
                $"DescribeTable in {region} reports replicas {Py.Repr(described.Replicas?.Select(r => r.RegionName).ToList())} after "
                + "CreateGlobalTable was refused");
        }

        // EFFECT ABSENT, part 3: nothing replicated. Each region still holds only its own item, and
        // neither item carries any aws:rep:* attribute -- the metadata version 2017.11.29 documented
        // cannot appear, because the version that injected it is refused.
        var finalA = await GetItem(ddbA, table, "shared-key");
        Ensure(
            Py.Equal(finalA, itemA),
            $"region {regionA} item changed to {Py.Repr(finalA)} after the refused global-table calls; "
            + $"expected the locally written {Py.Repr(itemA)} and no replicated value");
        var finalB = await GetItem(ddbB, table, "shared-key");
        Ensure(
            Py.Equal(finalB, itemB),
            $"region {regionB} item changed to {Py.Repr(finalB)} after the refused global-table calls; "
            + $"expected the locally written {Py.Repr(itemB)} and no replicated value");
        var injected = (finalA?.Keys ?? Enumerable.Empty<string>()).Concat(finalB?.Keys ?? Enumerable.Empty<string>())
            .Where(k => k.StartsWith("aws:", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Ensure(
            injected.Count == 0,
            $"items carry AWS-injected replication metadata {Py.Repr(injected)}; version {LegacyVersion} "
            + "is the only version that ever added aws:rep:* attributes and real AWS refuses it");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds)
    {
        var clock = FidelityTime.StartNew();
        T last = default!;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s, last value: {Py.Repr(last)}");
    }

    private static Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 180) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value,
            s => s == "ACTIVE",
            timeoutSeconds,
            3);

    /// <summary>Strongly consistent GetItem, so a stale read cannot decide a replication assertion.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> GetItem(IAmazonDynamoDB ddb, string table, string key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;

    /// <summary>(error code, message) for a call real AWS must refuse; an unexpected success raises.</summary>
    private static async Task<(string? Code, string Message)> Refusal<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>All global table names, following ListGlobalTables pagination.</summary>
    private static async Task<List<string>> AllGlobalTableNames(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        var request = new ListGlobalTablesRequest();
        while (true)
        {
            var page = await ddb.ListGlobalTablesAsync(request);
            names.AddRange((page.GlobalTables ?? []).Select(g => g.GlobalTableName));
            var last = page.LastEvaluatedGlobalTableName;
            if (string.IsNullOrEmpty(last))
            {
                return names;
            }

            request = new ListGlobalTablesRequest { ExclusiveStartGlobalTableName = last };
        }
    }

    /// <summary>us-east-1, or us-west-2 when ctx.region is us-east-1; both are enabled in every account.</summary>
    private static string DerivedSecondRegion(FidelityContext ctx) => ctx.Region != "us-east-1" ? "us-east-1" : "us-west-2";

    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
