// Translated from behavioral/dynamodb/check_transact_get_items_reports_capacity_per_table_as_a_list.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-reports-capacity-per-table-as-a-list</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsReportsCapacityPerTableAsAList
{
    /// <summary>
    /// A transactional read reports ConsumedCapacity as a LIST with one entry per table, unlike the
    /// single object GetItem returns -- so a caller cannot read the two the same way.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-reports-capacity-per-table-as-a-list")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("a"))));
        var tx = (await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("a"))) } }],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        var plain = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        // The .NET SDK's types make the shapes explicit: a list there, a single object here.
        Ensure(tx is not null, "a transactional read reports a list of per-table entries, got NoneType");
        Ensure(tx[0].TableName == table, $"the entry must name the table, got {Capacity(tx[0])}");
        Ensure(plain is not null, "GetItem reports a single object, got NoneType");
    }

    private static string Capacity(ConsumedCapacity c) =>
        Py.Repr(new Dictionary<string, object?> { ["TableName"] = c.TableName, ["CapacityUnits"] = c.CapacityUnits, ["ReadCapacityUnits"] = c.ReadCapacityUnits });
}
