// Translated from behavioral/dynamodb/check_update_set_arithmetic_and_list_append_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-set-arithmetic-and-list-append-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateSetArithmeticAndListAppendTypeMismatchRejected
{
    private const string Check = "update-set-arithmetic-and-list-append-type-mismatch-rejected";
    private const string TypeMismatch = "An operand in the update expression has an incorrect data type";
    private static readonly TimeSpan TableActiveBudget = TimeSpan.FromSeconds(120.0);
    private static readonly TimeSpan TeardownWait = TimeSpan.FromSeconds(120.0);

    /// <summary>
    /// UpdateItem arithmetic or list_append against an attribute of the wrong stored type is
    /// rejected with ValidationException and leaves the item unchanged; a same-typed operation
    /// on a sibling attribute still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-set-arithmetic-and-list-append-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("doc")));
        var original = Item(
            ("pk", S("doc")),
            ("word", S("hello")),
            ("props", M(("depth", N("1")))),
            ("tags", NS("1", "2")),
            ("n", N("10")),
            ("history", L(S("a"))));
        await ddb.PutItemAsync(table, original);

        // SET on a String attribute using "+" arithmetic: the operand's own type is a String.
        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET word = word + :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            $"SET word = word + :one with word stored as S answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            "expected ValidationException");
        Ensure(
            message == TypeMismatch,
            $"SET word = word + :one refusal message is {Py.Repr(message)}, expected {Py.Repr(TypeMismatch)}");

        // SET on a Map attribute using "-" arithmetic: the operand's own type is a Map.
        (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET props = props - :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
        }));
        Ensure(
            code == "ValidationException",
            $"SET props = props - :one with props stored as M answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            "expected ValidationException");
        Ensure(
            message == TypeMismatch,
            $"SET props = props - :one refusal message is {Py.Repr(message)}, expected {Py.Repr(TypeMismatch)}");

        // list_append requires List operands; the stored attribute is a Number Set.
        (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET tags = list_append(tags, :more)",
            ExpressionAttributeValues = Item((":more", L(N("3")))),
        }));
        Ensure(
            code == "ValidationException",
            $"list_append(tags, :more) with tags stored as NS answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            "expected ValidationException");
        Ensure(
            message == TypeMismatch,
            $"list_append(tags, :more) refusal message is {Py.Repr(message)}, expected {Py.Repr(TypeMismatch)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, original),
            $"after three refused type-mismatched updates the item is {Py.Repr(unchanged)}, expected it " +
            $"unchanged from {Py.Repr(original)}");

        // Positive controls: the identical operation against a same-typed sibling attribute applies.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET n = n + :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("n", N("11")))),
            $"SET n = n + :one on a Number attribute returned {Py.Repr(resp.Attributes)}, expected n=11");
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET history = list_append(history, :more)",
            ExpressionAttributeValues = Item((":more", L(S("b")))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("history", L(S("a"), S("b"))))),
            $"list_append(history, :more) on a List attribute returned {Py.Repr(resp.Attributes)}, " +
            "expected [a, b]");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final["word"], S("hello")) && Py.Equal(final["props"], M(("depth", N("1"))))
                && Py.Equal(final["tags"], NS("1", "2")),
            $"the three attributes under test must still read their original values: {Py.Repr(final)}");
        Ensure(
            Py.Equal(final["n"], N("11")) && Py.Equal(final["history"], L(S("a"), S("b"))),
            $"both positive controls must have landed: {Py.Repr(final)}");
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
