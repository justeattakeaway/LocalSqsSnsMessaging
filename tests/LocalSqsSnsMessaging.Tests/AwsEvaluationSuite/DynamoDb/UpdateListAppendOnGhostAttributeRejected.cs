// Translated from behavioral/dynamodb/check_update_list_append_on_ghost_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-list-append-on-ghost-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateListAppendOnGhostAttributeRejected
{
    private const string Check = "update-list-append-on-ghost-attribute-rejected";
    private const string MissingAttr = "The provided expression refers to an attribute that does not exist in the item";
    private static readonly TimeSpan TableActiveBudget = TimeSpan.FromSeconds(120.0);
    private static readonly TimeSpan TeardownWait = TimeSpan.FromSeconds(120.0);

    /// <summary>
    /// `list_append` naming an attribute the item does not have raises ValidationException
    /// naming the ghost attribute and leaves the item unchanged; the same call against an
    /// existing list attribute still appends.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-list-append-on-ghost-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(("pk", S("doc")), ("kept", S("k")), ("history", L(S("a"))));
        await ddb.PutItemAsync(table, original);

        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET ghost = list_append(ghost, :l)",
            ExpressionAttributeValues = Item((":l", L(S("x")))),
        }));
        Ensure(
            code == "ValidationException",
            $"list_append(ghost, :l) on an absent attribute answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            "expected ValidationException");
        Ensure(
            message == MissingAttr,
            $"list_append(ghost, :l) refusal message is {Py.Repr(message)}, expected {Py.Repr(MissingAttr)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, original),
            $"after the refused list_append the item is {Py.Repr(unchanged)}, expected it unchanged " +
            $"from {Py.Repr(original)}");
        Ensure(
            !unchanged.ContainsKey("ghost"),
            $"a refused list_append must not create the attribute it named: {Py.Repr(unchanged)}");

        // Positive control: the identical function call against an existing list attribute appends.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET history = list_append(history, :l)",
            ExpressionAttributeValues = Item((":l", L(S("b")))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("history", L(S("a"), S("b"))))),
            $"list_append(history, :l) on an existing list returned {Py.Repr(resp.Attributes)}, " +
            "expected [a, b]");
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final, Item(("pk", S("doc")), ("kept", S("k")), ("history", L(S("a"), S("b"))))),
            $"final item is {Py.Repr(final)}, expected kept intact and history=[a, b]");
    }

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
