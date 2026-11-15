// Translated from behavioral/dynamodb/check_table_arn_accepted_wherever_table_name_is.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.table-arn-accepted-wherever-table-name-is</summary>
[Category("AwsEvaluationSuite")]
public sealed class TableArnAcceptedWhereverTableNameIs
{
    private const string CheckId = "table-arn-accepted-wherever-table-name-is";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// The botocore error code and message <paramref name="fn"/> raises.
    /// An unexpected SUCCESS raises rather than returning None: a responder that ACCEPTS a request real
    /// AWS refuses is the strongest signal this check can produce.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>
    /// TableName accepts a full table ARN as well as a bare name for the same table: items written through one form
    /// read back through the other, while a slashed name and malformed ARNs raise ValidationException with the exact
    /// message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.table-arn-accepted-wherever-table-name-is")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var arn = (await ddb.DescribeTableAsync(t)).Table.TableArn;
        Ensure(arn.EndsWith($"table/{t}", StringComparison.Ordinal), $"the described ARN must name this table, got {Py.Repr(arn)}");
        Ensure(
            arn.Contains(':', StringComparison.Ordinal) && arn.Contains('/', StringComparison.Ordinal),
            $"a table ARN contains characters a bare table name may not, which is the point of this check: {Py.Repr(arn)}");

        // Write by bare name, read by ARN.
        await ddb.PutItemAsync(t, Item(("pk", S("by-name")), ("v", S("written-by-name"))));
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = arn, Key = Item(("pk", S("by-name"))), ConsistentRead = true })).Item ?? [];
        Ensure(Py.Equal(got.GetValueOrDefault("v"), S("written-by-name")), $"an item written by bare name must be readable by ARN, got {Py.Repr(got)}");

        // Write by ARN, read by bare name.
        await ddb.PutItemAsync(arn, Item(("pk", S("by-arn")), ("v", S("written-by-arn"))));
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("by-arn"))), ConsistentRead = true })).Item ?? [];
        Ensure(Py.Equal(got.GetValueOrDefault("v"), S("written-by-arn")), $"an item written by ARN must be readable by bare name, got {Py.Repr(got)}");

        // Query and DescribeTable accept the ARN form too.
        var rows = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = arn,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("by-arn"))),
        })).Items ?? [];
        Ensure(rows.Count == 1, $"Query by ARN must find the item, got {rows.Count} rows");
        Ensure(
            (await ddb.DescribeTableAsync(arn)).Table.TableName == t,
            "DescribeTable by ARN must describe the same table and report its bare name");

        // Accepting an ARN does not mean accepting arbitrary punctuation: a string that
        // is neither a legal bare name nor a well-formed table ARN is still rejected.
        const string pattern = "Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+";
        const string arnFormat =
            "Valid ARN format is 'arn:<awsPartition>:<vendor>:<region>:<subscriber>:"
            + "resourceType/resourceName', where Resource name must have length less than or equal to "
            + "255, Resource name must have length greater than or equal to 3, Resource name must "
            + "satisfy regular expression pattern: [a-zA-Z0-9_.-]+";
        var cases = new[]
        {
            ("bare name with a slash", "not/a/name", pattern),
            ("ARN with an empty service", ReplaceFirst(arn, ":dynamodb:", "::"), arnFormat),
            ("ARN naming a different resource type", ReplaceFirst(arn, "table/", "dummy/"), "Invalid resource type"),
        };
        foreach (var (label, bad, constraint) in cases)
        {
            var (code, message) = await Code(() => ddb.GetItemAsync(new GetItemRequest { TableName = bad, Key = Item(("pk", S("by-arn"))) }));
            Ensure(
                code == ErrValidation,
                $"{label} is neither a legal name nor a well-formed table ARN and must be {ErrValidation}, got {Py.Repr(code)} for {Py.Repr(bad)}");
            var expectedMessage = $"1 validation error detected: Value '{bad}' at 'tableName' failed to satisfy constraint: {constraint}";
            Ensure(message == expectedMessage, $"{label}: expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
        }
    }

    /// <summary><c>s.replace(old, new, 1)</c>.</summary>
    private static string ReplaceFirst(string s, string oldValue, string newValue)
    {
        var at = s.IndexOf(oldValue, StringComparison.Ordinal);
        return at < 0 ? s : string.Concat(s.AsSpan(0, at), newValue, s.AsSpan(at + oldValue.Length));
    }
}
