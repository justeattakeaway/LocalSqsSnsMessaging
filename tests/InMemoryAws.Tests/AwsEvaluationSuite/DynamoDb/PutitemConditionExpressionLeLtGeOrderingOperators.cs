// Translated from behavioral/dynamodb/check_putitem_condition_expression_le_lt_ge_ordering_operators.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-expression-le-lt-ge-ordering-operators</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionExpressionLeLtGeOrderingOperators
{
    private const string Check = "putitem-condition-expression-le-lt-ge-ordering-operators";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    // (label, operator, stored value when the condition holds, threshold for that case,
    //  stored value when the condition does not hold, threshold for that case)
    private static readonly (string Label, string Op, string TrueVal, string TrueThreshold, string FalseVal, string FalseThreshold)[] Cases =
    [
        ("le", "<=", "5", "5", "6", "5"),
        ("lt", "<", "5", "6", "6", "5"),
        ("ge", ">=", "5", "5", "4", "5"),
    ];

    /// <summary>
    /// A PutItem ConditionExpression using &lt;=, &lt; or &gt;= against a numeric attribute lets the
    /// write land when the comparison holds, and refuses it with ConditionalCheckFailedException,
    /// leaving the item unchanged, when it does not.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-expression-le-lt-ge-ordering-operators")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-put-ord-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (label, op, trueVal, trueThreshold, falseVal, falseThreshold) in Cases)
        {
            var trueKey = Item(("pk", S($"{label}-true")));
            await ddb.PutItemAsync(table, Item(("pk", S($"{label}-true")), ("val", N(trueVal))));
            var newItem = Item(("pk", S($"{label}-true")), ("val", N(trueVal)), ("applied", Bool(true)));
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = newItem,
                ConditionExpression = $"val {op} :t",
                ExpressionAttributeValues = Item((":t", N(trueThreshold))),
            });
            var landed = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = trueKey, ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(landed, newItem),
                $"PutItem under 'val {op} :t' ({trueVal} {op} {trueThreshold}) should land, got {Py.Repr(landed)}");

            var falseKey = Item(("pk", S($"{label}-false")));
            var stored = Item(("pk", S($"{label}-false")), ("val", N(falseVal)));
            await ddb.PutItemAsync(table, stored);
            await ExpectError(
                () => ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = table,
                    Item = Item(("pk", S($"{label}-false")), ("val", N(falseVal)), ("applied", Bool(true))),
                    ConditionExpression = $"val {op} :t",
                    ExpressionAttributeValues = Item((":t", N(falseThreshold))),
                }),
                Ccf);
            var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = falseKey, ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(unchanged, stored),
                $"PutItem rejected under 'val {op} :t' ({falseVal} {op} {falseThreshold}) must leave " +
                $"the item unchanged, got {Py.Repr(unchanged)}");
        }
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>Assert fn() raises exactly (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }
}
