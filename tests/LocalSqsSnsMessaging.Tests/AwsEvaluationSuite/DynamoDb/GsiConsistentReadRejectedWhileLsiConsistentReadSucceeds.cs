// Translated from behavioral/dynamodb/check_gsi_consistent_read_rejected_while_lsi_consistent_read_succeeds.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiConsistentReadRejectedWhileLsiConsistentReadSucceeds
{
    private const string CheckId = "gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// On one table, a ConsistentRead Query on the LSI returns the item just written, while the same Query on the GSI
    /// raises ValidationException with the exact message and no items; an eventually consistent GSI Query returns it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-consistent-read-rejected-while-lsi-consistent-read-succeeds")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-idxconsist-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsi_sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
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
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        Dictionary<string, string?> indexStatus = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(300))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            indexStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus?.Value, StringComparer.Ordinal);
            if (indexStatus.GetValueOrDefault("gsi1") == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(indexStatus.GetValueOrDefault("gsi1") == "ACTIVE", $"gsi1 never became ACTIVE within 300s: {Py.Repr(indexStatus)}");

        const string pk = "PART#1";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("S1")), ("lsi_sk", S("L1")), ("gsi1pk", S("A"))));

        // Positive control 1: the LSI is strongly consistent, so this read of the write that
        // immediately preceded it must succeed with NO poll loop.
        var lsiItems = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p AND lsi_sk = :l",
            ExpressionAttributeValues = Item((":p", S(pk)), (":l", S("L1"))),
        })).Items;
        Ensure(lsiItems.Select(i => i["pk"].S).SequenceEqual([pk]), $"the strongly consistent LSI read did not see the write it followed: {Py.Repr(lsiItems)}");

        // Break exactly one thing: the same query shape, against the GLOBAL index.
        var (accepted, rejected) = await FidelityContext.Capture(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            ConsistentRead = true,
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S("A"))),
        }));

        Ensure(
            accepted is null,
            $"ConsistentRead=True on a global secondary index must be rejected, but the service accepted it and returned {Py.Repr(accepted?.Items)}");
        var code = rejected!.Code;
        var message = rejected.Message;
        Ensure(code == ErrValidation, $"expected {Py.Repr(ErrValidation)} for ConsistentRead on a GSI, got {Py.Repr(code)} ({Py.Repr(rejected.Message)})");
        Ensure(
            message == "Consistent reads are not supported on global secondary indexes",
            $"expected the exact ValidationException message AWS returns, got {Py.Repr(message)}");
        // Effect absent: the rejected query carried no data whatsoever -- not an empty page, no page
        // (the .NET SDK raises an exception with no response object).

        // Positive control 2: the GSI itself works when asked for an eventually consistent read, so
        // the rejection above is about the consistency flag and not about an index that does not exist.
        clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> gsiItems = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            gsiItems = (await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "gsi1",
                KeyConditionExpression = "gsi1pk = :g",
                ExpressionAttributeValues = Item((":g", S("A"))),
            })).Items;
            if (gsiItems.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            gsiItems.Select(i => i["pk"].S).SequenceEqual([pk]),
            $"the GSI never became queryable at all within 120s, so the rejection is not about consistency: {Py.Repr(gsiItems)}");
    }
}
