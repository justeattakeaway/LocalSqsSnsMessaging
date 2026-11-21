// Translated from behavioral/dynamodb/check_update_item_add_decimal_arithmetic_precision.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-decimal-arithmetic-precision</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddDecimalArithmeticPrecision
{
    private const string Check = "update-item-add-decimal-arithmetic-precision";
    private static readonly string Base38 = new string('1', 37) + "0"; // 38 digits, one short of 38 ones
    private static readonly string Target38 = new('1', 38); // the 38-significant-digit ADD result

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

    /// <summary>
    /// ADD on a Number keeps the exact decimal result for a fractional increment and a
    /// fractional decrement, and accepts an arithmetic result at exactly 38 significant digits.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-decimal-arithmetic-precision")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-decadd-");
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

        // ADD with a fractional decimal operand keeps the exact decimal sum.
        var keyFrac = Item(("pk", S("dec-add-frac")));
        Dictionary<string, AttributeValue> Frac(string bal) => Item(("pk", S("dec-add-frac")), ("bal", N(bal)));
        await ddb.PutItemAsync(table, Frac("100.53"));
        var r1 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyFrac,
            UpdateExpression = "ADD bal :inc",
            ExpressionAttributeValues = Item((":inc", N("25.34"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(Py.Equal(r1, Frac("125.87")), $"ADD bal :inc with bal=100.53, :inc=25.34 returned {Py.Repr(r1)}, expected bal=125.87");

        // ADD with a negative fractional decimal operand decrements by the exact decimal amount.
        var r2 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyFrac,
            UpdateExpression = "ADD bal :dec",
            ExpressionAttributeValues = Item((":dec", N("-50.12"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(Py.Equal(r2, Frac("75.75")), $"ADD bal :dec with bal=125.87, :dec=-50.12 returned {Py.Repr(r2)}, expected bal=75.75");
        var gotFrac = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = keyFrac, ConsistentRead = true })).Item;
        Ensure(Py.Equal(gotFrac, Frac("75.75")), $"stored item after both fractional ADDs is {Py.Repr(gotFrac)}, expected bal=75.75");

        // ADD whose arithmetic result lands at exactly 38 significant digits is accepted and
        // rendered with every digit intact.
        var key38 = Item(("pk", S("dec-add-38digits")));
        Dictionary<string, AttributeValue> Of38(string bal) => Item(("pk", S("dec-add-38digits")), ("bal", N(bal)));
        await ddb.PutItemAsync(table, Of38(Base38));
        var r3 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key38,
            UpdateExpression = "ADD bal :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(r3, Of38(Target38)),
            $"ADD bal :one with bal={Py.Repr(Base38)} returned {Py.Repr(r3)}, expected bal={Py.Repr(Target38)} " +
            $"({Target38.Length} significant digits)");
        var got38 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key38, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got38, Of38(Target38)), $"stored item after the 38-significant-digit ADD is {Py.Repr(got38)}");
    }
}
