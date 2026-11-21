// Translated from behavioral/dynamodb/check_update_remove_add_delete_through_missing_parent_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-remove-add-delete-through-missing-parent-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateRemoveAddDeleteThroughMissingParentRejected
{
    private const string Check = "update-remove-add-delete-through-missing-parent-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private const double TeardownWaitSeconds = 120.0;
    private const string PathInvalid = "The document path provided in the update expression is invalid for update";

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (resp, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"call unexpectedly SUCCEEDED where an error was required: {resp}") : (err.Code, err.Message);
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
    /// REMOVE, ADD and DELETE each reject a nested path whose top-level attribute does not
    /// exist at all, leaving the item unchanged, while the same path is legal once that attribute
    /// is created as a map.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-remove-add-delete-through-missing-parent-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(("pk", S("doc")), ("keep", S("k")));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // REMOVE through a path whose top-level attribute ("m") does not exist at all.
        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE m.a",
        }));
        Ensure(
            code == "ValidationException",
            $"REMOVE m.a with no stored m answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == PathInvalid, $"REMOVE m.a refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        // ADD through the same missing top-level attribute.
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD m.a :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            $"ADD m.a :one with no stored m answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == PathInvalid, $"ADD m.a :one refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        // DELETE through the same missing top-level attribute.
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE m.a :v",
            ExpressionAttributeValues = Item((":v", SS("x"))),
        }));
        Ensure(
            code == "ValidationException",
            $"DELETE m.a :v with no stored m answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        Ensure(message == PathInvalid, $"DELETE m.a :v refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        var unchanged = await Read();
        Ensure(
            Py.Equal(unchanged, original),
            $"after the three refused missing-parent updates the item is {Py.Repr(unchanged)}, expected it " +
            $"unchanged from {Py.Repr(original)}");
        Ensure(!unchanged.ContainsKey("m"), $"a refused update through a missing parent must not create it: {Py.Repr(unchanged)}");

        // Positive controls: the same path forms once the top-level attribute exists as a map.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET m = :empty",
            ExpressionAttributeValues = Item((":empty", M())),
        });

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD m.a :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("m", M(("a", N("1")))))),
            $"ADD m.a :one once m exists returned {Py.Repr(resp.Attributes)}, expected m={{a: 1}}");

        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = "REMOVE m.a" });
        var afterRemove = await Read();
        Ensure(Py.Equal(afterRemove["m"], M()), $"REMOVE m.a once m exists left {Py.Repr(afterRemove["m"])}, expected an empty map");

        // DELETE through an existing map parent against a set member that is itself absent is a
        // documented no-op, not a path error: it still demonstrates the parent path is now valid.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE m.tags :v",
            ExpressionAttributeValues = Item((":v", SS("x"))),
        });
        var final = await Read();
        Ensure(
            Py.Equal(final, Item(("pk", S("doc")), ("keep", S("k")), ("m", M()))),
            $"final item is {Py.Repr(final)}, expected keep intact and m={{}}");
    }
}
