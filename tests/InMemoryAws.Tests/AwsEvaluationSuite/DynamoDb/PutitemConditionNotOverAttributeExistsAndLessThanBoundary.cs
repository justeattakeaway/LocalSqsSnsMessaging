// Translated from behavioral/dynamodb/check_putitem_condition_not_over_attribute_exists_and_less_than_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-not-over-attribute-exists-and-less-than-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionNotOverAttributeExistsAndLessThanBoundary
{
    private const string Check = "putitem-condition-not-over-attribute-exists-and-less-than-boundary";

    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// PutItem admits a write under `NOT attribute_exists(#m)` while #m is absent, and under
    /// `NOT (#n &lt; :seven)` while #n equals 7; each is refused once the negated operand turns true,
    /// leaving the stored item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ConditionExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-not-over-attribute-exists-and-less-than-boundary")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-put-not-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Read(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

        Task<PutItemResponse> PutNotExists(Dictionary<string, AttributeValue> item) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = item,
                ConditionExpression = "NOT attribute_exists(#m)",
                ExpressionAttributeNames = Names(("#m", "m")),
            });

        Task<PutItemResponse> PutNotLt(Dictionary<string, AttributeValue> item) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = item,
                ConditionExpression = "NOT (#n < :seven)",
                ExpressionAttributeNames = Names(("#n", "n")),
                ExpressionAttributeValues = Item((":seven", N("7"))),
            });

        // --- NOT attribute_exists: holds while #m is absent, fails once #m is present.
        const string existsKey = "ddb-put-not-exists";
        var seeded = Item(("pk", S(existsKey)), ("tag", S("seed")));
        await ddb.PutItemAsync(table, seeded);

        var holding = Item(("pk", S(existsKey)), ("tag", S("not-exists-holds")));
        await PutNotExists(holding);
        var got = await Read(existsKey);
        Ensure(Py.Equal(got, holding), $"PutItem under NOT attribute_exists(absent) left {Py.Repr(got)}, expected {Py.Repr(holding)}");

        // Make #m present, outside the tested condition, so the next attempt breaks exactly one thing.
        var present = Item(("pk", S(existsKey)), ("tag", S("m-now-present")), ("m", S("here")));
        await ddb.PutItemAsync(table, present);

        var rejectedWrite = Item(("pk", S(existsKey)), ("tag", S("not-exists-should-not-apply")));
        await ExpectError(() => PutNotExists(rejectedWrite), Ccf);
        got = await Read(existsKey);
        Ensure(Py.Equal(got, present), $"PutItem under a failing NOT attribute_exists(#m) left {Py.Repr(got)}, expected {Py.Repr(present)} unchanged");

        // --- NOT (#n < :seven): holds exactly at the boundary n == 7, fails once n < 7.
        const string ltKey = "ddb-put-not-lt-boundary";
        var seven = Item(("pk", S(ltKey)), ("n", N("7")), ("tag", S("seed")));
        await ddb.PutItemAsync(table, seven);

        var holdingLt = Item(("pk", S(ltKey)), ("n", N("7")), ("tag", S("not-lt-holds-at-boundary")));
        await PutNotLt(holdingLt);
        got = await Read(ltKey);
        Ensure(Py.Equal(got, holdingLt), $"PutItem under NOT (#n < 7) with n==7 left {Py.Repr(got)}, expected {Py.Repr(holdingLt)}");

        // Drop below the boundary, outside the tested condition.
        var six = Item(("pk", S(ltKey)), ("n", N("6")), ("tag", S("n-now-below-boundary")));
        await ddb.PutItemAsync(table, six);

        var rejectedLt = Item(("pk", S(ltKey)), ("n", N("6")), ("tag", S("not-lt-should-not-apply")));
        await ExpectError(() => PutNotLt(rejectedLt), Ccf);
        got = await Read(ltKey);
        Ensure(Py.Equal(got, six), $"PutItem under a failing NOT (#n < 7) with n==6 left {Py.Repr(got)}, expected {Py.Repr(six)} unchanged");
    }

    /// <summary>Assert <c>fn</c> raises exactly <c>(code, message)</c>. RAISES on an unexpected success.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it SUCCEEDED: {Py.Repr(result)}");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
