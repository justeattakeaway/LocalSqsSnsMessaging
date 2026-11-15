// Translated from behavioral/dynamodb/check_null_false_batchget_mixed_projection_and_query_keyconditions_with_expressions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.null-false-batchget-mixed-projection-and-query-keyconditions-with-expressions</summary>
[Category("AwsEvaluationSuite")]
public sealed class NullFalseBatchgetMixedProjectionAndQueryKeyconditionsWithExpressions
{
    private const string CheckId = "null-false-batchget-mixed-projection-and-query-keyconditions-with-expressions";
    private const string ErrValidation = "ValidationException";

    /// <summary>The Error.Code and Error.Message <paramref name="fn"/> raises. Raises on an unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static Dictionary<string, Condition> PkEq(string value) => new(StringComparer.Ordinal)
    {
        ["pk"] = new Condition { AttributeValueList = [S(value)], ComparisonOperator = ComparisonOperator.EQ },
    };

    /// <summary>
    /// A NULL attribute value of false is rejected; BatchGetItem refuses request-wide when
    /// one table's Keys use ProjectionExpression and another's use AttributesToGet; Query
    /// accepts legacy KeyConditions paired with ProjectionExpression or FilterExpression.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.null-false-batchget-mixed-projection-and-query-keyconditions-with-expressions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t1 = FidelityContext.UniqueName("sim2real-behav-a-");
        var t2 = FidelityContext.UniqueName("sim2real-behav-b-");
        foreach (var t in new[] { t1, t2 })
        {
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = t,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
                Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            });
            ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        }

        foreach (var t in new[] { t1, t2 })
        {
            await ddb.WaitForTableExists(t, delaySeconds: 1, maxAttempts: 60);
        }

        // --- 1. PutItem with {"NULL": false} is rejected; {"NULL": true} is the only legal form.
        var (code, message) = await Error(() => ddb.PutItemAsync(t1, Item(("pk", S("n1")), ("flag", new AttributeValue { NULL = false }))));
        Ensure(code == ErrValidation, $"NULL:false: expected {ErrValidation}, got {code}");
        Ensure(
            message == "1 validation error detected: One or more parameter values were invalid: Null attribute value types must have the value of true",
            $"NULL:false: expected AWS exact message, got {Py.Repr(message)}");
        var absent = (await ddb.GetItemAsync(new GetItemRequest { TableName = t1, Key = Item(("pk", S("n1"))), ConsistentRead = true })).Item;
        Ensure(absent is null, $"a rejected NULL:false PutItem must not have created the item: {Py.Repr(absent)}");
        await ddb.PutItemAsync(t1, Item(("pk", S("n1")), ("flag", Null())));
        var landed = (await ddb.GetItemAsync(new GetItemRequest { TableName = t1, Key = Item(("pk", S("n1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(landed, Item(("pk", S("n1")), ("flag", Null()))), $"positive control NULL:true did not land: {Py.Repr(landed)}");

        // --- 2. BatchGetItem refuses request-wide when one table's Keys use ProjectionExpression
        //        and another's use AttributesToGet -- even though they name different tables.
        await ddb.PutItemAsync(t1, Item(("pk", S("k1")), ("a", S("A")), ("b", S("B"))));
        await ddb.PutItemAsync(t2, Item(("pk", S("k1")), ("c", S("C")), ("d", S("D"))));
        (code, message) = await Error(() => ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [t1] = new KeysAndAttributes { Keys = [Item(("pk", S("k1")))], ProjectionExpression = "a" },
                [t2] = new KeysAndAttributes { Keys = [Item(("pk", S("k1")))], AttributesToGet = ["c"] },
            },
        }));
        Ensure(code == ErrValidation, $"mixed BatchGetItem: expected {ErrValidation}, got {code}");
        Ensure(
            message == "Can not use both expression and non-expression parameters in the same request: Non-expression parameters: {AttributesToGet} Expression parameters: {ProjectionExpression}",
            $"mixed BatchGetItem: expected AWS exact message naming the colliding parameters, got {Py.Repr(message)}");
        // Positive control: ProjectionExpression on both tables' Keys entries is accepted.
        var ok = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [t1] = new KeysAndAttributes { Keys = [Item(("pk", S("k1")))], ProjectionExpression = "a" },
                [t2] = new KeysAndAttributes { Keys = [Item(("pk", S("k1")))], ProjectionExpression = "c" },
            },
        });
        Ensure(Py.Equal(ok.Responses[t1], new[] { Item(("a", S("A"))) }), $"positive control t1 projection wrong: {Py.Repr(ok.Responses[t1])}");
        Ensure(Py.Equal(ok.Responses[t2], new[] { Item(("c", S("C"))) }), $"positive control t2 projection wrong: {Py.Repr(ok.Responses[t2])}");

        // --- 3. Query accepts legacy KeyConditions paired with ProjectionExpression, honouring both.
        var proj = await ddb.QueryAsync(new QueryRequest { TableName = t1, KeyConditions = PkEq("k1"), ProjectionExpression = "a", ConsistentRead = true });
        Ensure(proj.Count == 1, $"KeyConditions+ProjectionExpression: expected 1 match, got {proj.Count}");
        Ensure(Py.Equal(proj.Items, new[] { Item(("a", S("A"))) }), $"KeyConditions+ProjectionExpression did not project to just a: {Py.Repr(proj.Items)}");

        // --- 4. Query accepts legacy KeyConditions paired with FilterExpression, and the filter
        //        actually narrows the key-matched set, not merely accepted and ignored.
        var t3 = FidelityContext.UniqueName("sim2real-behav-c-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t3,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t3));
        await ddb.WaitForTableExists(t3, delaySeconds: 1, maxAttempts: 60);
        for (var i = 0; i < 3; i++)
        {
            await ddb.PutItemAsync(t3, Item(("pk", S("p1")), ("sk", S($"s{i}")), ("tag", S(i == 1 ? "keep" : "drop"))));
        }

        var filt = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t3,
            KeyConditions = PkEq("p1"),
            FilterExpression = "tag = :t",
            ExpressionAttributeValues = Item((":t", S("keep"))),
            ConsistentRead = true,
        });
        Ensure(filt.Count == 1, $"KeyConditions+FilterExpression: expected 1 surviving item, got {filt.Count}");
        Ensure(Py.Equal(filt.Items[0]["sk"], S("s1")), $"KeyConditions+FilterExpression kept the wrong item: {Py.Repr(filt.Items)}");
        Ensure(
            filt.ScannedCount == 3,
            $"KeyConditions+FilterExpression is a post-condition filter: all 3 items under the key must be scanned before the filter drops it to 1, got ScannedCount={Py.Repr(filt.ScannedCount)}");
    }
}
