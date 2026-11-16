// Translated from behavioral/dynamodb/check_execute_statement_select_multi_attribute_gsi_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-select-multi-attribute-gsi-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementSelectMultiAttributeGsiKeyRejected
{
    private const string CheckId = "execute-statement-select-multi-attribute-gsi-key-rejected";
    private const string MultiHash = "multi-hash-index";
    private const string MultiRange = "multi-range-index";
    private const string Single = "single-attr-index";
    private const string Lsi = "lsi1";

    private static readonly (string Code, string Message) MultiAttrError = (
        "ValidationException",
        "PartiQL statements cannot be executed on tables with multi-attribute HASH keys or "
        + "multi-attribute RANGE keys.");

    private static Dictionary<string, AttributeValue> ItemValue() => Item(
        ("pk", S("p1")), ("sk", S("s1")), ("tenant", S("acme")), ("region", S("eu")),
        ("ts", S("01")), ("seq", N("1")), ("cat", S("c1")), ("category", S("c1")),
        ("lsk", N("5")));

    /// <summary>
    /// PartiQL SELECT addressed at a GSI with a multi-attribute HASH or RANGE key is refused with
    /// ValidationException under every WHERE shape, but a syntax or literal/parameter conversion
    /// error is reported first; the base table, a single-attribute GSI and an LSI are unaffected.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-select-multi-attribute-gsi-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pq-mak-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("tenant", ScalarAttributeType.S),
                new AttributeDefinition("region", ScalarAttributeType.S),
                new AttributeDefinition("ts", ScalarAttributeType.S),
                new AttributeDefinition("seq", ScalarAttributeType.N),
                new AttributeDefinition("cat", ScalarAttributeType.S),
                new AttributeDefinition("category", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = MultiHash,
                    KeySchema =
                    [
                        new KeySchemaElement("tenant", KeyType.HASH),
                        new KeySchemaElement("region", KeyType.HASH),
                        new KeySchemaElement("ts", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new GlobalSecondaryIndex
                {
                    IndexName = MultiRange,
                    KeySchema =
                    [
                        new KeySchemaElement("cat", KeyType.HASH),
                        new KeySchemaElement("ts", KeyType.RANGE),
                        new KeySchemaElement("seq", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new GlobalSecondaryIndex
                {
                    IndexName = Single,
                    KeySchema = [new KeySchemaElement("category", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var desc = await Poll(
            async () => (await ddb.DescribeTableAsync(table)).Table,
            t => t.TableStatus == TableStatus.ACTIVE && t.GlobalSecondaryIndexes.All(g => g.IndexStatus == IndexStatus.ACTIVE),
            budgetSeconds: BudgetSeconds(ctx, 600.0),
            pollSeconds: 5.0);
        Ensure(desc.GlobalSecondaryIndexes.Count == 3, $"expected 3 GSIs active, got {Py.Repr(desc.GlobalSecondaryIndexes.Select(g => g.IndexName).ToList())}");

        await ddb.PutItemAsync(table, ItemValue());

        Task<ExecuteStatementResponse> Execute(string statement, List<AttributeValue>? parameters = null) =>
            ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, Parameters = parameters });

        // --- every SELECT shape against the two-HASH-attribute GSI gets the same refusal ---
        await ExpectError(() => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\""), MultiAttrError.Code, MultiAttrError.Message);
        await ExpectError(
            () => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE tenant='acme' AND region='eu' ORDER BY ts DESC"),
            MultiAttrError.Code,
            MultiAttrError.Message);
        await ExpectError(() => Execute($"SELECT COUNT(*) FROM \"{table}\".\"{MultiHash}\""), MultiAttrError.Code, MultiAttrError.Message);
        await ExpectError(() => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE category='c1'"), MultiAttrError.Code, MultiAttrError.Message);
        await ExpectError(() => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE tenant=5 AND region='eu'"), MultiAttrError.Code, MultiAttrError.Message);
        await ExpectError(
            () => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE tenant = ? AND region = ?", [N("5"), S("eu")]),
            MultiAttrError.Code,
            MultiAttrError.Message);
        // A full, correctly typed key-equality query is refused the same way: there is no WHERE shape
        // that lets a SELECT through a multi-attribute-key GSI succeed.
        await ExpectError(
            () => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE tenant='acme' AND region='eu'"),
            MultiAttrError.Code,
            MultiAttrError.Message);

        // --- the two-RANGE-attribute GSI is refused the same way (representative probe) ---
        await ExpectError(() => Execute($"SELECT * FROM \"{table}\".\"{MultiRange}\" WHERE cat='c1'"), MultiAttrError.Code, MultiAttrError.Message);

        // --- a syntax error, and an unconvertible literal or parameter, are reported first ---
        await ExpectError(
            () => Execute($"SELEKT * FROM \"{table}\".\"{MultiHash}\""),
            "ValidationException",
            "Statement wasn't well formed, can't be processed: Expected data manipulation");
        await ExpectError(
            () => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE tenant = 'acme, AND region='eu'"),
            "ValidationException",
            "Statement wasn't well formed, can't be processed: ");
        await ExpectError(
            () => Execute($"SELECT * FROM \"{table}\".\"{MultiHash}\" WHERE tenant = ? AND region = ?", [N("notanumber"), S("eu")]),
            "ValidationException",
            "The parameter cannot be converted to a numeric value: notanumber");

        // --- positive controls: the base table, the single-attribute GSI and the LSI still work ---
        // The GSI fans out asynchronously: poll until the item reaches it.
        var select = await Poll(
            async () => (await Execute($"SELECT * FROM \"{table}\".\"{Single}\" WHERE category='c1'")).Items,
            items => Py.Equal(items, new[] { ItemValue() }),
            budgetSeconds: BudgetSeconds(ctx, 60.0),
            pollSeconds: 1.0);
        Ensure(Py.Equal(select, new[] { ItemValue() }), $"single-attribute GSI SELECT returned {Py.Repr(select)}");

        select = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"{Lsi}\" WHERE pk='p1'", ConsistentRead = true })).Items;
        Ensure(Py.Equal(select, new[] { ItemValue() }), $"LSI SELECT returned {Py.Repr(select)}");

        select = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE pk='p1' AND sk='s1'", ConsistentRead = true })).Items;
        Ensure(Py.Equal(select, new[] { ItemValue() }), $"base-table SELECT returned {Py.Repr(select)}");

        await Execute($"INSERT INTO \"{table}\" VALUE {{'pk':'p2','sk':'s2'}}");
        var inserted = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p2")), ("sk", S("s2"))), ConsistentRead = true });
        Ensure(Py.Equal(inserted.Item, Item(("pk", S("p2")), ("sk", S("s2")))), $"the base-table INSERT did not create the item, got {Py.Repr(inserted.Item)}");

        await Execute($"UPDATE \"{table}\" SET extra='x' WHERE pk='p2' AND sk='s2'");
        var updated = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p2")), ("sk", S("s2"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(updated.GetValueOrDefault("extra"), S("x")), $"the base-table UPDATE did not apply, got {Py.Repr(updated)}");

        await Execute($"DELETE FROM \"{table}\" WHERE pk='p2' AND sk='s2'");
        var deleted = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p2")), ("sk", S("s2"))), ConsistentRead = true });
        Ensure(deleted.Item is null, "the base-table DELETE did not remove the item");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises; only <c>notFound</c> means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Assert <c>fn</c> raises exactly <c>(code, message)</c>; an unexpected success raises, naming it.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        Ensure(error.Code == code, $"expected {Py.Repr(code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
        Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
    }

    /// <summary>A poll's timeout: capSeconds, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary><c>read()</c> once <c>done(value)</c>; raises naming the last value when the budget runs out.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, double budgetSeconds, double pollSeconds)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(budgetSeconds))
            {
                throw Fail($"condition not met within {budgetSeconds}s; last value {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollSeconds));
        }
    }
}
