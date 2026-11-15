// Translated from behavioral/dynamodb/check_update_item_if_not_exists_document_path_fallback_and_existing_subtract.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-if-not-exists-document-path-fallback-and-existing-subtract</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemIfNotExistsDocumentPathFallbackAndExistingSubtract
{
    private const string CheckId = "update-item-if-not-exists-document-path-fallback-and-existing-subtract";
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;

    /// <summary>
    /// `SET b = if_not_exists(b, a)` copies a document path's value, key attribute included,
    /// into an absent `b` and leaves a present `b` untouched; `SET v = if_not_exists(v, :d) - :amt`
    /// subtracts from the existing `v`, ignoring the default, which only an absent `v` falls back to.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-if-not-exists-document-path-fallback-and-existing-subtract")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        // b absent: if_not_exists(b, a) copies the value at document path `a`.
        var key1 = Item(("pk", S("row-copy-attr")));
        await ddb.PutItemAsync(table, Item(("pk", S("row-copy-attr")), ("a", S("source-value"))));
        var r1 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key1,
            UpdateExpression = "SET b = if_not_exists(b, a)",
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(r1, Item(("pk", S("row-copy-attr")), ("a", S("source-value")), ("b", S("source-value")))),
            $"if_not_exists(b, a) with b absent returned {Py.Repr(r1)}, expected b copied from a");

        // b now present: a second if_not_exists(b, a) keeps b, even after `a` changes.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key1,
            UpdateExpression = "SET a = :changed",
            ExpressionAttributeValues = Item((":changed", S("changed-value"))),
        });
        var r2 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key1,
            UpdateExpression = "SET b = if_not_exists(b, a)",
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(r2, Item(("pk", S("row-copy-attr")), ("a", S("changed-value")), ("b", S("source-value")))),
            $"if_not_exists(b, a) with b already present returned {Py.Repr(r2)}, expected b to stay " +
            "'source-value' and not be overwritten by the changed 'a'");

        // b absent, operand is the key attribute itself: if_not_exists(b, pk) copies the key's value.
        var key2 = Item(("pk", S("row-copy-key")));
        await ddb.PutItemAsync(table, Item(("pk", S("row-copy-key"))));
        var r3 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key2,
            UpdateExpression = "SET b = if_not_exists(b, pk)",
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(r3, Item(("pk", S("row-copy-key")), ("b", S("row-copy-key")))),
            $"if_not_exists(b, pk) with b absent returned {Py.Repr(r3)}, expected b copied from the key " +
            "attribute pk");

        // v present: if_not_exists(v, :d) - :amt subtracts :amt from the EXISTING v, ignoring :d.
        var key3 = Item(("pk", S("row-subtract-existing")));
        await ddb.PutItemAsync(table, Item(("pk", S("row-subtract-existing")), ("v", N("10"))));
        var r4 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key3,
            UpdateExpression = "SET v = if_not_exists(v, :d) - :amt",
            ExpressionAttributeValues = Item((":d", N("100")), (":amt", N("3"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(r4, Item(("pk", S("row-subtract-existing")), ("v", N("7")))),
            $"if_not_exists(v, :d) - :amt with v=10 stored returned {Py.Repr(r4)}, expected v=7 " +
            "(10 - 3, the default 100 must be ignored)");

        // Contrast: v absent falls back to the default itself, then subtracts :amt from it.
        var key4 = Item(("pk", S("row-subtract-default")));
        await ddb.PutItemAsync(table, Item(("pk", S("row-subtract-default"))));
        var r5 = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key4,
            UpdateExpression = "SET v = if_not_exists(v, :d) - :amt",
            ExpressionAttributeValues = Item((":d", N("100")), (":amt", N("3"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(r5, Item(("pk", S("row-subtract-default")), ("v", N("97")))),
            $"if_not_exists(v, :d) - :amt with v absent returned {Py.Repr(r5)}, expected v=97 " +
            "(100 - 3, the default standing in for the missing v)");

        var final1 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key1, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final1, Item(("pk", S("row-copy-attr")), ("a", S("changed-value")), ("b", S("source-value")))),
            $"final stored row-copy-attr item is {Py.Repr(final1)}");
        var final3 = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key3, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final3, Item(("pk", S("row-subtract-existing")), ("v", N("7")))),
            $"final stored row-subtract-existing item is {Py.Repr(final3)}");
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
