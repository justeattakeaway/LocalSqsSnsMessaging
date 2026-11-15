// Translated from behavioral/dynamodb/check_condition_nested_path_type_mismatch_fails_the_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-nested-path-type-mismatch-fails-the-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class ConditionNestedPathTypeMismatchFailsTheCondition
{
    private const string CcfMsg = "The conditional request failed";

    private static readonly Dictionary<string, string> NamesMap = Names(("#m", "m"), ("#k", "k"), ("#mm", "mm"), ("#l", "l"), ("#z", "z"), ("#u", "u"));

    private static readonly string[] Fails =
    [
        "attribute_exists(#m.#z)",
        "attribute_not_exists(#m.#k)",
        "#m.#k <> :inner",
        "attribute_exists(#l.#k)",
        "attribute_exists(#m[0])",
        "#m.#k = :other OR #m.#mm.#k = :other OR #l[0].#k = :other",
    ];

    private static Dictionary<string, AttributeValue> Original() => Item(
        ("pk", S("nested")),
        ("m", M(("k", S("inner")), ("mm", M(("k", S("deep")))))),
        ("l", L(M(("k", S("first"))), S("s"))));

    /// <summary>
    /// A condition dereferencing a list member by name, indexing a map, or comparing nested members
    /// that differ is ConditionalCheckFailedException on UpdateItem and DeleteItem; item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.Attributes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-nested-path-type-mismatch-fails-the-condition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-nested-fail-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("nested")));
        await ddb.PutItemAsync(table, Original());
        Dictionary<string, AttributeValue> Values() => Item((":x", S("z")), (":inner", S("inner")), (":other", S("other")));

        foreach (var condition in Fails)
        {
            var tokens = TokenPattern().Matches(condition).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            tokens.UnionWith(["#u", ":x"]);
            var names = NamesMap.Where(kv => tokens.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var vals = Values().Where(kv => tokens.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET #u = :x",
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = vals,
            }));
            Ensure(
                (code, msg) == ("ConditionalCheckFailedException", CcfMsg),
                $"UpdateItem under {Py.Repr(condition)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            Ensure(Py.Equal(item, Original()), $"UpdateItem under failing {Py.Repr(condition)} changed the item to {Py.Repr(item)}");
        }

        var (deleteCode, deleteMsg) = await Error(() => ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = "#m.#mm.#k = :other",
            ExpressionAttributeNames = Names(("#m", "m"), ("#mm", "mm"), ("#k", "k")),
            ExpressionAttributeValues = Item((":other", S("other"))),
        }));
        Ensure(
            (deleteCode, deleteMsg) == ("ConditionalCheckFailedException", CcfMsg),
            $"DeleteItem under '#m.#mm.#k = :other' gave {Py.Repr(deleteCode)}: {Py.Repr(deleteMsg)}");
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, Original()), $"DeleteItem under a failing nested condition left {Py.Repr(after)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET #u = :x",
            ConditionExpression = "#l[0].#k = :first",
            ExpressionAttributeNames = Names(("#u", "u"), ("#l", "l"), ("#k", "k")),
            ExpressionAttributeValues = Item((":x", S("z")), (":first", S("first"))),
        });
        var updated = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        var want = Original();
        want["u"] = S("z");
        Ensure(Py.Equal(updated, want), $"UpdateItem under a holding condition left {Py.Repr(updated)}");
    }

    [GeneratedRegex("[#:][A-Za-z0-9_]+")]
    private static partial Regex TokenPattern();

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null ? (error.Code, error.Message) : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
