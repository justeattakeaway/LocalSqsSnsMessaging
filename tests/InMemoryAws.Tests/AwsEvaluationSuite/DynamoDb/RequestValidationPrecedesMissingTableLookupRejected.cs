// Translated from behavioral/dynamodb/check_request_validation_precedes_missing_table_lookup_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.request-validation-precedes-missing-table-lookup-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RequestValidationPrecedesMissingTableLookupRejected
{
    private static readonly (string? Code, string? Message) NotFound = ("ResourceNotFoundException", "Requested resource not found");

    private const string ExpectedEqNoValueMessage =
        "1 validation error detected: One or more parameter values were invalid: " +
        "Value or AttributeValueList must be used with ComparisonOperator: EQ for Attribute: a";

    private const string ProjectionEmptyMessage = "Invalid ProjectionExpression: The expression can not be empty;";
    private const string ProjectionSyntaxMessage = "Invalid ProjectionExpression: Syntax error; token: \".\", near: \"..b\"";

    private const string ProjectionOverlapMessage =
        "Invalid ProjectionExpression: Two document paths overlap with each other; " +
        "must remove or rewrite one of these paths; path one: [a], path two: [a, b]";

    private const string UpdateEmptyMessage = "1 validation error detected: Invalid UpdateExpression: The expression can not be empty;";
    private const string UpdateSyntaxMessage = "1 validation error detected: Invalid UpdateExpression: Syntax error; token: \"<EOF>\", near: \"SET\"";

    private const string UpdateReservedMessage =
        "1 validation error detected: Invalid UpdateExpression: Attribute name is a reserved keyword; " +
        "reserved keyword: status";

    private const string UpdateUndefinedNameMessage =
        "1 validation error detected: Invalid UpdateExpression: An expression attribute name used in " +
        "the document path is not defined; attribute name: #x";

    private const string FilterSyntaxMessage = "Invalid FilterExpression: Syntax error; token: \"+\", near: \"a +\"";
    private const string BatchDuplicateKeysMessage = "Provided list of item keys contains duplicates";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
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
    /// On a table that does not exist, malformed Expected, projection, update, filter and duplicate
    /// batch keys are ValidationException, while the same requests well formed are ResourceNotFound.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Programming.Errors.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.request-validation-precedes-missing-table-lookup-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-absent-");
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("k")));

        async Task Check<T>(string label, Func<Task<T>> fn, (string? Code, string? Message) want)
            where T : class
        {
            var (code, msg) = await Error(fn);
            Ensure((code, msg) == want, $"{label} on the missing table gave {Py.Repr(code)}: {Py.Repr(msg)}, expected {Py.Repr(want)}");
        }

        Dictionary<string, ExpectedAttributeValue> NoValue() => new() { ["a"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.EQ } };
        Dictionary<string, ExpectedAttributeValue> OneValue() => new()
        {
            ["a"] = new ExpectedAttributeValue { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S("x")] },
        };

        await Check(
            "PutItem with Expected EQ and no value",
            () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Key(), Expected = NoValue() }),
            ("ValidationException", ExpectedEqNoValueMessage));
        await Check(
            "PutItem with Expected EQ and one value",
            () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Key(), Expected = OneValue() }),
            NotFound);
        await Check(
            "DeleteItem with Expected EQ and no value",
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), Expected = NoValue() }),
            ("ValidationException", ExpectedEqNoValueMessage));
        await Check(
            "DeleteItem with Expected EQ and one value",
            () => ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = Key(), Expected = OneValue() }),
            NotFound);
        foreach (var (projection, message) in new[]
        {
            ("", ProjectionEmptyMessage),
            ("a..b", ProjectionSyntaxMessage),
            ("a, a.b", ProjectionOverlapMessage),
        })
        {
            await Check(
                $"GetItem with ProjectionExpression {Py.Repr(projection)}",
                () => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ProjectionExpression = projection }),
                ("ValidationException", message));
        }

        await Check(
            "GetItem with ProjectionExpression 'a, b'",
            () => ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ProjectionExpression = "a, b" }),
            NotFound);
        Dictionary<string, AttributeValue> Value() => Item((":v", S("x")));
        foreach (var (expression, message) in new[]
        {
            ("", UpdateEmptyMessage),
            ("SET", UpdateSyntaxMessage),
            ("SET status = :v", UpdateReservedMessage),
            ("SET #x = :v", UpdateUndefinedNameMessage),
        })
        {
            var withValues = expression is not ("" or "SET");
            await Check(
                $"UpdateItem with UpdateExpression {Py.Repr(expression)}",
                () => ddb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = Key(),
                    UpdateExpression = expression,
                    ExpressionAttributeValues = withValues ? Value() : null,
                }),
                ("ValidationException", message));
        }

        await Check(
            "UpdateItem with UpdateExpression 'SET #s = :v'",
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET #s = :v",
                ExpressionAttributeNames = Names(("#s", "status")),
                ExpressionAttributeValues = Value(),
            }),
            NotFound);
        await Check(
            "Scan with FilterExpression 'a +'",
            () => ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = "a +" }),
            ("ValidationException", FilterSyntaxMessage));
        await Check(
            "Scan with FilterExpression 'attribute_exists(a)'",
            () => ddb.ScanAsync(new ScanRequest { TableName = table, FilterExpression = "attribute_exists(a)" }),
            NotFound);
        await Check(
            "BatchGetItem with a duplicated key",
            () => ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new() { [table] = new KeysAndAttributes { Keys = [Key(), Key()] } },
            }),
            ("ValidationException", BatchDuplicateKeysMessage));
        await Check(
            "BatchGetItem with distinct keys",
            () => ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new() { [table] = new KeysAndAttributes { Keys = [Key(), Item(("pk", S("k2")))] } },
            }),
            NotFound);
        var (code, msg) = await Error(() => ddb.DescribeTableAsync(table));
        (string? Code, string? Message) want = ("ResourceNotFoundException", $"Requested resource not found: Table: {table} not found");
        Ensure((code, msg) == want, $"after the requests the table {table} describes as {Py.Repr(code)}: {Py.Repr(msg)}");
    }
}
