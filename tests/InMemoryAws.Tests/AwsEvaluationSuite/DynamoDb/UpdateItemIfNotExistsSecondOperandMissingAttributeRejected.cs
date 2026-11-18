// Translated from behavioral/dynamodb/check_update_item_if_not_exists_second_operand_missing_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-if-not-exists-second-operand-missing-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemIfNotExistsSecondOperandMissingAttributeRejected
{
    private const string CheckId = "update-item-if-not-exists-second-operand-missing-attribute-rejected";
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;

    /// <summary>
    /// `SET b = if_not_exists(b, a)` raises ValidationException with the exact message when
    /// both `b` and the document-path operand `a` are absent, and leaves the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-if-not-exists-second-operand-missing-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("row-both-missing")));
        await ddb.PutItemAsync(table, Item(("pk", S("row-both-missing")), ("label", S("orig"))));

        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET b = if_not_exists(b, a)",
        }));
        Ensure(
            code == "ValidationException",
            $"if_not_exists(b, a) with both b and a absent answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            "expected ValidationException");
        Ensure(
            message == "The provided expression refers to an attribute that does not exist in the item",
            $"the refusal's message is {Py.Repr(message)}; real AWS names the missing fallback operand as " +
            "an attribute that does not exist in the item");

        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(item, Item(("pk", S("row-both-missing")), ("label", S("orig")))),
            $"after the refused SET the item is {Py.Repr(item)}; a refused UpdateItem must change nothing");

        // Positive control: the same expression succeeds once the fallback operand `a` exists.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET a = :v",
            ExpressionAttributeValues = Item((":v", S("fallback-value"))),
        });
        var ok = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET b = if_not_exists(b, a)",
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(ok, Item(("pk", S("row-both-missing")), ("label", S("orig")), ("a", S("fallback-value")), ("b", S("fallback-value")))),
            $"if_not_exists(b, a) with a present returned {Py.Repr(ok)}, expected b copied from a");
    }

    /// <summary>(code, message) of the error fn() raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"call unexpectedly SUCCEEDED where an error was required: {Py.Repr(resp)}")
            : (error.Code, error.Message);
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await ddb.DeleteTableAsync(tableName);
                return;
            }
            catch (Amazon.Runtime.AmazonServiceException exc)
            {
                if (exc.ErrorCode == "ResourceNotFoundException")
                {
                    return;
                }

                if (exc.ErrorCode != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= TeardownWaitS)
                {
                    throw new InvalidOperationException($"dynamodb table {Py.Repr(tableName)} could not be deleted: {exc.ErrorCode}", exc);
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetS)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {TableActiveBudgetS}s: {Py.Repr(status)}");
    }
}
