// Translated from behavioral/dynamodb/check_update_remove_add_delete_through_invalid_path_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-remove-add-delete-through-invalid-path-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateRemoveAddDeleteThroughInvalidPathRejected
{
    private const string Check = "update-remove-add-delete-through-invalid-path-rejected";
    private const string PathInvalid = "The document path provided in the update expression is invalid for update";
    private static readonly TimeSpan TableActiveBudget = TimeSpan.FromSeconds(120.0);
    private static readonly TimeSpan TeardownWait = TimeSpan.FromSeconds(120.0);

    /// <summary>
    /// REMOVE, ADD and DELETE each reject a nested path whose parent is not a container,
    /// leaving the item unchanged, while the same action against a real map or set succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-remove-add-delete-through-invalid-path-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(
            ("pk", S("doc")),
            ("word", S("scalar")),
            ("counters", M(("hits", N("1")))),
            ("flags", M(("labels", SS("a", "b")))));
        await ddb.PutItemAsync(table, original);

        // REMOVE through a path whose parent ("word") is a String, not a map.
        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE word.child",
        }));
        Ensure(
            code == "ValidationException",
            $"REMOVE word.child with word stored as S answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            "ValidationException");
        Ensure(message == PathInvalid, $"REMOVE word.child refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        // ADD through the same wrong-shape parent.
        (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD word.hits :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            $"ADD word.hits :one with word stored as S answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            "ValidationException");
        Ensure(message == PathInvalid, $"ADD word.hits :one refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        // DELETE through the same wrong-shape parent.
        (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE word.tags :v",
            ExpressionAttributeValues = Item((":v", SS("x"))),
        }));
        Ensure(
            code == "ValidationException",
            $"DELETE word.tags :v with word stored as S answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            "ValidationException");
        Ensure(message == PathInvalid, $"DELETE word.tags :v refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        var unchanged = await Get(ddb, table, key);
        Ensure(
            Py.Equal(unchanged, original),
            $"after the three refused invalid-path updates the item is {Py.Repr(unchanged)}, expected it " +
            $"unchanged from {Py.Repr(original)}");

        // Positive controls: the identical action against a real container at the same depth.
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = "REMOVE counters.hits" });
        var afterRemove = await Get(ddb, table, key);
        Ensure(
            !afterRemove["counters"].M.ContainsKey("hits"),
            $"REMOVE counters.hits on a real map left {Py.Repr(afterRemove["counters"])}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD counters.visits :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        });
        var afterAdd = await Get(ddb, table, key);
        Ensure(
            Py.Equal(afterAdd["counters"].M["visits"], N("1")),
            $"ADD counters.visits :one on a real map produced {Py.Repr(afterAdd["counters"])}, expected " +
            "visits=1");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE flags.labels :v",
            ExpressionAttributeValues = Item((":v", SS("a"))),
        });
        var final = await Get(ddb, table, key);
        Ensure(
            final["flags"].M["labels"].SS.ToHashSet(StringComparer.Ordinal).SetEquals(["b"]),
            $"DELETE flags.labels :v on a real set produced {Py.Repr(final["flags"])}, expected only 'b' left");
        Ensure(
            Py.Equal(final["word"], S("scalar")),
            $"the attribute under test must still read its original value: {Py.Repr(final)}");
    }

    private static async Task<Dictionary<string, AttributeValue>> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<UpdateItemResponse>> fn)
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"call unexpectedly SUCCEEDED where an error was required: {Py.Repr(resp?.Attributes)}");
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

            if (error.Code == "ResourceInUseException" && clock.Elapsed < TeardownWait)
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
        TableStatus? status = null;
        while (clock.Elapsed < TableActiveBudget)
        {
            status = (await ddb.DescribeTableAsync(name)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within 120.0s: {Py.Repr(status)}");
    }
}
