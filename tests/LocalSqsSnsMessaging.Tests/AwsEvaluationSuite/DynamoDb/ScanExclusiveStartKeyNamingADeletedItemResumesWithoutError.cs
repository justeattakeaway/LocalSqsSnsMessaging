// Translated from behavioral/dynamodb/check_scan_exclusive_start_key_naming_a_deleted_item_resumes_without_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-exclusive-start-key-naming-a-deleted-item-resumes-without-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanExclusiveStartKeyNamingADeletedItemResumesWithoutError
{
    private const string Check = "scan-exclusive-start-key-naming-a-deleted-item-resumes-without-error";
    private const int NItems = 16;
    private const int Page = 5;

    private static SortedSet<string> Set(IEnumerable<string> values) => new(values, StringComparer.Ordinal);

    /// <summary>
    /// Scan resumes from an ExclusiveStartKey naming an item deleted since it was issued.
    /// The call succeeds, the filter is applied to items found after the key's position,
    /// and exactly the items not yet seen are returned.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-exclusive-start-key-naming-a-deleted-item-resumes-without-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await CreateTable(ctx, ddb, table);
        var allPks = Set(Enumerable.Range(0, NItems).Select(i => $"item-{i:D3}"));
        foreach (var pk in allPks)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("category", S("keep"))));
        }

        var page1 = await ddb.ScanAsync(new ScanRequest { TableName = table, Limit = Page, ConsistentRead = true });
        Ensure(page1.Items.Count == Page, $"first page returned {page1.Items.Count} item(s), expected {Page}");
        Ensure(page1.LastEvaluatedKey is not null, "expected a LastEvaluatedKey after the first page");
        var lek = page1.LastEvaluatedKey;
        var page1Pks = Set(page1.Items.Select(item => item["pk"].S));
        var deletedPk = lek["pk"].S;
        Ensure(
            page1Pks.Contains(deletedPk),
            $"LastEvaluatedKey names {Py.Repr(deletedPk)}, not one of the first page's items {Py.Repr(page1Pks)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(deletedPk))),
            UpdateExpression = "SET category = :drop",
            ExpressionAttributeValues = Item((":drop", S("drop"))),
        });
        var changed = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(deletedPk))), ConsistentRead = true });
        Ensure(
            changed.Item["category"].S == "drop",
            $"expected {Py.Repr(deletedPk)}'s category to read back as 'drop', got {Py.Repr(changed.Item["category"])}");
        await ddb.DeleteItemAsync(table, Item(("pk", S(deletedPk))));
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(deletedPk))), ConsistentRead = true });
        Ensure(gone.Item is null, $"expected {Py.Repr(deletedPk)} absent after DeleteItem, got {Py.Repr(gone.Item)}");
        var resumedPks = Set([]);
        var request = new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            ExclusiveStartKey = lek,
            FilterExpression = "category = :want",
            ExpressionAttributeValues = Item((":want", S("keep"))),
        };
        while (true)
        {
            var resp = await ddb.ScanAsync(request);
            foreach (var item in resp.Items)
            {
                Ensure(
                    item["category"].S == "keep",
                    $"filtered Scan returned {Py.Repr(item["pk"].S)} with category {Py.Repr(item["category"].S)}, expected only 'keep'");
            }

            var pagePks = Set(resp.Items.Select(item => item["pk"].S));
            Ensure(
                !pagePks.Overlaps(page1Pks),
                $"resumed Scan returned {Py.Repr(pagePks.Intersect(page1Pks).ToList())}, already seen on the first page");
            resumedPks.UnionWith(pagePks);
            if (resp.LastEvaluatedKey is null)
            {
                break;
            }

            request.ExclusiveStartKey = resp.LastEvaluatedKey;
        }

        var expectedResumed = Set(allPks.Except(page1Pks));
        Ensure(
            resumedPks.SetEquals(expectedResumed),
            $"resuming after the deleted item's key returned {Py.Repr(resumedPks)}, expected exactly the items past the first page {Py.Repr(expectedResumed)}");
        var finalPks = Set([]);
        var finalRequest = new ScanRequest { TableName = table, ConsistentRead = true };
        while (true)
        {
            var resp = await ddb.ScanAsync(finalRequest);
            finalPks.UnionWith(resp.Items.Select(item => item["pk"].S));
            if (resp.LastEvaluatedKey is null)
            {
                break;
            }

            finalRequest.ExclusiveStartKey = resp.LastEvaluatedKey;
        }

        Ensure(
            finalPks.SetEquals(allPks.Where(p => p != deletedPk)),
            $"expected the table to hold every item but {Py.Repr(deletedPk)}, got {Py.Repr(finalPks)}");
    }

    private static async Task CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DropTable(ddb, name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
    }

    /// <summary>Teardown: a CREATING table refuses DeleteTable, so wait for it to settle first (bounded).</summary>
    private static async Task DropTable(IAmazonDynamoDB ddb, string name)
    {
        try
        {
            var status = (await ddb.DescribeTableAsync(name)).Table.TableStatus;
            if (status == TableStatus.DELETING)
            {
                return;
            }

            if (status != TableStatus.ACTIVE)
            {
                await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
            }

            await ddb.DeleteTableAsync(name);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
