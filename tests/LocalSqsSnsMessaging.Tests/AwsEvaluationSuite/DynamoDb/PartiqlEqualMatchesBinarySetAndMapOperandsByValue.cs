// Translated from behavioral/dynamodb/check_partiql_equal_matches_binary_set_and_map_operands_by_value.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-equal-matches-binary-set-and-map-operands-by-value</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlEqualMatchesBinarySetAndMapOperandsByValue
{
    private const string CheckId = "partiql-equal-matches-binary-set-and-map-operands-by-value";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>Assert <paramref name="fn"/> is refused with the exact <c>(code, message)</c> pair.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task<HashSet<string>> SelectPks(IAmazonDynamoDB ddb, string table, string where, params AttributeValue[] parameters)
    {
        var items = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\" WHERE {where}",
            Parameters = [.. parameters],
            ConsistentRead = true,
        })).Items;
        return items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
    }

    private static string Sorted(HashSet<string> values) => Py.Repr(values.Order(StringComparer.Ordinal).ToList());

    /// <summary>
    /// PartiQL = and &lt;&gt; compare Binary, Set and NULL parameters by exact value and type; a String
    /// Set or Map matches = regardless of member or key order, a List only in stored order; the
    /// same equality agrees as an UpdateItem ConditionExpression and a PartiQL UPDATE condition.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-operators.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-equal-matches-binary-set-and-map-operands-by-value")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlnonscalar-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        var rows = new (string Pk, AttributeValue Val)[]
        {
            ("bval", B("hello")),
            ("bsval", BS("a"u8.ToArray(), "b"u8.ToArray())),
            ("nsval", NS("1", "2")),
            ("nullval", Null()),
            ("ssval", SS("a", "b")),
            ("mval", M(("x", N("1")), ("y", N("2")))),
            ("lval", L(N("1"), N("2"))),
        };
        foreach (var (pk, val) in rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("val", val)));
        }

        var allPks = rows.Select(r => r.Pk).ToHashSet(StringComparer.Ordinal);

        // B: = matches the equal stored value, not a different one; <> is the exact complement.
        var matched = await SelectPks(ddb, table, "val = ?", B("hello"));
        Ensure(matched.SetEquals(["bval"]), $"B = matched {Sorted(matched)}, expected ['bval']");
        var mismatched = await SelectPks(ddb, table, "val = ?", B("world"));
        Ensure(mismatched.Count == 0, $"B = against a different value matched {Sorted(mismatched)}, expected none");
        var notMatched = await SelectPks(ddb, table, "val <> ?", B("hello"));
        Ensure(notMatched.SetEquals(allPks.Where(p => p != "bval")), $"B <> matched {Sorted(notMatched)}, expected everything but bval");

        // BS and NS: a parameter permuted in member order still matches =.
        matched = await SelectPks(ddb, table, "val = ?", BS("b"u8.ToArray(), "a"u8.ToArray()));
        Ensure(matched.SetEquals(["bsval"]), $"permuted BS = matched {Sorted(matched)}, expected ['bsval']");
        matched = await SelectPks(ddb, table, "val = ?", NS("2", "1"));
        Ensure(matched.SetEquals(["nsval"]), $"permuted NS = matched {Sorted(matched)}, expected ['nsval']");

        // NULL: = matches a stored NULL and nothing else.
        matched = await SelectPks(ddb, table, "val = ?", Null());
        Ensure(matched.SetEquals(["nullval"]), $"NULL = matched {Sorted(matched)}, expected ['nullval']");

        // M: permuted key order still matches =; a differing value under one key does not.
        matched = await SelectPks(ddb, table, "val = ?", M(("y", N("2")), ("x", N("1"))));
        Ensure(matched.SetEquals(["mval"]), $"permuted M = matched {Sorted(matched)}, expected ['mval']");
        mismatched = await SelectPks(ddb, table, "val = ?", M(("x", N("1")), ("y", N("99"))));
        Ensure(mismatched.Count == 0, $"M = against a changed value matched {Sorted(mismatched)}, expected none");

        // L: = matches only the stored element order.
        matched = await SelectPks(ddb, table, "val = ?", L(N("1"), N("2")));
        Ensure(matched.SetEquals(["lval"]), $"L = in stored order matched {Sorted(matched)}, expected ['lval']");
        var reordered = await SelectPks(ddb, table, "val = ?", L(N("2"), N("1")));
        Ensure(reordered.Count == 0, $"L = reordered matched {Sorted(reordered)}, expected none");

        // The same equality agrees as an UpdateItem ConditionExpression: a permuted SS applies.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ssval"))),
            UpdateExpression = "SET marker = :m",
            ConditionExpression = "val = :v",
            ExpressionAttributeValues = Item((":m", S("via-updateitem")), (":v", SS("b", "a"))),
        });
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ssval"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(after.GetValueOrDefault("marker"), S("via-updateitem")),
            $"UpdateItem's ConditionExpression on a permuted SS must have applied, got {Py.Repr(after.GetValueOrDefault("marker"))}");

        // And as a PartiQL UPDATE write condition: a permuted SS fires the write.
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET marker2 = ? WHERE pk = ? AND val = ?",
            Parameters = [S("via-partiql"), S("ssval"), SS("b", "a")],
        });
        after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ssval"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(after.GetValueOrDefault("marker2"), S("via-partiql")),
            $"the PartiQL UPDATE WHERE condition on a permuted SS must have applied, got {Py.Repr(after.GetValueOrDefault("marker2"))}");

        // A mismatched M as the write condition raises ConditionalCheckFailedException, item unchanged.
        var before = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("mval"))), ConsistentRead = true })).Item;
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = $"UPDATE \"{table}\" SET marker3 = ? WHERE pk = ? AND val = ?",
                Parameters = [S("should-not-apply"), S("mval"), M(("x", N("1")), ("y", N("99")))],
            }),
            Ccf);
        after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("mval"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, before), $"a mismatched M write condition must leave the item untouched, got {Py.Repr(after)}");
    }
}
