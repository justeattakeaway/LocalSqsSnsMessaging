// Translated from behavioral/dynamodb/check_partiql_select_index_qualifier_projection_follows_index_type.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-index-qualifier-projection-follows-index-type</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectIndexQualifierProjectionFollowsIndexType
{
    private const string CheckId = "partiql-select-index-qualifier-projection-follows-index-type";
    private const string Gsi = "gsi-keys-only";
    private const string Lsi = "lsi-keys-only";

    /// <summary>A poll's budget: capS from now, bounded by the runner's remaining time less 5 s.</summary>
    private static TimeSpan Deadline(FidelityContext ctx, double capS) =>
        TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0)));

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the statement to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL SELECT through a KEYS_ONLY GSI qualifier projects only its key and the table
    /// keys; through a KEYS_ONLY LSI it table-fetches an unprojected attribute named in the SELECT
    /// list; naming that same attribute through the GSI is refused.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-index-qualifier-projection-follows-index-type")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlidx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsiPk2", ScalarAttributeType.S),
                new AttributeDefinition("lsiSk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gsiPk2", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsiSk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        string? gsiStatus = null;
        var deadline = Deadline(ctx, 300.0);
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < deadline)
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            var gsis = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            gsiStatus = gsis.GetValueOrDefault(Gsi);
            if (gsiStatus == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gsiStatus == "ACTIVE", $"{Gsi} never became ACTIVE within the deadline: {Py.Repr(gsiStatus)}");

        await ddb.PutItemAsync(table, Item(
            ("pk", S("p1")), ("sk", S("s1")), ("gsiPk2", S("g1")), ("lsiSk", N("5")),
            ("extra", S("unprojected-value"))));

        // The GSI is eventually consistent, so poll for the write to appear before grading its shape.
        List<Dictionary<string, AttributeValue>> gsiItems = [];
        deadline = Deadline(ctx, 120.0);
        clock = FidelityTime.StartNew();
        while (clock.Elapsed < deadline)
        {
            gsiItems = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"SELECT * FROM \"{table}\".\"{Gsi}\" WHERE gsiPk2 = 'g1'",
            })).Items ?? [];
            if (gsiItems.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(gsiItems.Count == 1, $"expected exactly one item through the GSI, got {Py.Repr(gsiItems)}");
        Ensure(
            gsiItems[0].Keys.Order(StringComparer.Ordinal).SequenceEqual(["gsiPk2", "pk", "sk"]),
            $"a SELECT * through a KEYS_ONLY GSI must project only the GSI key and the table keys, got {Py.Repr(gsiItems[0].Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            Py.Equal(gsiItems[0], Item(("gsiPk2", S("g1")), ("pk", S("p1")), ("sk", S("s1")))),
            $"unexpected GSI key/table-key values: {Py.Repr(gsiItems[0])}");

        // The LSI supports a strongly consistent read, so a table fetch through it needs no poll.
        var lsiItems = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT extra FROM \"{table}\".\"{Lsi}\" WHERE pk = 'p1'",
            ConsistentRead = true,
        })).Items ?? [];
        Ensure(lsiItems.Count == 1, $"expected exactly one item through the LSI, got {Py.Repr(lsiItems)}");
        Ensure(
            Py.Equal(lsiItems[0], Item(("extra", S("unprojected-value")))),
            $"naming 'extra' in the SELECT list through a KEYS_ONLY LSI must table-fetch it from the base table, got {Py.Repr(lsiItems[0])}");

        // The same attribute, named through the GSI instead of the LSI, is refused: a GSI has no
        // base table to fetch from.
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT extra FROM \"{table}\".\"{Gsi}\" WHERE gsiPk2 = 'g1'",
            ConsistentRead = false,
        }));
        Ensure(
            code == "ValidationException",
            $"naming 'extra' in the SELECT list through the KEYS_ONLY GSI must be refused with ValidationException, got {code}: {Py.Repr(message)}");
        Ensure(
            message == $"One or more parameter values were invalid: Global secondary index {Gsi} does not project [extra]",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // The rejected SELECT is read-only: the base item is exactly as written.
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1")), ("sk", S("s1"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(kept, Item(
                ("pk", S("p1")), ("sk", S("s1")), ("gsiPk2", S("g1")), ("lsiSk", N("5")),
                ("extra", S("unprojected-value")))),
            $"the rejected SELECT must not have written anything; item now reads {Py.Repr(kept)}");
    }
}
