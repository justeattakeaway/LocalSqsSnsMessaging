// Translated from behavioral/dynamodb/check_update_item_set_arithmetic_wrong_type_source_into_different_target_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-arithmetic-wrong-type-source-into-different-target-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetArithmeticWrongTypeSourceIntoDifferentTargetRejected
{
    private const string Check = "update-item-set-arithmetic-wrong-type-source-into-different-target-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;

    private static readonly (string Code, string Message) TypeMismatch = (
        "ValidationException",
        "An operand in the update expression has an incorrect data type");

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (resp, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"call unexpectedly SUCCEEDED where an error was required: {resp}") : (err.Code, err.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(gotMessage)}");
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code == "ResourceInUseException" && clock.Elapsed.TotalSeconds < TeardownWaitSeconds)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
                continue;
            }

            throw error.Exception;
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetSeconds)
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {Py.Repr(TableActiveBudgetSeconds)}s: {Py.Repr(status)}");
    }

    /// <summary>
    /// SET into an absent target attribute is rejected with ValidationException when the
    /// arithmetic operand it reads is not a Number, in either operand order, and creates nothing;
    /// the identical expression succeeds once the operand is a Number.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-arithmetic-wrong-type-source-into-different-target-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("item")));
        var original = Item(("pk", S("item")), ("label", S("Jane Doe")), ("price", N("20")));
        await ddb.PutItemAsync(table, original);

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET derived = label + :tax",
                ExpressionAttributeValues = Item((":tax", N("5"))),
            }),
            TypeMismatch);
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET derived = :tax + label",
                ExpressionAttributeValues = Item((":tax", N("5"))),
            }),
            TypeMismatch);

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, original),
            $"after both refused updates the item is {Py.Repr(unchanged)}, expected it unchanged from " +
            $"{Py.Repr(original)} (a target that gets created anyway would add a 'derived' attribute)");

        // Positive control: the identical shape, writing into the same different target, with a
        // Number-typed source instead.
        var control = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET derived = price + :tax",
            ExpressionAttributeValues = Item((":tax", N("5"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        var expected = new Dictionary<string, AttributeValue>(original, StringComparer.Ordinal) { ["derived"] = N("25") };
        Ensure(
            Py.Equal(control, expected),
            $"SET derived = price + :tax with price=20, :tax=5 returned ALL_NEW {Py.Repr(control)}, " +
            "expected the original item plus derived=25");
    }
}
