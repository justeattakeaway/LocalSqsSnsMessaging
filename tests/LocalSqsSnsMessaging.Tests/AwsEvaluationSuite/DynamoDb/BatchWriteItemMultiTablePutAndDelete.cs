// Translated from behavioral/dynamodb/check_batch_write_item_multi_table_put_and_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-multi-table-put-and-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemMultiTablePutAndDelete
{
    private const string Check = "batch-write-item-multi-table-put-and-delete";

    /// <summary>
    /// A single BatchWriteItem spanning two tables -- a put into one, a delete from the other --
    /// applies both writes and returns an empty UnprocessedItems.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-multi-table-put-and-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await SimpleTable(ctx, ddb, "sim2real-behav-bwi-a-");
        var tableB = await SimpleTable(ctx, ddb, "sim2real-behav-bwi-b-");

        Dictionary<string, AttributeValue> KeepKey() => Item(("pk", S("keep")));
        Dictionary<string, AttributeValue> DelKey() => Item(("pk", S("del")));
        await ddb.PutItemAsync(tableB, KeepKey());
        await ddb.PutItemAsync(tableB, DelKey());

        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [tableA] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("new")), ("from", S("a"))) } }],
                [tableB] = [new WriteRequest { DeleteRequest = new DeleteRequest { Key = DelKey() } }],
            },
        });
        Ensure(
            (resp.UnprocessedItems ?? []).Count == 0,
            $"a batch within limits must leave nothing unprocessed, got {Py.Repr(resp.UnprocessedItems?.Keys)}");

        var putItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableA, Key = Item(("pk", S("new"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(putItem, Item(("pk", S("new")), ("from", S("a")))), $"the put into table_a did not land, got {Py.Repr(putItem)}");
        var deleted = await ddb.GetItemAsync(new GetItemRequest { TableName = tableB, Key = DelKey(), ConsistentRead = true });
        Ensure(deleted.Item is null, $"the deleted key is still present in table_b: {Py.Repr(deleted.Item)}");
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableB, Key = KeepKey(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(kept, KeepKey()), $"an untouched key in table_b must survive the batch, got {Py.Repr(kept)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 90.0, double intervalS = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS.ToString("0.0", CultureInfo.InvariantCulture)}s (last={status})");
    }

    private static async Task<string> SimpleTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(name), "ResourceNotFoundException"));
        await WaitForTableActive(ddb, name);
        return name;
    }
}
