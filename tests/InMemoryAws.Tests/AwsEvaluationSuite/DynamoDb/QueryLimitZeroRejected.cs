// Translated from behavioral/dynamodb/check_query_limit_zero_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-limit-zero-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryLimitZeroRejected
{
    private const string Check = "query-limit-zero-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>Limit=0, held in a field: the SDK's analyzer refuses the constant, as boto3's validation would.</summary>
    private static readonly int ZeroLimit = 0;

    /// <summary>Return (code, message) of the ClientError <paramref name="fn"/> raises; an accepted call fails the check.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// Query and Scan with Limit=0, sent without client-side validation, raise ValidationException with the exact
    /// message, while the same Query and Scan with Limit=1 find the item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-limit-zero-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // Config(parameter_validation=False): the .NET SDK doesn't validate client-side.
        var ddbRaw = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("lz-1"))));
        var queryControl = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("lz-1"))),
            Limit = 1,
        });
        Ensure(queryControl.Count == 1, $"expected positive-control Query(Limit=1) to find the item, got Count={queryControl.Count}");
        var scanControl = await ddb.ScanAsync(new ScanRequest { TableName = t, Limit = 1 });
        Ensure(scanControl.Count == 1, $"expected positive-control Scan(Limit=1) to find the item, got Count={scanControl.Count}");
        const string queryLimitMessage =
            "1 validation error detected: Value at 'Limit' failed to satisfy constraint: " +
            "Member must have value greater than or equal to 1";
        var (queryCode, queryMessage) = await Error(() => ddbRaw.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("lz-1"))),
            Limit = ZeroLimit,
        }));
        Ensure(queryCode == ErrValidation, $"expected {ErrValidation}, got {queryCode}");
        Ensure(queryMessage == queryLimitMessage, $"unexpected Query message {Py.Repr(queryMessage)}");
        var (scanCode, scanMessage) = await Error(() => ddbRaw.ScanAsync(new ScanRequest { TableName = t, Limit = ZeroLimit }));
        Ensure(scanCode == ErrValidation, $"expected {ErrValidation}, got {scanCode}");

        // Scan words the same refusal differently from Query (lowercase member name, quoted value).
        const string scanLimitMessage =
            "1 validation error detected: Value '0' at 'limit' failed to satisfy constraint: " +
            "Member must have value greater than or equal to 1";
        Ensure(scanMessage == scanLimitMessage, $"unexpected Scan message {Py.Repr(scanMessage)}");
    }
}
