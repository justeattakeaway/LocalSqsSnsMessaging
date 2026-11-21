// Translated from behavioral/dynamodb/check_partiql_update_where_and_updateitem_condition_expression_agree_on_equal_and_not_equal_for_sets_and_null.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-where-and-updateitem-condition-expression-agree-on-equal-and-not-equal-for-sets-and-null</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateWhereAndUpdateitemConditionExpressionAgreeOnEqualAndNotEqualForSetsAndNull
{
    private const string CheckId = "partiql-update-where-and-updateitem-condition-expression-agree-on-equal-and-not-equal-for-sets-and-null";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    private static byte[] Bin(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Each case's stored value, an operand equal to it and one that differs (built fresh per request).</summary>
    private static readonly (string Name, Func<AttributeValue> Stored, Func<AttributeValue> Match, Func<AttributeValue> Mismatch)[] Cases =
    [
        ("bs", () => BS(Bin("aa"), Bin("bb")), () => BS(Bin("bb"), Bin("aa")), () => BS(Bin("aa"), Bin("cc"))),
        ("ns", () => NS("1", "2"), () => NS("2", "1"), () => NS("1", "3")),
        ("ss", () => SS("a", "b"), () => SS("b", "a"), () => SS("a", "c")),
        ("null", Null, Null, () => S("not-null")),
    ];

    /// <summary>Assert <paramref name="fn"/> is refused with the exact (code, message) pair.</summary>
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

    /// <summary>
    /// An UpdateItem ConditionExpression and a PartiQL UPDATE ... WHERE clause agree on '=' and
    /// '&lt;&gt;' over a Binary Set, a Number Set, a String Set and NULL; each refusal is
    /// ConditionalCheckFailedException, leaving the item exactly as the prior write left it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-where-and-updateitem-condition-expression-agree-on-equal-and-not-equal-for-sets-and-null")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlcce-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 60);

        foreach (var (name, stored, match, mismatch) in Cases)
        {
            var pk = $"{name}-item";
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("val", stored())));

            async Task<Dictionary<string, AttributeValue>> Get() =>
                (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

            Task<UpdateItemResponse> UpdateItem(string step, string value, string condition, AttributeValue operand) =>
                ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = Item(("pk", S(pk))),
                    UpdateExpression = $"SET {step} = :s",
                    ConditionExpression = condition,
                    ExpressionAttributeValues = Item((":s", S(value)), (":v", operand)),
                });

            Task<ExecuteStatementResponse> Statement(string step, string value, string op, AttributeValue operand) =>
                ddb.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = $"UPDATE \"{table}\" SET {step} = ? WHERE pk = ? AND val {op} ?",
                    Parameters = [S(value), S(pk), operand],
                });

            // UpdateItem ConditionExpression '=' with the matching operand applies.
            await UpdateItem("step1", "ui-eq-match", "val = :v", match());
            var item = await Get();
            Ensure(
                Py.Equal(item.GetValueOrDefault("step1"), S("ui-eq-match")),
                $"{name}: UpdateItem ConditionExpression 'val = :v' with the matching operand must apply, got {Py.Repr(item)}");

            // The same '=' as a PartiQL UPDATE ... WHERE condition applies too (agreement).
            await Statement("step2", "pql-eq-match", "=", match());
            item = await Get();
            Ensure(
                Py.Equal(item.GetValueOrDefault("step2"), S("pql-eq-match")),
                $"{name}: PartiQL UPDATE ... WHERE val = ? with the matching operand must apply, got {Py.Repr(item)}");

            // UpdateItem ConditionExpression '<>' with the same matching operand is refused: val is
            // not '<>' a value it equals.
            var before = item;
            await ExpectError(() => UpdateItem("step3", "should-not-apply", "val <> :v", match()), Ccf);
            item = await Get();
            Ensure(Py.Equal(item, before), $"{name}: a refused UpdateItem ConditionExpression '<>' must leave the item unchanged, got {Py.Repr(item)}");

            // The same '<>' as a PartiQL UPDATE ... WHERE condition is refused the same way (agreement).
            await ExpectError(() => Statement("step3", "should-not-apply", "<>", match()), Ccf);
            item = await Get();
            Ensure(Py.Equal(item, before), $"{name}: a refused PartiQL UPDATE ... WHERE '<>' must leave the item unchanged, got {Py.Repr(item)}");

            // UpdateItem ConditionExpression '<>' with a differing operand applies.
            await UpdateItem("step4", "ui-ne-mismatch", "val <> :v", mismatch());
            item = await Get();
            Ensure(
                Py.Equal(item.GetValueOrDefault("step4"), S("ui-ne-mismatch")),
                $"{name}: UpdateItem ConditionExpression 'val <> :v' with a differing operand must apply, got {Py.Repr(item)}");

            // The same '<>' as a PartiQL UPDATE ... WHERE condition applies too (agreement).
            await Statement("step5", "pql-ne-mismatch", "<>", mismatch());
            item = await Get();
            Ensure(
                Py.Equal(item.GetValueOrDefault("step5"), S("pql-ne-mismatch")),
                $"{name}: PartiQL UPDATE ... WHERE val <> ? with a differing operand must apply, got {Py.Repr(item)}");

            // A mismatched '=' is refused on both surfaces and leaves the item exactly as the prior
            // successful write left it.
            before = item;
            await ExpectError(() => UpdateItem("step6", "should-not-apply", "val = :v", mismatch()), Ccf);
            item = await Get();
            Ensure(Py.Equal(item, before), $"{name}: a refused UpdateItem ConditionExpression '=' must leave the item unchanged, got {Py.Repr(item)}");

            await ExpectError(() => Statement("step6", "should-not-apply", "=", mismatch()), Ccf);
            item = await Get();
            Ensure(Py.Equal(item, before), $"{name}: a refused PartiQL UPDATE ... WHERE '=' must leave the item unchanged, got {Py.Repr(item)}");
        }
    }
}
