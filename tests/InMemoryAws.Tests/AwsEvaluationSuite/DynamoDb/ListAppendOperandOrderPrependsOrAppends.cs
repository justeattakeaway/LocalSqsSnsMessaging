// Translated from behavioral/dynamodb/check_list_append_operand_order_prepends_or_appends.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-append-operand-order-prepends-or-appends</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListAppendOperandOrderPrependsOrAppends
{
    private const string CheckId = "list-append-operand-order-prepends-or-appends";
    private const double TableActiveBudgetSeconds = 120.0;
    private static readonly string[] Prepended = ["a0", "a1", "b", "c"];
    private static readonly string[] Appended = ["a0", "a1", "b", "c", "d"];

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"call unexpectedly SUCCEEDED where an error was required: {Py.Repr(resp)}");
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        try
        {
            await ddb.DeleteTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
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

    private static List<string> Strings(AttributeValue attr) => [.. attr.L.Select(e => e.S)];

    /// <summary>
    /// `list_append(a, b)` concatenates in operand order: `list_append(:new, l)` prepends and
    /// `list_append(l, :new)` appends; a non-list operand fails with ValidationException and leaves
    /// the stored list unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-append-operand-order-prepends-or-appends")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("concat")));
        await ddb.PutItemAsync(table, Item(("pk", S("concat")), ("l", L(S("b"), S("c")))));

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET l = list_append(:new, l)",
            ExpressionAttributeValues = Item((":new", L(S("a0"), S("a1")))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Strings(resp.Attributes["l"]).SequenceEqual(Prepended),
            $"list_append(:new, l) produced {Py.Repr(resp.Attributes["l"])}; expected :new PREPENDED in its own order");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET l = list_append(l, :new)",
            ExpressionAttributeValues = Item((":new", L(S("d")))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Strings(resp.Attributes["l"]).SequenceEqual(Appended),
            $"list_append(l, :new) produced {Py.Repr(resp.Attributes["l"])}; expected :new APPENDED");

        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET l = list_append(l, :new)",
            ExpressionAttributeValues = Item((":new", S("not-a-list"))),
        }));
        Ensure(code == "ValidationException", $"list_append with a String operand answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        const string expected = "1 validation error detected: Invalid UpdateExpression: Incorrect operand type for operator or function; operator or function: list_append, operand type: S";
        Ensure(message == expected, $"list_append refusal message is {Py.Repr(message)}, expected {Py.Repr(expected)}");
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item["l"];
        Ensure(
            Strings(stored).SequenceEqual(Appended),
            $"after the refused list_append the stored list is {Py.Repr(stored)}; a refused update must leave the list exactly as the two accepted ones built it");
    }
}
