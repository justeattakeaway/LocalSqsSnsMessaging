// Translated from behavioral/dynamodb/check_batch_write_item_full_batch_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-full-batch-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemFullBatchAccepted
{
    private const string Check = "batch-write-item-full-batch-accepted";

    /// <summary>
    /// BatchWriteItem accepts a full batch of 25 puts to a table with a local secondary index in one call,
    /// with no UnprocessedItems, and a Query then counts all 25 items.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-full-batch-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsi_sk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsi_sk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await WaitTableActive(ddb, tableName);

        // Bounded, cheap control: a batch of items well under the 10 GB
        // collection limit, all sharing one partition key, must be accepted in
        // full - this is the real, asserted oracle for this check.
        const string sharedPk = "collection-under-limit";
        var putRequests = Enumerable.Range(0, 25).Select(i => new WriteRequest
        {
            PutRequest = new PutRequest
            {
                Item = Item(
                    ("pk", S(sharedPk)),
                    ("sk", S($"sk-{i.ToString("000", CultureInfo.InvariantCulture)}")),
                    ("lsi_sk", S($"lsi-{i.ToString("000", CultureInfo.InvariantCulture)}")),
                    ("payload", S(new string('x', 1024)))),
            },
        }).ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [tableName] = putRequests } });
        Ensure(
            resp.UnprocessedItems is not { Count: > 0 },
            "expected all under-limit writes to be accepted, got unprocessed items: " +
            $"{Py.Repr(resp.UnprocessedItems?.Keys)}");

        var query = await ddb.QueryAsync(new QueryRequest
        {
            TableName = tableName,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S(sharedPk))),
        });
        Ensure(query.Count == 25, $"expected 25 items written under the shared partition key, found {query.Count}");
    }

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
