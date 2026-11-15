// Translated from behavioral/dynamodb/check_batch_write_item_mixed_put_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-mixed-put-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemMixedPutDelete
{
    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = "sim2real:managed", Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
    ];

    /// <summary>A single BatchWriteItem call can Put a new item and Delete an existing item across requests atomically per-entry.</summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-mixed-put-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = await CreateSimpleTable(ddb, ctx);
        Dictionary<string, AttributeValue> KeepKey() => Item(("pk", S("batch-keep")), ("sk", S("a")));
        Dictionary<string, AttributeValue> DelKey() => Item(("pk", S("batch-del")), ("sk", S("a")));
        await ddb.PutItemAsync(tableName, DelKey());

        var keepItem = KeepKey();
        keepItem["v"] = S("new");
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [tableName] =
                [
                    new WriteRequest { PutRequest = new PutRequest { Item = keepItem } },
                    new WriteRequest { DeleteRequest = new DeleteRequest { Key = DelKey() } },
                ],
            },
        });
        var unprocessed = resp.UnprocessedItems ?? [];
        Ensure(unprocessed.Count == 0, $"expected no unprocessed items, got {Py.Repr(unprocessed.Keys)}");

        var gotKeep = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = KeepKey(), ConsistentRead = true });
        Ensure(Py.Equal(gotKeep.Item["v"], S("new")), $"expected PutRequest item v='new', got {Py.Repr(gotKeep.Item)}");
        var gotDel = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = DelKey(), ConsistentRead = true });
        Ensure(gotDel.Item is null, $"expected DeleteRequest item to be gone, still present: {Py.Repr(gotDel.Item)}");
    }

    /// <summary>
    /// Poll DescribeTable until ACTIVE. CreateTable is eventually consistent, so a
    /// transient ResourceNotFoundException counts as "not yet", never a failure.
    /// </summary>
    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 90.0, double intervalS = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            try
            {
                status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                status = "PENDING";
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS.ToString("0.0", CultureInfo.InvariantCulture)}s (last={status})");
    }

    /// <summary>Create a pk (S) + sk (S) composite-key table, register teardown, wait ACTIVE.</summary>
    private static async Task<string> CreateSimpleTable(IAmazonDynamoDB ddb, FidelityContext ctx)
    {
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);
        return tableName;
    }
}
