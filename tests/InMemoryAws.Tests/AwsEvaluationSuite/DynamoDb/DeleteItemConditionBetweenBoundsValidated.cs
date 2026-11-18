// Translated from behavioral/dynamodb/check_delete_item_condition_between_bounds_validated.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-condition-between-bounds-validated</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionBetweenBoundsValidated
{
    private static string ReversedMsg(string lo, string hi) =>
        "1 validation error detected: Invalid ConditionExpression: The BETWEEN operator requires upper " +
        "bound to be greater than or equal to lower bound; lower bound operand: AttributeValue: " + lo + ", " +
        "upper bound operand: AttributeValue: " + hi;

    private static string MixedTypeMsg(string lo, string hi) =>
        "1 validation error detected: Invalid ConditionExpression: The BETWEEN operator requires same " +
        "data type for lower and upper bounds; lower bound operand: AttributeValue: " + lo + ", " +
        "upper bound operand: AttributeValue: " + hi;

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0)
    {
        return async () =>
        {
            var clock = FidelityTime.StartNew();
            while (true)
            {
                var error = await FidelityContext.CaptureError(undo);
                if (error is null || notFound.Contains(error.Code))
                {
                    return;
                }

                if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
                {
                    throw error.Exception;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            }
        };
    }

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var limit = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < limit)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A DeleteItem condition BETWEEN with reversed N or S bounds, or bounds of different types, is
    /// ValidationException and the item stays; ordered and equal bounds that hold delete it.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-condition-between-bounds-validated")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-between-del-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        var numItem = Item(("pk", S("num")), ("v", N("50")));
        var strItem = Item(("pk", S("str")), ("v", S("m")));
        await ddb.PutItemAsync(table, numItem);
        await ddb.PutItemAsync(table, strItem);

        Task<DeleteItemResponse> Delete(Dictionary<string, AttributeValue> item, AttributeValue lo, AttributeValue hi) =>
            ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Item(("pk", item["pk"])),
                ConditionExpression = "v BETWEEN :lo AND :hi",
                ExpressionAttributeValues = Item((":lo", lo), (":hi", hi)),
            });

        var cases = new (string Label, Dictionary<string, AttributeValue> Item, AttributeValue Lo, AttributeValue Hi, string Want)[]
        {
            ("reversed N bounds", numItem, N("100"), N("1"), ReversedMsg("{N:100}", "{N:1}")),
            ("reversed S bounds", strItem, S("z"), S("a"), ReversedMsg("{S:z}", "{S:a}")),
            ("N lower and S upper bound", numItem, N("1"), S("100"), MixedTypeMsg("{N:1}", "{S:100}")),
        };
        foreach (var (label, item, lo, hi, want) in cases)
        {
            var (code, msg) = await Error(async () => await Delete(item, lo, hi));
            Ensure(
                (code, msg) == ("ValidationException", want),
                $"DeleteItem with BETWEEN {label} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", item["pk"])), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got, item), $"DeleteItem refused for BETWEEN {label} left {Py.Repr(got)}, expected {Py.Repr(item)}");
        }

        await Delete(numItem, N("50"), N("50"));
        var gotNum = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", numItem["pk"])), ConsistentRead = true })).Item;
        Ensure(gotNum is null, $"DeleteItem under 'v BETWEEN 50 AND 50' (v = 50) left {Py.Repr(gotNum)}");

        await Delete(strItem, S("a"), S("z"));
        var gotStr = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", strItem["pk"])), ConsistentRead = true })).Item;
        Ensure(gotStr is null, $"DeleteItem under \"v BETWEEN 'a' AND 'z'\" (v = 'm') left {Py.Repr(gotStr)}");
    }
}
