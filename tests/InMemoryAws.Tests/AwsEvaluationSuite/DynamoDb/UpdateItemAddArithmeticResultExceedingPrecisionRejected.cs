// Translated from behavioral/dynamodb/check_update_item_add_arithmetic_result_exceeding_precision_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-arithmetic-result-exceeding-precision-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddArithmeticResultExceedingPrecisionRejected
{
    private const string Check = "update-item-add-arithmetic-result-exceeding-precision-rejected";
    private static readonly string BaseNines38 = new('9', 38); // 10**38 - 1, the largest 38-significant-digit integer
    private const string OverflowMsg = "Number overflow. Attempting to store a number with magnitude larger than supported range";
    private static readonly (string Code, string Message) OverflowErr = ("ValidationException", OverflowMsg);

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
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

    /// <summary>Raise if fn() succeeds; otherwise assert its exact (code, message).</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            Ensure(
                (err.Code, err.Message) == expected,
                $"expected ({Py.Repr(expected.Code)}, {Py.Repr(expected.Message)}), got ({Py.Repr(err.Code)}, {Py.Repr(err.Message)})");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>
    /// ADD whose arithmetic result would need 39 significant digits is the same ValidationException
    /// as a magnitude overflow, even far below the magnitude ceiling, and leaves the item unchanged;
    /// the same ADD held within the 38-digit limit succeeds.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-arithmetic-result-exceeding-precision-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-decaddprec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        // The arithmetic result needs 39 significant digits (none of them trimmable: it starts
        // with a nonzero digit and ends with one). ADD refuses it and leaves the item unchanged.
        var key = Item(("pk", S("dec-add-overprecision")));
        var item = Item(("pk", S("dec-add-overprecision")), ("bal", N(BaseNines38)));
        await ddb.PutItemAsync(table, item);
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD bal :inc",
                ExpressionAttributeValues = Item((":inc", N("9"))),
            }),
            OverflowErr);
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, item), $"refused ADD bal :inc changed the item to {Py.Repr(got)}");

        // Positive control: the same base ADD-ed by 1 lands at exactly 38 significant digits and
        // is accepted.
        var r = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD bal :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        var want = Item(("pk", S("dec-add-overprecision")), ("bal", N("1" + new string('0', 38))));
        Ensure(Py.Equal(r, want), $"ADD bal :one with bal={Py.Repr(BaseNines38)} returned {Py.Repr(r)}, expected {Py.Repr(want)}");
    }
}
