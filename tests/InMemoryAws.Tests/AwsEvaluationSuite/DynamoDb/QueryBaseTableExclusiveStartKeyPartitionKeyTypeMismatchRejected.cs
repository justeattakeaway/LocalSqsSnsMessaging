// Translated from behavioral/dynamodb/check_query_base_table_exclusive_start_key_partition_key_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-base-table-exclusive-start-key-partition-key-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryBaseTableExclusiveStartKeyPartitionKeyTypeMismatchRejected
{
    private static readonly (string Code, string Message) BadStartKey = ("ValidationException", "The provided starting key is invalid");

    /// <summary>
    /// A base-table Query refuses an ExclusiveStartKey whose partition key type differs from the
    /// schema with ValidationException, and the same Query continued from a real LastEvaluatedKey
    /// returns exactly the remaining items in order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-base-table-exclusive-start-key-partition-key-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        for (var i = 0; i < 3; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p0")), ("sk", S($"s{i}"))));
        }

        var first = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p0"))),
            Limit = 1,
            ConsistentRead = true,
        });
        var firstSks = first.Items.Select(i => i["sk"].S).ToList();
        Ensure(firstSks.SequenceEqual(["s0"]), $"expected the Limit=1 page to hold exactly ['s0'], got {Py.Repr(firstSks)}");
        var realKey = first.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        Ensure(realKey is not null, "expected a LastEvaluatedKey from the Limit=1 page over 3 items");

        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = Item((":p", S("p0"))),
                ExclusiveStartKey = Item(("pk", N("1")), ("sk", S("s0"))),
            }),
            BadStartKey.Code,
            BadStartKey.Message);

        var continued = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p0"))),
            ExclusiveStartKey = realKey,
            ConsistentRead = true,
        });
        var continuedSks = continued.Items.Select(i => i["sk"].S).ToList();
        Ensure(
            continuedSks.SequenceEqual(["s1", "s2"]),
            $"expected the page continued from the real LastEvaluatedKey to hold exactly ['s1', 's2'], got {Py.Repr(continuedSks)}");
        Ensure(
            continued.LastEvaluatedKey is not { Count: > 0 },
            $"expected the continued page to exhaust the 3-item partition, got LastEvaluatedKey {Py.Repr(continued.LastEvaluatedKey)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        Ensure(error.Code == code, $"expected error code {Py.Repr(code)}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == message, $"expected error message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
    }
}
