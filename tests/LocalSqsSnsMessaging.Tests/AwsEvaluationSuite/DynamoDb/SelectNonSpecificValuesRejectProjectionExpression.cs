// Translated from behavioral/dynamodb/check_select_non_specific_values_reject_projection_expression.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.select-non-specific-values-reject-projection-expression</summary>
[Category("AwsEvaluationSuite")]
public sealed class SelectNonSpecificValuesRejectProjectionExpression
{
    private const string CheckId = "select-non-specific-values-reject-projection-expression";
    private const string ErrValidation = "ValidationException";

    // The Select value a request names, and the exact AWS message for pairing it with
    // ProjectionExpression (only SPECIFIC_ATTRIBUTES may be).
    private static readonly (string Select, string Message)[] Rejected =
    [
        ("ALL_ATTRIBUTES", "1 validation error detected: Cannot specify the ProjectionExpression when choosing to get ALL_ATTRIBUTES"),
        ("COUNT", "1 validation error detected: Cannot specify the ProjectionExpression when choosing to get only the Count"),
        ("ALL_PROJECTED_ATTRIBUTES", "1 validation error detected: Cannot specify the ProjectionExpression when choosing to get ALL_PROJECTED_ATTRIBUTES"),
    ];

    /// <summary>A wait's timeout: capS, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>The error code and message <paramref name="fn"/> raises; an unexpected success fails loudly instead.</summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// Query and Scan refuse ALL_ATTRIBUTES, COUNT and ALL_PROJECTED_ATTRIBUTES paired
    /// with a ProjectionExpression: only SPECIFIC_ATTRIBUTES may carry one.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Query.html#DDB-Query-request-Select
    /// </summary>
    [Test]
    [DisplayName("dynamodb.select-non-specific-values-reject-projection-expression")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-select-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        const int delay = 2;
        var budgetS = BudgetS(ctx, 120.0);
        await ddb.WaitForTableExists(table, delaySeconds: delay, maxAttempts: Math.Max(1, (int)(budgetS / delay)));

        var item = Item(("pk", S("P")), ("sk", S("S")), ("body", S("payload")));
        await ddb.PutItemAsync(table, item);

        QueryRequest Query(Select select, string projection) => new()
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("P"))),
            ConsistentRead = true,
            Select = select,
            ProjectionExpression = projection,
        };
        ScanRequest Scan(Select select, string projection) => new()
        {
            TableName = table,
            ConsistentRead = true,
            Select = select,
            ProjectionExpression = projection,
        };

        foreach (var (select, message) in Rejected)
        {
            var (code, msg) = await Code(() => ddb.QueryAsync(Query(new Select(select), "body")));
            Ensure(code == ErrValidation, $"Query Select={select} with a ProjectionExpression must be refused with {ErrValidation}; got {code}");
            Ensure(msg == message, $"Query Select={select}: expected AWS message {Py.Repr(message)}, got {Py.Repr(msg)}");
            (code, msg) = await Code(() => ddb.ScanAsync(Scan(new Select(select), "body")));
            Ensure(code == ErrValidation, $"Scan Select={select} with a ProjectionExpression must be refused with {ErrValidation}; got {code}");
            Ensure(msg == message, $"Scan Select={select}: expected AWS message {Py.Repr(message)}, got {Py.Repr(msg)}");
        }

        // Positive control: the same request, legally paired (SPECIFIC_ATTRIBUTES + ProjectionExpression),
        // still succeeds through both operations.
        var queryResp = await ddb.QueryAsync(Query(Select.SPECIFIC_ATTRIBUTES, "body"));
        Ensure(
            Py.Equal(queryResp.Items, new[] { Item(("body", S("payload"))) }),
            $"legal Query Select=SPECIFIC_ATTRIBUTES + ProjectionExpression should still work, got {Py.Repr(queryResp.Items)}");
        var scanResp = await ddb.ScanAsync(Scan(Select.SPECIFIC_ATTRIBUTES, "body"));
        Ensure(
            Py.Equal(scanResp.Items, new[] { Item(("body", S("payload"))) }),
            $"legal Scan Select=SPECIFIC_ATTRIBUTES + ProjectionExpression should still work, got {Py.Repr(scanResp.Items)}");

        // None of the six rejected calls wrote, deleted or altered anything: the table still holds
        // exactly the one item this check put, unchanged.
        var final = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            Py.Equal(final.Items, new[] { item }),
            $"the six rejected Select+ProjectionExpression combinations must have no side effect; table holds {Py.Repr(final.Items)}");
    }
}
