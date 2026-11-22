// Translated from behavioral/dynamodb/check_update_item_set_arithmetic_result_over_38_significant_digits_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-arithmetic-result-over-38-significant-digits-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetArithmeticResultOver38SignificantDigitsRejected
{
    private static readonly string Max38Nines = new('9', 38);
    private static readonly string Carried1Sig = "1" + new string('0', 38);
    private const string OverflowMsg = "Number overflow. Attempting to store a number with magnitude larger than supported range";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until it is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"expected the call to be refused, but it succeeded: {result}") : (err.Code, err.Message);
    }

    /// <summary>
    /// UpdateItem SET arithmetic that carries a 38-nines N to a result spanning 39 significant
    /// digits is ValidationException; the item keeps its pre-update value.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-arithmetic-result-over-38-significant-digits-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-sigdig-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("oversig")));
        var item = Item(("pk", S("oversig")), ("v", N(Max38Nines)));
        await ddb.PutItemAsync(table, item);

        var (code, msg) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = v + :two",
            ExpressionAttributeValues = Item((":two", N("2"))),
        }));
        Ensure(
            (code, msg) == ("ValidationException", OverflowMsg),
            $"38-nines + 2 carries to a 39-significant-digit N; UpdateItem gave {Py.Repr(code)}: {Py.Repr(msg)}");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, item), $"the refused SET left the item as {Py.Repr(got)}, expected it unchanged");

        var control = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = v + :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(control.Attributes, Item(("v", N(Carried1Sig)))),
            "38-nines + 1 is the neighbouring one-significant-digit carry and must be accepted, " +
            $"got {Py.Repr(control.Attributes)}");
    }
}
