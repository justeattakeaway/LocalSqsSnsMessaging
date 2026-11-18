// Translated from behavioral/dynamodb/check_key_condition_illegal_sort_key_operator_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.key-condition-illegal-sort-key-operator-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class KeyConditionIllegalSortKeyOperatorRejected
{
    private const string CheckId = "key-condition-illegal-sort-key-operator-rejected";
    private const double TableActiveBudgetSeconds = 120.0;
    private static readonly int[] NumericSortKeys = [1, 2, 3, 10, 11];
    private static readonly int[] ExpectedAfterOne = [2, 3, 10, 11];
    private static readonly string[] StringSortKeys = ["a", "b", "c"];
    private static readonly string[] JustA = ["a"];
    private static readonly string[] AThroughB = ["a", "b"];

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

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetSeconds)
        {
            status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(status == "ACTIVE", $"table {Py.Repr(table)} not ACTIVE within budget: {Py.Repr(status)}");
    }

    /// <summary>
    /// A KeyConditionExpression rejects `&lt;&gt;`/`OR` and an operand type the sort key cannot take --
    /// `begins_with`, `&lt;` and `BETWEEN` given an N or a BOOL operand against an S sort key, or any
    /// of these against an N one -- with ValidationException, while the legal form still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.key-condition-illegal-sort-key-operator-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, table));
        await WaitActive(ddb, table);

        foreach (var sk in NumericSortKeys)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", N(sk.ToString(CultureInfo.InvariantCulture)))));
        }

        Dictionary<string, AttributeValue> Values() => Item((":p", S("p")), (":s", N("1")));
        // All three forms are legal in a FilterExpression, just not in a key condition.
        const string invalidOperator = "Invalid KeyConditionExpression: Invalid operator used in KeyConditionExpression";
        var illegal = new (string Label, string Expr, string Expected)[]
        {
            ("not-equal", "pk = :p AND sk <> :s", invalidOperator),
            ("or", "pk = :p OR sk = :s", invalidOperator),
            (
                "begins_with on N", "pk = :p AND begins_with(sk, :s)",
                "Invalid KeyConditionExpression: Incorrect operand type for operator or function; operator or function: begins_with, operand type: N"
            ),
        };
        foreach (var (label, expr, expected) in illegal)
        {
            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = expr,
                ExpressionAttributeValues = Values(),
            }));
            Ensure(
                code == "ValidationException",
                $"KeyConditionExpression {Py.Repr(expr)} ({label}) answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException -- this form is legal in a FilterExpression, never in a key condition");
            Ensure(message == expected, $"the {label} refusal's message is {Py.Repr(message)}, expected {Py.Repr(expected)}");
        }

        // Positive control: a legal sort-key operator over the same partition.
        var items = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND sk > :s",
            ExpressionAttributeValues = Values(),
            ConsistentRead = true,
        })).Items;
        var got = items.Select(i => int.Parse(i["sk"].N, CultureInfo.InvariantCulture)).ToList();
        Ensure(got.SequenceEqual(ExpectedAfterOne), $"Query pk = p AND sk > 1 returned sort keys {Py.Repr(got)}; expected [2, 3, 10, 11] in NUMERIC order");

        // --- The same operand-type validation holds against an S sort key: begins_with(), `<` and
        // BETWEEN refuse an N or a BOOL operand, not just the non-key-condition operators above. ---
        var tableS = FidelityContext.UniqueName("sim2real-behav-ddb-strsk-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableS,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, tableS));
        await WaitActive(ddb, tableS);

        foreach (var sk in StringSortKeys)
        {
            await ddb.PutItemAsync(tableS, Item(("pk", S("p")), ("sk", S(sk))));
        }

        var mismatched = new (string Label, string Expr, Func<Dictionary<string, AttributeValue>> Values, string Expected)[]
        {
            (
                "begins_with with an N operand", "pk = :p AND begins_with(sk, :s)",
                () => Item((":p", S("p")), (":s", N("1"))),
                "Invalid KeyConditionExpression: Incorrect operand type for operator or function; operator or function: begins_with, operand type: N"
            ),
            (
                "begins_with with a BOOL operand", "pk = :p AND begins_with(sk, :s)",
                () => Item((":p", S("p")), (":s", Bool(true))),
                "Invalid KeyConditionExpression: Incorrect operand type for operator or function; operator or function: begins_with, operand type: BOOL"
            ),
            (
                "< with an N operand", "pk = :p AND sk < :s",
                () => Item((":p", S("p")), (":s", N("1"))),
                "One or more parameter values were invalid: Condition parameter type does not match schema type"
            ),
            (
                "< with a BOOL operand", "pk = :p AND sk < :s",
                () => Item((":p", S("p")), (":s", Bool(true))),
                "Invalid KeyConditionExpression: Incorrect operand type for operator or function; operator or function: <, operand type: BOOL"
            ),
            (
                "BETWEEN with an N lower bound", "pk = :p AND sk BETWEEN :s1 AND :s2",
                () => Item((":p", S("p")), (":s1", N("1")), (":s2", S("z"))),
                "Invalid KeyConditionExpression: The BETWEEN operator requires same data type for lower and upper bounds; lower bound operand: AttributeValue: {N:1}, upper bound operand: AttributeValue: {S:z}"
            ),
            (
                "BETWEEN with a BOOL lower bound", "pk = :p AND sk BETWEEN :s1 AND :s2",
                () => Item((":p", S("p")), (":s1", Bool(true)), (":s2", S("z"))),
                "Invalid KeyConditionExpression: Incorrect operand type for operator or function; operator or function: BETWEEN, operand type: BOOL"
            ),
        };
        foreach (var (label, expr, values, expected) in mismatched)
        {
            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = tableS,
                KeyConditionExpression = expr,
                ExpressionAttributeValues = values(),
            }));
            Ensure(
                code == "ValidationException",
                $"KeyConditionExpression {Py.Repr(expr)} ({label}) against an S sort key answered {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
            Ensure(message == expected, $"the {label} refusal's message against an S sort key is {Py.Repr(message)}, expected {Py.Repr(expected)}");
        }

        // Positive control: the same operators are legal with an operand of the sort key's own type.
        var begins = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = tableS,
            KeyConditionExpression = "pk = :p AND begins_with(sk, :s)",
            ExpressionAttributeValues = Item((":p", S("p")), (":s", S("a"))),
            ConsistentRead = true,
        })).Items;
        Ensure(begins.Select(i => i["sk"].S).SequenceEqual(JustA), $"begins_with(sk, 'a') against the S sort key returned {Py.Repr(begins)}; expected just 'a'");
        var between = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = tableS,
            KeyConditionExpression = "pk = :p AND sk BETWEEN :s1 AND :s2",
            ExpressionAttributeValues = Item((":p", S("p")), (":s1", S("a")), (":s2", S("b"))),
            ConsistentRead = true,
        })).Items;
        Ensure(between.Select(i => i["sk"].S).SequenceEqual(AThroughB), $"sk BETWEEN 'a' AND 'b' against the S sort key returned {Py.Repr(between)}; expected ['a', 'b']");
    }
}
