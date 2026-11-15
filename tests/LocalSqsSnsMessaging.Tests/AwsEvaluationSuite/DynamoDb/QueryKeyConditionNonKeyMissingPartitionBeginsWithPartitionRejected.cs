// Translated from behavioral/dynamodb/check_query_key_condition_non_key_missing_partition_begins_with_partition_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-non-key-missing-partition-begins-with-partition-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionNonKeyMissingPartitionBeginsWithPartitionRejected
{
    private const string Check = "query-key-condition-non-key-missing-partition-begins-with-partition-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The (code, message) of the ClientError fn() raises. RAISES when fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A KeyConditionExpression naming a non-key attribute, omitting the partition key, or using
    /// begins_with on the partition key is rejected with its own exact message; legal Queries work.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-non-key-missing-partition-begins-with-partition-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-qkc-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (sk, extra) in new[] { ("a", "1"), ("b", "2") })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S(sk)), ("extra", S(extra))));
        }

        // (1) `extra` is a real attribute on every item but is not part of any key schema; it is
        // ANDed onto a correct partition-key condition so the rejection is attributable to `extra`
        // standing in for the sort key, not to a missing partition key (case 2, below).
        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND extra = :v",
            ExpressionAttributeValues = Item((":p", S("p")), (":v", S("1"))),
        }));
        Ensure(code == ErrValidation, $"non-key attribute in KeyConditionExpression answered {Py.Repr(code)} ({Py.Repr(message)})");
        const string expectedNonkey = "Query condition missed key schema element: sk";
        Ensure(message == expectedNonkey, $"expected message {Py.Repr(expectedNonkey)}, got {Py.Repr(message)}");

        // (2) The partition key `pk` is never mentioned; only the sort key is.
        (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "sk = :v",
            ExpressionAttributeValues = Item((":v", S("a"))),
        }));
        Ensure(code == ErrValidation, $"missing partition key in KeyConditionExpression answered {Py.Repr(code)} ({Py.Repr(message)})");
        const string expectedMissingPk = "Query condition missed key schema element: pk";
        Ensure(message == expectedMissingPk, $"expected message {Py.Repr(expectedMissingPk)}, got {Py.Repr(message)}");

        // Query legally in between the two remaining malformations, to show neither rejection left
        // the table or the access path broken.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
            ConsistentRead = true,
        });
        Ensure(control.Count == 2, $"legal Query mid-sequence matched {Py.Repr(control.Count)}, expected 2");

        // (3) `begins_with` is legal on a sort key, never on the partition key.
        (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "begins_with(pk, :v)",
            ExpressionAttributeValues = Item((":v", S("p"))),
        }));
        Ensure(code == ErrValidation, $"begins_with on the partition key answered {Py.Repr(code)} ({Py.Repr(message)})");
        const string expectedBeginsWithPk = "Query key condition not supported";
        Ensure(message == expectedBeginsWithPk, $"expected message {Py.Repr(expectedBeginsWithPk)}, got {Py.Repr(message)}");

        // Positive control: the legal form -- partition key equality ANDed with begins_with on the
        // sort key -- still works after all three rejections.
        var final = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND begins_with(sk, :s)",
            ExpressionAttributeValues = Item((":p", S("p")), (":s", S("a"))),
            ConsistentRead = true,
        });
        var got = final.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(got.SequenceEqual(["a"]), $"begins_with on the sort key returned sk values {Py.Repr(got)}, expected ['a']");
    }
}
