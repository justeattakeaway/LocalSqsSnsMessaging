// Translated from behavioral/dynamodb/check_update_item_set_decimal_arithmetic_updated_old_new.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-decimal-arithmetic-updated-old-new</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetDecimalArithmeticUpdatedOldNew
{
    private const string Check = "update-item-set-decimal-arithmetic-updated-old-new";

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
    /// SET path = path + :decimal returns the exact decimal sum as UPDATED_NEW, and a later
    /// SET path = path - :decimal returns the pre-image as UPDATED_OLD while storing the exact
    /// decimal difference.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-decimal-arithmetic-updated-old-new")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-decset-");
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

        var key = Item(("pk", S("dec-set-arith")));
        await ddb.PutItemAsync(table, Item(("pk", S("dec-set-arith")), ("bal", N("12.53"))));

        // SET bal = bal + :inc with a fractional decimal operand: UPDATED_NEW carries the exact sum.
        var new1 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET bal = bal + :inc",
            ExpressionAttributeValues = Item((":inc", N("3.33"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(new1, Item(("bal", N("15.86")))),
            $"SET bal = bal + :inc with bal=12.53, :inc=3.33 gave UPDATED_NEW {Py.Repr(new1)}, " +
            "expected {'bal': {'N': '15.86'}}");
        var stored1 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(stored1, Item(("pk", S("dec-set-arith")), ("bal", N("15.86")))),
            $"stored item after the SET addition is {Py.Repr(stored1)}, expected bal=15.86");

        // SET bal = bal - :dec with a fractional decimal operand: UPDATED_OLD is the pre-image.
        var old2 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET bal = bal - :dec",
            ExpressionAttributeValues = Item((":dec", N("1.08"))),
            ReturnValues = ReturnValue.UPDATED_OLD,
        })).Attributes;
        Ensure(
            Py.Equal(old2, Item(("bal", N("15.86")))),
            $"SET bal = bal - :dec with bal=15.86, :dec=1.08 gave UPDATED_OLD {Py.Repr(old2)}, " +
            "expected the pre-image {'bal': {'N': '15.86'}}");
        var stored2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(stored2, Item(("pk", S("dec-set-arith")), ("bal", N("14.78")))),
            $"stored item after the SET subtraction is {Py.Repr(stored2)}, expected bal=14.78 (15.86 - 1.08)");
    }
}
