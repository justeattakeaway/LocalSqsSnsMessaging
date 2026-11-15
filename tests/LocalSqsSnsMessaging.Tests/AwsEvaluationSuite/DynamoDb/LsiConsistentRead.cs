// Translated from behavioral/dynamodb/check_lsi_consistent_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-consistent-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiConsistentRead
{
    private const string CheckId = "lsi-consistent-read";

    /// <summary>LSI supports ConsistentRead=True (unlike GSI).</summary>
    [Test]
    [DisplayName("dynamodb.lsi-consistent-read")]
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
                new AttributeDefinition("ts", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "ts-index",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("ts", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var item = Item(("pk", S("lsi-cr")), ("sk", S("x")), ("ts", S("100")));
        await ddb.PutItemAsync(table, item);
        // No sleep and no retry: a query against an LSI with ConsistentRead=True must see the write it
        // immediately followed, and must not be rejected the way the identical flag is on a GSI.
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "ts-index",
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("lsi-cr"))),
            ConsistentRead = true,
        });
        Ensure(
            Py.Equal(resp.Items, new[] { item }),
            $"ConsistentRead=True query against the LSI immediately after the write it followed returned {Py.Repr(resp.Items)}, expected the single item just written back verbatim {Py.Repr(new[] { item })}. A target that rejects ConsistentRead on an LSI (as it correctly does for a GSI), or that returns it stale or empty, is not honoring LSI strong consistency.");
    }
}
