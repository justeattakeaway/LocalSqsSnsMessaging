// Translated from behavioral/dynamodb/check_query_on_index_consumed_capacity_attributes_the_index.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-on-index-consumed-capacity-attributes-the-index</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryOnIndexConsumedCapacityAttributesTheIndex
{
    /// <summary><c>sorted(cc)</c>: the names of the ConsumedCapacity members present.</summary>
    private static List<string> Members(ConsumedCapacity cc)
    {
        var members = new List<string>();
        if (cc.TableName is not null)
        {
            members.Add("TableName");
        }

        if (cc.CapacityUnits is not null)
        {
            members.Add("CapacityUnits");
        }

        if (cc.ReadCapacityUnits is not null)
        {
            members.Add("ReadCapacityUnits");
        }

        if (cc.WriteCapacityUnits is not null)
        {
            members.Add("WriteCapacityUnits");
        }

        if (cc.Table is not null)
        {
            members.Add("Table");
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            members.Add("LocalSecondaryIndexes");
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            members.Add("GlobalSecondaryIndexes");
        }

        members.Sort(StringComparer.Ordinal);
        return members;
    }

    /// <summary>
    /// ReturnConsumedCapacity=INDEXES on a Query through a GSI attributes the read to the INDEX
    /// arm and charges the table nothing -- the arm breakdown, not just a total.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-on-index-consumed-capacity-attributes-the-index")]
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
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g1")), ("lsk", N("5"))));
        var cc = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            KeyConditionExpression = "gpk = :v",
            ExpressionAttributeValues = Item((":v", S("g1"))),
        })).ConsumedCapacity;
        Ensure(cc.GlobalSecondaryIndexes is not null, $"expected a GlobalSecondaryIndexes arm, got {Py.Repr(Members(cc))}");
        Ensure(
            cc.GlobalSecondaryIndexes.ContainsKey("gsi1"),
            $"expected the gsi1 arm, got {Py.Repr(cc.GlobalSecondaryIndexes.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(cc.GlobalSecondaryIndexes["gsi1"].CapacityUnits > 0, "the index arm must be charged");
        Ensure(
            (cc.Table?.CapacityUnits ?? 0) == 0,
            $"a GSI read charges the table nothing, got {(cc.Table is null ? "None" : Py.Repr(new Dictionary<string, object?> { ["CapacityUnits"] = cc.Table.CapacityUnits }))}");
    }
}
