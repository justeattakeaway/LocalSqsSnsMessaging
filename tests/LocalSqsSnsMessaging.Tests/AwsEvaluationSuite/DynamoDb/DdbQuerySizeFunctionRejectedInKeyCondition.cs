// Translated from behavioral/dynamodb/check_ddb_query_size_function_rejected_in_key_condition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-query-size-function-rejected-in-key-condition</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbQuerySizeFunctionRejectedInKeyCondition
{
    private const string Check = "ddb-query-size-function-rejected-in-key-condition";

    private static readonly (string Code, string Message) NestedOp =
        ("ValidationException", "Invalid KeyConditionExpression: KeyConditionExpressions cannot contain nested operations");

    /// <summary>
    /// A Query KeyConditionExpression refuses size() -- a function call is a nested operation
    /// there -- while size() on a non-key attribute still succeeds in that same Query's
    /// FilterExpression.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-query-size-function-rejected-in-key-condition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbkcs-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("probe")), ("sk", S("ab")), ("tag", S("xy"))));

        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "size(pk) = :n",
                ExpressionAttributeValues = Item((":n", N("5"))),
            }),
            NestedOp.Code,
            NestedOp.Message);

        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ConsistentRead = true,
            FilterExpression = "size(tag) = :n",
            ExpressionAttributeValues = Item((":p", S("probe")), (":n", N("2"))),
        });
        Ensure(
            control.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal).SetEquals(["ab"]),
            $"the same size() shape in a FilterExpression must still select the item, got {Py.Repr(control.Items)}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        Ensure(
            (error.Code, error.Message) == (code, message),
            $"got ({Py.Repr(error.Code)}, {Py.Repr(error.Message)}), expected ({Py.Repr(code)}, {Py.Repr(message)})");
    }
}
