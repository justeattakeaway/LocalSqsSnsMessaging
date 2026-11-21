// Translated from behavioral/dynamodb/check_transact_write_items_maintain_gsi_and_lsi_entries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-maintain-gsi-and-lsi-entries</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsMaintainGsiAndLsiEntries
{
    private const string Check = "transact-write-items-maintain-gsi-and-lsi-entries";

    /// <summary>
    /// TransactWriteItems Puts, an Update that moves the GSI and LSI keys and a Delete are each reflected
    /// in both secondary indexes: entries appear, move to the new key and disappear.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/transaction-apis.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-maintain-gsi-and-lsi-entries")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N), new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("gsk", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "by-l",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "by-g",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH), new KeySchemaElement("gsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 300);
        var a = MakeItem("p", "a", "g1", 1, 10);
        var b = MakeItem("p", "b", "g1", 2, 20);
        var c = MakeItem("p", "c", "g1", 3, 5);

        async Task<List<Dictionary<string, AttributeValue>>> Lsi() => (await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "by-l",
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
        })).Items;

        Func<Task<List<Dictionary<string, AttributeValue>>>> Gsi(string gpk) => async () => (await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "by-g",
            KeyConditionExpression = "gpk = :g",
            ExpressionAttributeValues = Item((":g", S(gpk))),
        })).Items;

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = a } },
                new TransactWriteItem { Put = new Put { TableName = t, Item = b } },
            ],
        });
        var got = await Lsi();
        Ensure(Py.Equal(got, new[] { a, b }), $"after the first transaction the LSI held {Py.Repr(got)}");
        got = await Converged(Gsi("g1"), [a, b]);
        Ensure(Py.Equal(got, new[] { a, b }), $"after the first transaction the GSI partition g1 held {Py.Repr(got)}");

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = t,
                        Key = Item(("pk", S("p")), ("sk", S("a"))),
                        UpdateExpression = "SET gpk = :g, lsk = :l",
                        ExpressionAttributeValues = Item((":g", S("g2")), (":l", N("30"))),
                    },
                },
                new TransactWriteItem { Delete = new Delete { TableName = t, Key = Item(("pk", S("p")), ("sk", S("b"))) } },
                new TransactWriteItem { Put = new Put { TableName = t, Item = c } },
            ],
        });
        var moved = MakeItem("p", "a", "g2", 1, 30);
        got = await Lsi();
        Ensure(Py.Equal(got, new[] { c, moved }), $"after the second transaction the LSI held {Py.Repr(got)}");
        got = await Converged(Gsi("g1"), [c]);
        Ensure(Py.Equal(got, new[] { c }), $"after the second transaction the GSI partition g1 held {Py.Repr(got)}");
        got = await Converged(Gsi("g2"), [moved]);
        Ensure(Py.Equal(got, new[] { moved }), $"after the second transaction the GSI partition g2 held {Py.Repr(got)}");
    }

    private static Dictionary<string, AttributeValue> MakeItem(string pk, string sk, string gpk, int gsk, int lsk) => Item(
        ("pk", S(pk)),
        ("sk", S(sk)),
        ("gpk", S(gpk)),
        ("gsk", N(gsk.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        ("lsk", N(lsk.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    /// <summary><c>read()</c> once it equals <paramref name="want"/>, or its last answer when the budget runs out.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> Converged(
        Func<Task<List<Dictionary<string, AttributeValue>>>> read, Dictionary<string, AttributeValue>[] want, double budgetS = 60.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var got = await read();
            if (Py.Equal(got, want) || clock.Elapsed.TotalSeconds > budgetS)
            {
                return got;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
