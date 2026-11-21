// Translated from behavioral/dynamodb/check_update_through_bracket_index_on_non_list_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-through-bracket-index-on-non-list-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateThroughBracketIndexOnNonListRejected
{
    private const string Check = "update-through-bracket-index-on-non-list-rejected";
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
    /// A `[n]` bracket index in an update path is rejected with ValidationException when the
    /// attribute it indexes is a String or a Map rather than a List, leaving the item unchanged,
    /// while the identical bracket form against a real List element succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-through-bracket-index-on-non-list-rejected")]
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
            ("doc_map", M(("x", N("1")))));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // One refused bracket-index update: its label, as the expression reads, and the stored attribute it indexes.
        async Task Refused(string expression, Dictionary<string, AttributeValue>? values, string label, string stored)
        {
            var request = new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = expression };
            if (values is not null)
            {
                request.ExpressionAttributeValues = values;
            }

            var (code, message) = await Error(async () => await ddb.UpdateItemAsync(request));
            Ensure(
                code == "ValidationException",
                $"{label} with {stored} answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
                "ValidationException");
            Ensure(message == PathInvalid, $"{label} refusal message is {Py.Repr(message)}, expected {Py.Repr(PathInvalid)}");
        }

        // REMOVE/ADD/DELETE indexing a String attribute with [n].
        await Refused("REMOVE word[0]", null, "REMOVE word[0]", "word stored as S");
        await Refused("ADD word[0] :one", Item((":one", N("1"))), "ADD word[0] :one", "word stored as S");
        await Refused("DELETE word[0] :v", Item((":v", SS("x"))), "DELETE word[0] :v", "word stored as S");

        // SET/REMOVE/ADD/DELETE indexing a Map attribute with [n].
        await Refused("SET doc_map[1] = :v", Item((":v", S("x"))), "SET doc_map[1]", "doc_map stored as M");
        await Refused("REMOVE doc_map[1]", null, "REMOVE doc_map[1]", "doc_map stored as M");
        await Refused("ADD doc_map[1] :one", Item((":one", N("1"))), "ADD doc_map[1] :one", "doc_map stored as M");
        await Refused("DELETE doc_map[1] :v", Item((":v", SS("x"))), "DELETE doc_map[1] :v", "doc_map stored as M");

        var unchanged = await Read();
        Ensure(
            Py.Equal(unchanged, original),
            $"after the seven refused bracket-index updates the item is {Py.Repr(unchanged)}, expected it " +
            $"unchanged from {Py.Repr(original)}");

        // Positive controls: the identical bracket forms against a real List at the same depth.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET lst = :l",
            ExpressionAttributeValues = Item((":l", L(SS("a", "b"), N("5"), S("c")))),
        });

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET lst[2] = :v",
            ExpressionAttributeValues = Item((":v", S("z"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("lst", L(S("z"))))),
            $"SET lst[2] on a real list returned {Py.Repr(resp.Attributes)}, expected lst[2]='z'");

        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = "REMOVE lst[2]" });
        var afterRemove = await Read();
        Ensure(
            Py.Equal(afterRemove["lst"].L, new[] { SS("a", "b"), N("5") }),
            $"REMOVE lst[2] on a real list left {Py.Repr(afterRemove["lst"])}");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD lst[1] :one",
            ExpressionAttributeValues = Item((":one", N("1"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("lst", L(N("6"))))),
            $"ADD lst[1] :one on a real list returned {Py.Repr(resp.Attributes)}, expected lst[1]=6");

        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE lst[0] :v",
            ExpressionAttributeValues = Item((":v", SS("a"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("lst", L(SS("b"))))),
            $"DELETE lst[0] :v on a real list returned {Py.Repr(resp.Attributes)}, expected lst[0]={{'b'}}");

        var final = await Read();
        Ensure(
            Py.Equal(final["lst"].L, new[] { SS("b"), N("6") }),
            $"final list is {Py.Repr(final["lst"])}, expected [{{'b'}}, 6]");
        Ensure(
            Py.Equal(final["word"], S("scalar")) && Py.Equal(final["doc_map"], M(("x", N("1")))),
            $"the attributes under test must still read their original values: {Py.Repr(final)}");
    }
}
