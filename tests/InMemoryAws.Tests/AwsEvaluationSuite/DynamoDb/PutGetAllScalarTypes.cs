// Translated from behavioral/dynamodb/check_put_get_all_scalar_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-get-all-scalar-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetAllScalarTypes
{
    private const string CheckId = "put-get-all-scalar-types";

    /// <summary>PutItem/GetItem round-trips all scalar types: S, N, B, BOOL, NULL.</summary>
    [Test]
    [DisplayName("dynamodb.put-get-all-scalar-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var binaryData = Convert.ToBase64String(Encoding.ASCII.GetBytes("binary-payload"));
        var item = Item(
            ("pk", S("types-test")),
            ("str_attr", S("text")),
            ("num_attr", N("123.456")),
            ("bin_attr", B(Convert.FromBase64String(binaryData))),
            ("bool_true", Bool(true)),
            ("bool_false", Bool(false)),
            ("null_attr", Null()));
        await ddb.PutItemAsync(table, item);
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("types-test"))), ConsistentRead = true })).Item;
        Ensure(got is not null, "expected an Item in the GetItem response, got none");
        Ensure(Py.Equal(got["str_attr"], S("text")), $"expected str_attr {{'S': 'text'}}, got {Py.Repr(got.GetValueOrDefault("str_attr"))}");
        Ensure(Py.Equal(got["num_attr"], N("123.456")), $"expected num_attr {{'N': '123.456'}}, got {Py.Repr(got.GetValueOrDefault("num_attr"))}");
        Ensure(Py.Equal(got["bool_true"], Bool(true)), $"expected bool_true {{'BOOL': True}}, got {Py.Repr(got.GetValueOrDefault("bool_true"))}");
        Ensure(Py.Equal(got["bool_false"], Bool(false)), $"expected bool_false {{'BOOL': False}}, got {Py.Repr(got.GetValueOrDefault("bool_false"))}");
        Ensure(Py.Equal(got["null_attr"], Null()), $"expected null_attr {{'NULL': True}}, got {Py.Repr(got.GetValueOrDefault("null_attr"))}");
        Ensure(
            Bytes(got["bin_attr"].B).SequenceEqual(Convert.FromBase64String(binaryData)),
            $"expected bin_attr bytes {Py.Repr(Convert.FromBase64String(binaryData))}, got {Py.Repr(got.GetValueOrDefault("bin_attr")?.B)}");
    }
}
