// Translated from behavioral/dynamodb/check_execute_statement_in_operator_with_101_operands_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-statement-in-operator-with-101-operands-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteStatementInOperatorWith101OperandsRejected
{
    private const string CheckId = "execute-statement-in-operator-with-101-operands-rejected";

    /// <summary>A pk/sk table for PartiQL SELECT statements.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-pqin-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    /// <summary>(code, message) of the error fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"call unexpectedly SUCCEEDED where ValidationException was required: {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL WHERE clause's IN predicate is refused past 100 operands; 100 is the accepted
    /// boundary and still selects every matching item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-reference.operators.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-statement-in-operator-with-101-operands-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await MakeTable(ctx);
        for (var i = 0; i < 3; i++)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S($"a{i}")), ("status", S($"s{i}"))));
        }

        var over = $"SELECT * FROM \"{t}\" WHERE pk='p1' AND status IN (" + string.Join(", ", Enumerable.Repeat("?", 101)) + ")";
        var overParams = Enumerable.Range(0, 101).Select(i => S($"s{i}")).ToList();
        var (code, message) = await Error(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = over, Parameters = overParams }));
        var expected = ("ValidationException", "The IN operator is provided with too many operands; number of operands: 101");
        Ensure((code, message) == expected, $"the 101-operand IN raised {Py.Repr((code, message))}, expected {Py.Repr(expected)}");

        var boundary = $"SELECT * FROM \"{t}\" WHERE pk='p1' AND status IN (" + string.Join(", ", Enumerable.Repeat("?", 100)) + ")";
        var boundaryParams = Enumerable.Range(0, 3).Select(i => S($"s{i}")).Concat(Enumerable.Range(0, 97).Select(i => S($"unused{i}"))).ToList();
        var page = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = boundary, Parameters = boundaryParams, ConsistentRead = true });
        var matched = page.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            matched.SequenceEqual(["a0", "a1", "a2"]),
            $"the 100-operand IN matched {"(" + string.Join(", ", matched.Select(Py.Repr)) + ")"}, expected every seeded item");
    }
}
