// Translated from behavioral/dynamodb/check_update_remove_through_missing_nested_parent_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-remove-through-missing-nested-parent-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateRemoveThroughMissingNestedParentRejected
{
    private const string Check = "update-remove-through-missing-nested-parent-rejected";
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
    /// REMOVE rejects a nested path whose parent attribute is entirely absent from the item,
    /// leaving the item unchanged, while the same REMOVE form against a real map succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-remove-through-missing-nested-parent-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(
            ("pk", S("doc")),
            ("keep", S("k")),
            ("box", M(("hits", N("1")))));
        await ddb.PutItemAsync(table, original);

        // REMOVE through a nested path whose parent ("absent") does not exist on the item at all.
        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE absent.child",
        }));
        Ensure(
            code == "ValidationException",
            $"REMOVE absent.child with no 'absent' attribute answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            "expected ValidationException");
        Ensure(message == PathInvalid, $"REMOVE absent.child refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, original),
            $"after the refused REMOVE the item is {Py.Repr(unchanged)}, expected it unchanged from " +
            $"{Py.Repr(original)}");
        Ensure(!unchanged.ContainsKey("absent"), $"the refused REMOVE must not create the absent parent: {Py.Repr(unchanged)}");

        // The same expression against a nonexistent item is a no-op, not a rejection: there is
        // nothing stored to validate the path against, and REMOVE never upserts.
        var absentKey = Item(("pk", S("nowhere")));
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = absentKey, UpdateExpression = "REMOVE absent.child" });
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = absentKey, ConsistentRead = true })).Item is null,
            "REMOVE absent.child against a nonexistent item must not create it");

        // Positive control: the identical REMOVE form against a real map on the same item.
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = "REMOVE box.hits" });
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(!final["box"].M.ContainsKey("hits"), $"REMOVE box.hits on a real map left {Py.Repr(final["box"])}");
        Ensure(Py.Equal(final["keep"], S("k")), $"the attribute under test must still read its original value: {Py.Repr(final)}");
    }
}
