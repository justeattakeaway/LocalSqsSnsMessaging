// Translated from behavioral/dynamodb/check_string_revision_token_conditional_writes_refuse_stale_and_unissued_tokens.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.string-revision-token-conditional-writes-refuse-stale-and-unissued-tokens</summary>
[Category("AwsEvaluationSuite")]
public sealed class StringRevisionTokenConditionalWritesRefuseStaleAndUnissuedTokens
{
    private const string CheckId = "string-revision-token-conditional-writes-refuse-stale-and-unissued-tokens";
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";
    private const string CcfMessage = "The conditional request failed";

    /// <summary>Bound on each wait; a target that needs longer fails with the last answer it gave.</summary>
    private const double WaitSeconds = 40.0;

    private static readonly string[] CcfCodes = [ErrConditionalCheckFailed];

    /// <summary>
    /// On a composite-key item guarded by a string revision token, a put-if-absent of an existing key,
    /// a stale token, a never-issued token and a current token with a mismatched status are each refused
    /// and change nothing, while the current token rotates the revision and the payload.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.string-revision-token-conditional-writes-refuse-stale-and-unissued-tokens")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("tenant", KeyType.HASH), new KeySchemaElement("doc", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("tenant", ScalarAttributeType.S), new AttributeDefinition("doc", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, table));
        await WaitTableActive(ddb, table);

        Dictionary<string, AttributeValue> Key() => Item(("tenant", S("acme")), ("doc", N("907")));
        var rev1 = FidelityContext.UniqueName("rev1-");
        var rev2 = FidelityContext.UniqueName("rev2-");
        var rev3 = FidelityContext.UniqueName("rev3-");
        var first = With(Key(), ("rev", S(rev1)), ("status", S("open")), ("body", S("draft")));
        const string putIfAbsent = "attribute_not_exists(tenant)";
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = first, ConditionExpression = putIfAbsent });
        var exc = await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = With(Key(), ("rev", S("other")), ("body", S("clobber"))),
                ConditionExpression = putIfAbsent,
            }),
            CcfCodes);
        Ensure(exc.Message == CcfMessage, Py.Repr(ErrorDict(exc)));
        await AssertItem(ddb, table, Key(), first, "the refused put-if-absent");

        async Task<Dictionary<string, AttributeValue>> Rotate(string expectedRev, string newRev, string body, string status = "open") =>
            (await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET rev = :new, body = :body",
                ConditionExpression = "rev = :expected AND #st = :status",
                ExpressionAttributeNames = Names(("#st", "status")),
                ExpressionAttributeValues = Item(
                    (":new", S(newRev)), (":body", S(body)), (":expected", S(expectedRev)), (":status", S(status))),
                ReturnValues = ReturnValue.ALL_NEW,
            })).Attributes;

        var second = With(first, ("rev", S(rev2)), ("body", S("reviewed")));
        Ensure(Py.Equal(await Rotate(rev1, rev2, "reviewed"), second), "the current token did not rotate the revision");

        foreach (var (label, token, status) in new[] { ("stale", rev1, "open"), ("never-issued", rev3, "open"), ("current token, wrong status", rev2, "closed") })
        {
            exc = await ExpectError(() => Rotate(token, rev3, "lost", status), CcfCodes);
            var message = exc.Message;
            Ensure(message == CcfMessage, $"the {label} update: got the message {Py.Repr(message)}");
            await AssertItem(ddb, table, Key(), second, $"the refused {label} update");
        }

        var third = With(second, ("rev", S(rev3)), ("body", S("final")));
        Ensure(Py.Equal(await Rotate(rev2, rev3, "final"), third), "the current token did not rotate a second time");
        await AssertItem(ddb, table, Key(), third, "the second rotation");
        // A sibling key in the same partition was never written by any of the refused calls.
        var other = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("tenant", S("acme")), ("doc", N("908"))),
            ConsistentRead = true,
        });
        Ensure(other.Item is null, $"an unrelated key appeared: {Py.Repr(other.Item)}");
    }

    /// <summary><c>{**base, 'k': v, ...}</c>.</summary>
    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> source, params (string Name, AttributeValue Value)[] entries)
    {
        var copy = new Dictionary<string, AttributeValue>(source, StringComparer.Ordinal);
        foreach (var (name, value) in entries)
        {
            copy[name] = value;
        }

        return copy;
    }

    private static Dictionary<string, object?> ErrorDict(AwsError error) => new() { ["Message"] = error.Message, ["Code"] = error.Code };

    private static async Task AssertItem(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key, Dictionary<string, AttributeValue> want, string after)
    {
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, want), $"after {after}: expected {Py.Repr(want)}, got {Py.Repr(got)}");
    }

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Grade a ClientError on code, HTTP status class and a non-empty message.</summary>
    private static void AssertError(AwsError error, string[] codes, int status = 400)
    {
        var code = error.Code;
        var msg = error.Message ?? "";
        var gotStatus = (int)error.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(codes)}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(gotStatus / 100 == status / 100, $"expected HTTP {status / 100}xx with {code}, got {gotStatus}");
        Ensure(!string.IsNullOrWhiteSpace(msg), $"expected a non-empty message with {code}");
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string[] codes, int status = 400)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            AssertError(error, codes, status);
            return error;
        }

        throw Fail($"expected {Py.Repr(codes)}, but the call succeeded: {result}");
    }

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = WaitSeconds)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw Fail($"table {table} not ACTIVE within {(int)timeoutSeconds}s; last TableStatus {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }
}
