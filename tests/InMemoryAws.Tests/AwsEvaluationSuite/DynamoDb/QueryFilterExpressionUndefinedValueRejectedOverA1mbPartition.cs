// Translated from behavioral/dynamodb/check_query_filter_expression_undefined_value_rejected_over_a_1mb_partition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-undefined-value-rejected-over-a-1mb-partition</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionUndefinedValueRejectedOverA1mbPartition
{
    private const string Check = "query-filter-expression-undefined-value-rejected-over-a-1mb-partition";
    private const int ItemBytes = 50 * 1024;
    private const int ItemCount = 40;

    /// <summary>The (code, message) <paramref name="fn"/> is refused with. An unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// An undefined expression attribute value in Query's FilterExpression is a ValidationException
    /// even over a partition exceeding the 1 MB page limit, so the expression is parsed before any
    /// page is produced rather than failing only once the budget is reached.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-undefined-value-rejected-over-a-1mb-partition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-qfundef-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var payload = new string('x', ItemBytes);
        for (var i = 0; i < ItemCount; i++)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("big")), ("sk", S(i.ToString("D3", CultureInfo.InvariantCulture))), ("payload", S(payload))));
        }

        QueryRequest Base(string filterExpression) => new()
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("big"))),
            ConsistentRead = true,
            FilterExpression = filterExpression,
        };

        // Positive control: a well-formed never-matching filter over the same > 1 MB partition
        // truncates normally, proving the partition is large enough for the budget to apply at all.
        var control = await ddb.QueryAsync(Base("attribute_exists(no_such_attr)"));
        Ensure(
            control.Count == 0 && control.Items.Count == 0,
            $"control filter must match nothing, got Count={control.Count} Items={Py.Repr(control.Items)}");
        Ensure(control.LastEvaluatedKey is not null, "the control page must be truncated by the 1 MB budget and carry a LastEvaluatedKey");

        // The broken call: FilterExpression names :missing_tag, never defined in
        // ExpressionAttributeValues, over the same > 1 MB partition.
        var (code, message) = await Error(() => ddb.QueryAsync(Base("tag = :missing_tag")));
        Ensure(
            code == "ValidationException",
            $"an undefined expression attribute value in FilterExpression over a > 1 MB partition gave {Py.Repr(code)}, expected ValidationException");
        const string expected =
            "Invalid FilterExpression: An expression attribute value used in expression is not " +
            "defined; attribute value: :missing_tag";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }
}
