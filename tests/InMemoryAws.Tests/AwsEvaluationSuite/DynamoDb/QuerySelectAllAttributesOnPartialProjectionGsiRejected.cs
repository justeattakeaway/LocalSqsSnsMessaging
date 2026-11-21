// Translated from behavioral/dynamodb/check_query_select_all_attributes_on_partial_projection_gsi_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-select-all-attributes-on-partial-projection-gsi-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QuerySelectAllAttributesOnPartialProjectionGsiRejected
{
    private const string KeysOnly = "keys-only-index";
    private const string Include = "include-index";

    private static string NotAll(string index) =>
        "One or more parameter values were invalid: Select type ALL_ATTRIBUTES is not supported for " +
        $"global secondary index {index} because its projection type is not ALL";

    /// <summary>
    /// Query with Select=ALL_ATTRIBUTES on a KEYS_ONLY or INCLUDE global secondary index is
    /// refused -- a GSI never fetches from the base table -- while ALL_PROJECTED_ATTRIBUTES on
    /// each index and ALL_ATTRIBUTES on the table itself are served.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-select-all-attributes-on-partial-projection-gsi-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-querysel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = KeysOnly,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
                new GlobalSecondaryIndex
                {
                    IndexName = Include,
                    KeySchema = [new KeySchemaElement("gk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["inc"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, ctx, table);

        var item = Item(("pk", S("P")), ("sk", S("S")), ("gk", S("G")), ("inc", S("i")), ("body", S("b")));
        await ddb.PutItemAsync(table, item);
        var keys = Item(("pk", item["pk"]), ("sk", item["sk"]), ("gk", item["gk"]));
        var projected = new List<(string Index, Dictionary<string, AttributeValue> Want)>
        {
            (KeysOnly, keys),
            (Include, new Dictionary<string, AttributeValue>(keys, StringComparer.Ordinal) { ["inc"] = item["inc"] }),
        };

        foreach (var index in new[] { KeysOnly, Include })
        {
            var expected = NotAll(index);
            var (code, msg) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = index,
                Select = Select.ALL_ATTRIBUTES,
                KeyConditionExpression = "gk = :g",
                ExpressionAttributeValues = Item((":g", S("G"))),
            }));
            Ensure(
                (code, msg) == ("ValidationException", expected),
                $"Query IndexName={Py.Repr(index)} Select=ALL_ATTRIBUTES gave {Py.Repr(code)}: {Py.Repr(msg)}");
        }

        // Index reads are eventually consistent: poll until each index serves its projection.
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 60.0);
        foreach (var (index, want) in projected)
        {
            List<Dictionary<string, AttributeValue>> got;
            while (true)
            {
                got = (await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = index,
                    Select = Select.ALL_PROJECTED_ATTRIBUTES,
                    KeyConditionExpression = "gk = :g",
                    ExpressionAttributeValues = Item((":g", S("G"))),
                })).Items;
                if (got.Count > 0)
                {
                    break;
                }

                Ensure(clock.Elapsed.TotalSeconds < deadline, $"Query of {index} stayed empty: {Py.Repr(got)}");
                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }

            Ensure(Py.Equal(got, new[] { want }), $"Query {index} ALL_PROJECTED_ATTRIBUTES returned {Py.Repr(got)}, expected [{Py.Repr(want)}]");
        }

        var full = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            Select = Select.ALL_ATTRIBUTES,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("P"))),
        })).Items;
        Ensure(Py.Equal(full, new[] { item }), $"Query of the table with Select=ALL_ATTRIBUTES returned {Py.Repr(full)}");
    }

    /// <summary>Poll DescribeTable every 2 s until the table and its GSIs are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
