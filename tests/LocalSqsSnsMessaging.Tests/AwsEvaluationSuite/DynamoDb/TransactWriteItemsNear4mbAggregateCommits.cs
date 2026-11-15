// Translated from behavioral/dynamodb/check_transact_write_items_near_4mb_aggregate_commits.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-near-4mb-aggregate-commits</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsNear4mbAggregateCommits
{
    private const string Check = "transact-write-items-near-4mb-aggregate-commits";
    private const int FillerBytes = 350 * 1024;
    private const int ItemCount = 10;
    private const int FourMb = 4 * 1024 * 1024;

    /// <summary>A cleanup running undo() where a failed delete raises; only not_found means it is gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
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
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary>An item of one short key and one ~350 KB string attribute.</summary>
    private static Dictionary<string, AttributeValue> MakeItem(string pk) =>
        Item(("pk", S(pk)), ("data", S(new string('x', FillerBytes))));

    /// <summary>The DynamoDB size of an all-string item: attribute-name bytes plus value bytes.</summary>
    private static int ItemSize(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>
    /// TransactWriteItems whose aggregate payload sits just under the 4 MB transaction ceiling
    /// commits every item, and each is readable afterward with its exact content.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/transaction-apis.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-near-4mb-aggregate-commits")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-tw4mb-");

        var items = Enumerable.Range(0, ItemCount).Select(i => MakeItem($"big-{i.ToString("D2", CultureInfo.InvariantCulture)}")).ToList();
        var aggregate = items.Sum(ItemSize);
        Ensure(aggregate < FourMb, $"the near-limit items already total {aggregate} bytes, at or over 4 MB");
        Ensure(aggregate > FourMb * 0.75, $"the near-limit items total only {aggregate} bytes, not near 4 MB");

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [.. items.Select(it => new TransactWriteItem { Put = new Put { TableName = table, Item = it } })],
        });

        foreach (var it in items)
        {
            var got = await Get(ddb, table, Item(("pk", it["pk"])));
            Ensure(
                Py.Equal(got, it),
                $"item {Py.Repr(it["pk"])} read back as {Py.Repr(got)}, expected the exact {FillerBytes}-byte item committed in a {aggregate}-byte transaction");
        }
    }
}
