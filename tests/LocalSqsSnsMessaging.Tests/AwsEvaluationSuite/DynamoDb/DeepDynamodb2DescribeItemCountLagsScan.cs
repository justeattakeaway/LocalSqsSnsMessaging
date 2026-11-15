// Translated from behavioral/dynamodb/check_deep_dynamodb_2_describe_item_count_lags_scan.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-2-describe-item-count-lags-scan</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb2DescribeItemCountLagsScan
{
    private const string Check = "deep-dynamodb-2-describe-item-count-lags-scan";
    private const int NItems = 25;

    private static async Task<int> ScanCount(IAmazonDynamoDB ddb, string table)
    {
        var total = 0;
        var request = new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true };
        while (true)
        {
            var page = await ddb.ScanAsync(request);
            total += page.Count ?? 0;
            var lek = page.LastEvaluatedKey;
            if (lek is null || lek.Count == 0)
            {
                return total;
            }

            request.ExclusiveStartKey = lek;
        }
    }

    /// <summary>
    /// DescribeTable.ItemCount is a periodic estimate; a consistent Scan counts all 25 writes.
    ///
    /// AWS updates ItemCount roughly every six hours, so 0 and 25 are both accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deep-dynamodb-2-describe-item-count-lags-scan")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);

        for (var i = 0; i < NItems; i++)
        {
            var key = Item(("pk", S("item-" + i.ToString("00", CultureInfo.InvariantCulture))));
            var v = i.ToString(CultureInfo.InvariantCulture);
            if (i % 2 != 0)
            {
                await ddb.PutItemAsync(t, Item(("pk", key["pk"]), ("v", N(v))));
            }
            else
            {
                await ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = t,
                    Key = key,
                    UpdateExpression = "SET v = :v",
                    ExpressionAttributeValues = Item((":v", N(v))),
                });
            }
        }

        var counted = await ScanCount(ddb, t);
        Ensure(counted == NItems, $"consistent Scan counted {Py.Repr(counted)} items; expected {NItems}");

        var desc = (await ddb.DescribeTableAsync(t)).Table;
        Ensure(desc.TableStatus == TableStatus.ACTIVE, $"table status {Py.Repr(desc.TableStatus?.Value)}");
        Ensure(
            desc.ItemCount == 0 || desc.ItemCount == NItems,
            $"DescribeTable.ItemCount was {Py.Repr(desc.ItemCount)} seconds after {NItems} writes; real " +
            $"DynamoDB still reports the periodic estimate 0 here, a live count is {NItems}");
        Ensure(
            desc.TableSizeBytes == 0 || (desc.ItemCount == NItems && desc.TableSizeBytes > 0),
            $"DescribeTable.TableSizeBytes was {Py.Repr(desc.TableSizeBytes)} with ItemCount " +
            $"{Py.Repr(desc.ItemCount)}; expected the stale estimate 0 or a live non-zero size");

        // Scan remains the real-time view after the metadata read.
        counted = await ScanCount(ddb, t);
        Ensure(counted == NItems, $"second Scan counted {Py.Repr(counted)}; expected {NItems}");
    }

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("ddbexpr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}
