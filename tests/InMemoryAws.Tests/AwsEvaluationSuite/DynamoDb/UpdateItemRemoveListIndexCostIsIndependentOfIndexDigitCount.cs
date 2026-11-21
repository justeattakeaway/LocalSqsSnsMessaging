// Translated from behavioral/dynamodb/check_update_item_remove_list_index_cost_is_independent_of_index_digit_count.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-remove-list-index-cost-is-independent-of-index-digit-count</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemRemoveListIndexCostIsIndependentOfIndexDigitCount
{
    private const string Check = "update-item-remove-list-index-cost-is-independent-of-index-digit-count";
    private const int ListLen = 100;

    /// <summary>A cleanup running undo() where a failed delete raises; the resource's own not-found code means it is already gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        var table = FidelityContext.UniqueName(name);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        return table;
    }

    /// <summary>
    /// Reset a fixed 100-element list plus an f filler, REMOVE l[index], and report whether
    /// that write cost 1.0 CapacityUnits (as opposed to 2.0).
    /// </summary>
    private static async Task<bool> RemoveCosts1Wcu(IAmazonDynamoDB ddb, string table, int index, int fillerLen)
    {
        await ddb.PutItemAsync(table, Item(
            ("pk", S("p")),
            ("f", S(new string('x', fillerLen))),
            ("l", L([.. Enumerable.Range(0, ListLen).Select(_ => S("a"))]))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("p"))),
            UpdateExpression = $"REMOVE l[{index}]",
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var cap = resp.ConsumedCapacity.CapacityUnits;
        Ensure(cap is 1.0 or 2.0, $"REMOVE l[{Py.Repr(index)}] at filler {Py.Repr(fillerLen)} cost {Py.Repr(cap)} CapacityUnits");
        return cap == 1.0;
    }

    /// <summary>
    /// UpdateItem's ConsumedCapacity for REMOVE l[i] is the same whether the index is a single
    /// digit or two digits: both removals leave an identical resulting list, and both cost the same
    /// CapacityUnits right up to the shared byte-exact boundary.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-remove-list-index-cost-is-independent-of-index-digit-count")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableOneDigit = await CreateTable(ctx, ddb, "sim2real-behav-rmix-one-");
        var tableTwoDigit = await CreateTable(ctx, ddb, "sim2real-behav-rmix-two-");

        // Index 0 is one digit; index 99 is two digits. Each table keeps its own partition history so
        // neither search's write volume can bias the other's measured boundary.
        var maxOneDigit = await UpdateSizeBisection.Search(f => RemoveCosts1Wcu(ddb, tableOneDigit, 0, f), 0, 2000);
        var maxTwoDigit = await UpdateSizeBisection.Search(f => RemoveCosts1Wcu(ddb, tableTwoDigit, 99, f), 0, 2000);

        Ensure(
            maxTwoDigit == maxOneDigit,
            $"max filler at 1.0 CapacityUnits was {Py.Repr(maxOneDigit)} for a one-digit index and " +
            $"{Py.Repr(maxTwoDigit)} for a two-digit index; expected the same boundary for both");

        // One byte past the shared boundary, both must now cost 2.0.
        Ensure(
            !await RemoveCosts1Wcu(ddb, tableOneDigit, 0, maxOneDigit + 1),
            "the one-digit index did not cost 2.0 CapacityUnits one byte past the measured boundary");
        Ensure(
            !await RemoveCosts1Wcu(ddb, tableTwoDigit, 99, maxTwoDigit + 1),
            "the two-digit index did not cost 2.0 CapacityUnits one byte past the measured boundary");

        // Positive control: both removals actually ran, each leaving 99 identical elements behind.
        foreach (var table in new[] { tableOneDigit, tableTwoDigit })
        {
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p"))), ConsistentRead = true })).Item;
            var elements = got["l"].L;
            Ensure(
                Py.Equal(elements, Enumerable.Range(0, ListLen - 1).Select(_ => S("a")).ToList()),
                $"table {Py.Repr(table)} list after REMOVE has {Py.Repr(elements)}, expected 99 copies of {{'S': 'a'}}");
        }
    }
}
