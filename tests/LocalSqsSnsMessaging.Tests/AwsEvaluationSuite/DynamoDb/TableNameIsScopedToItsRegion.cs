// Translated from behavioral/dynamodb/check_table_name_is_scoped_to_its_region.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-name-is-scoped-to-its-region</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableNameIsScopedToItsRegion
{
    private const string CheckId = "table-name-is-scoped-to-its-region";
    private const string ErrNotFound = "ResourceNotFoundException";

    /// <summary>
    /// A region distinct from the primary. Which one does not matter -- only that it differs, so the
    /// check cannot pass on an implementation that ignores the region entirely.
    /// </summary>
    private static string SecondRegion(string primary) => primary != "us-west-2" ? "us-west-2" : "us-east-1";

    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>
    /// Cleanup that tolerates the table already being gone -- this check deletes one of the two
    /// tables as part of its assertions, so its cleanup must be idempotent.
    /// </summary>
    private static async Task Drop(IAmazonDynamoDB client, string table)
    {
        var error = await FidelityContext.CaptureError(() => client.DeleteTableAsync(table));
        if (error is not null && error.Code != ErrNotFound)
        {
            throw error.Exception;
        }
    }

    /// <summary>ListTables pages at 100 names, and a shared account can hold more than that.</summary>
    private static async Task<List<string>> AllTableNames(IAmazonDynamoDB client)
    {
        var names = new List<string>();
        var request = new ListTablesRequest();
        while (true)
        {
            var page = await client.ListTablesAsync(request);
            names.AddRange(page.TableNames ?? []);
            var last = page.LastEvaluatedTableName;
            if (string.IsNullOrEmpty(last))
            {
                return names;
            }

            request = new ListTablesRequest { ExclusiveStartTableName = last };
        }
    }

    /// <summary>
    /// A table name is scoped to its region: before it exists in the second region DescribeTable there raises
    /// ResourceNotFoundException with the exact message, the two same-named tables keep separate items and listings,
    /// and deleting one leaves the other and its item intact.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.table-name-is-scoped-to-its-region")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var primary = ctx.Region;
        var secondary = SecondRegion(primary);
        var ddbA = ctx.Dynamo;
        var ddbB = ctx.DynamoClient(region: secondary);
        var table = FidelityContext.UniqueName("sim2real-behav-region-");

        async Task Create(IAmazonDynamoDB client)
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            });
            await client.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        }

        // --- before the second region has the name, it does not resolve there ---
        await Create(ddbA);
        ctx.OnCleanup(() => Drop(ddbA, table));
        var (code, message) = await Code(() => ddbB.DescribeTableAsync(table));
        Ensure(
            code == ErrNotFound,
            $"a table created in {Py.Repr(primary)} must not resolve in {Py.Repr(secondary)}: DescribeTable there must fail with {ErrNotFound}, got {code}");
        Ensure(
            message == $"Requested resource not found: Table: {table} not found",
            $"expected AWS's exact ResourceNotFoundException message, got {Py.Repr(message)}");

        await Create(ddbB);
        ctx.OnCleanup(() => Drop(ddbB, table));

        // --- each region lists only its own table, and both are ACTIVE independently ---
        foreach (var (client, region) in new[] { (ddbA, primary), (ddbB, secondary) })
        {
            var described = (await client.DescribeTableAsync(table)).Table;
            Ensure(
                described.TableStatus == TableStatus.ACTIVE,
                $"the table in {Py.Repr(region)} must be ACTIVE in its own right, got {Py.Repr(described.TableStatus?.Value)}");
            Ensure(
                described.TableArn.Contains(region, StringComparison.Ordinal),
                $"the ARN of the table in {Py.Repr(region)} must name that region, got {Py.Repr(described.TableArn)}");
        }

        // --- items written in one region are invisible in the other ---
        await ddbA.PutItemAsync(table, Item(("pk", S("shared-key")), ("home", S(primary))));
        await ddbB.PutItemAsync(table, Item(("pk", S("shared-key")), ("home", S(secondary))));
        foreach (var (client, region) in new[] { (ddbA, primary), (ddbB, secondary) })
        {
            var got = (await client.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shared-key"))), ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(got, Item(("pk", S("shared-key")), ("home", S(region)))),
                $"the same key in {Py.Repr(region)} must hold that region's own item, not the other region's: got {Py.Repr(got)}");
        }

        // --- an item written in only one region does not appear in the other ---
        await ddbA.PutItemAsync(table, Item(("pk", S("primary-only"))));
        var absent = await ddbB.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("primary-only"))), ConsistentRead = true });
        Ensure(
            absent.Item is null,
            $"an item written only in {Py.Repr(primary)} must be absent in {Py.Repr(secondary)}, found {Py.Repr(absent.Item)}");

        // --- deleting one region's table leaves the other's table and data intact ---
        await ddbB.DeleteTableAsync(table);
        await ddbB.WaitForTableNotExists(table, delaySeconds: 2, maxAttempts: 250);
        var survivor = (await ddbA.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shared-key"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(survivor, Item(("pk", S("shared-key")), ("home", S(primary)))),
            $"deleting the table in {Py.Repr(secondary)} must not touch the table in {Py.Repr(primary)} or its items, got {Py.Repr(survivor)}");
        Ensure(
            (await AllTableNames(ddbA)).Contains(table),
            $"the table in {Py.Repr(primary)} must still be listed after the {Py.Repr(secondary)} table was deleted");
        Ensure(
            !(await AllTableNames(ddbB)).Contains(table),
            $"the deleted table must no longer be listed in {Py.Repr(secondary)}");
    }
}
