// Translated from behavioral/dynamodb/check_partiql_select_and_query_agree_on_one_partition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-and-query-agree-on-one-partition</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectAndQueryAgreeOnOnePartition
{
    private const string CheckId = "partiql-select-and-query-agree-on-one-partition";
    private const int InPartition = 6;
    private const int OtherPartition = 4;

    /// <summary>
    /// A PartiQL SELECT on one partition key returns the same items, in the same order, and the same
    /// ConsumedCapacity as the equivalent Query, so it was planned as a key lookup and not a scan.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-and-query-agree-on-one-partition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlplan-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        for (var i = 0; i < InPartition; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("TARGET")), ("sk", N(i.ToString(CultureInfo.InvariantCulture))), ("body", S($"target-{i}"))));
        }

        // Items outside the target partition make a plan that ignores the predicate cost more.
        for (var i = 0; i < OtherPartition; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("OTHER")), ("sk", N(i.ToString(CultureInfo.InvariantCulture))), ("body", S($"other-{i}"))));
        }

        var queried = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("TARGET"))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var selected = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE pk = ?",
            Parameters = [S("TARGET")],
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });

        Ensure(
            queried.Items.Count == InPartition,
            $"the Query control must return the {InPartition} items in the target partition and nothing from the other one, got {queried.Items.Count}: {Py.Repr(queried.Items)}");
        Ensure(
            Py.Equal(selected.Items, queried.Items),
            $"a PartiQL SELECT on one partition key must return the identical items in the identical sort-key order as the equivalent Query.\nQuery:  {Py.Repr(queried.Items)}\nSELECT: {Py.Repr(selected.Items)}");

        var queryCost = queried.ConsumedCapacity.CapacityUnits;
        var selectCost = selected.ConsumedCapacity.CapacityUnits;
        Ensure(
            selectCost == queryCost,
            $"a partition-key predicate must be planned as a key lookup, so the SELECT costs what the Query costs: Query read {Py.Repr(queryCost)} capacity units, SELECT read {Py.Repr(selectCost)}. A larger figure means the whole table was read and filtered");
    }
}
