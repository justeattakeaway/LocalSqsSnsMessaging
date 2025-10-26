using System.Text;
using LocalSqsSnsMessaging.Http;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Http;

/// <summary>
/// Tests for the custom UTF-8 query string parser.
/// Verifies it produces the same results as HttpUtility.ParseQueryString
/// but works directly with UTF-8 bytes.
/// </summary>
public class QueryStringParsingTests
{
    [Test]
    public void ParseQueryStringUtf8_SimpleKeyValue_ShouldParse()
    {
        // Arrange
        var queryString = "key=value";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result.ShouldContainKeyAndValue("key", "value");
        result.Count.ShouldBe(1);
    }

    [Test]
    public void ParseQueryStringUtf8_MultipleKeyValues_ShouldParse()
    {
        // Arrange
        var queryString = "Action=Publish&TopicArn=arn:aws:sns:us-east-1:123456789012:MyTopic&Message=Hello";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result.Count.ShouldBe(3);
        result["Action"].ShouldBe("Publish");
        result["TopicArn"].ShouldBe("arn:aws:sns:us-east-1:123456789012:MyTopic");
        result["Message"].ShouldBe("Hello");
    }

    [Test]
    public void ParseQueryStringUtf8_UrlEncodedSpace_ShouldDecode()
    {
        // Arrange - URL encoding uses %20 for space
        var queryString = "Message=Hello%20World";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result["Message"].ShouldBe("Hello World");
    }

    [Test]
    public void ParseQueryStringUtf8_PlusAsSpace_ShouldDecode()
    {
        // Arrange - Query strings also use + for space
        var queryString = "Message=Hello+World";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result["Message"].ShouldBe("Hello World");
    }

    [Test]
    public void ParseQueryStringUtf8_SpecialCharacters_ShouldDecode()
    {
        // Arrange - Special characters like : / are URL encoded
        var queryString = "TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A123456789012%3AMyTopic";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result["TopicArn"].ShouldBe("arn:aws:sns:us-east-1:123456789012:MyTopic");
    }

    [Test]
    public void ParseQueryStringUtf8_EmptyValue_ShouldParse()
    {
        // Arrange
        var queryString = "key1=value1&key2=&key3=value3";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result.Count.ShouldBe(3);
        result["key1"].ShouldBe("value1");
        result["key2"].ShouldBe("");
        result["key3"].ShouldBe("value3");
    }

    [Test]
    public void ParseQueryStringUtf8_NoValue_ShouldIgnore()
    {
        // Arrange - key without = should be ignored (not standard, but safe)
        var queryString = "key1=value1&invalidkey&key2=value2";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result.Count.ShouldBe(2);
        result["key1"].ShouldBe("value1");
        result["key2"].ShouldBe("value2");
    }

    [Test]
    public void ParseQueryStringUtf8_ComplexAwsExample_ShouldParse()
    {
        // Arrange - Real AWS SNS Publish request
        var queryString = "Action=Publish&" +
                         "TopicArn=arn%3Aaws%3Asns%3Aus-east-1%3A123456789012%3AMyTopic&" +
                         "Message=Hello+World&" +
                         "Subject=Test+Message&" +
                         "MessageAttributes.entry.1.Name=attr1&" +
                         "MessageAttributes.entry.1.Value.DataType=String&" +
                         "MessageAttributes.entry.1.Value.StringValue=value1";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result["Action"].ShouldBe("Publish");
        result["TopicArn"].ShouldBe("arn:aws:sns:us-east-1:123456789012:MyTopic");
        result["Message"].ShouldBe("Hello World");
        result["Subject"].ShouldBe("Test Message");
        result["MessageAttributes.entry.1.Name"].ShouldBe("attr1");
        result["MessageAttributes.entry.1.Value.DataType"].ShouldBe("String");
        result["MessageAttributes.entry.1.Value.StringValue"].ShouldBe("value1");
    }

    [Test]
    public void ParseQueryStringUtf8_Vs_HttpUtility_ShouldMatch()
    {
        // Arrange - Compare our parser with HttpUtility.ParseQueryString
        var queryString = "Action=Publish&Message=Hello%20World&TopicArn=arn%3Aaws%3Asns";
        var utf8Bytes = Encoding.UTF8.GetBytes(queryString);

        // Act
        var ourResult = QueryStringParser.Parse(utf8Bytes);
        var httpUtilityResult = System.Web.HttpUtility.ParseQueryString(queryString);

        // Assert - Both should produce the same key-value pairs
        ourResult.Count.ShouldBe(httpUtilityResult.Count);
        foreach (var key in ourResult.Keys)
        {
            ourResult[key].ShouldBe(httpUtilityResult[key], $"Key '{key}' should have matching values");
        }
    }

    [Test]
    public void ParseQueryStringUtf8_EmptyString_ShouldReturnEmpty()
    {
        // Arrange
        var utf8Bytes = Encoding.UTF8.GetBytes("");

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result.Count.ShouldBe(0);
    }

    [Test]
    public void ParseQueryStringUtf8_OnlyAmpersands_ShouldReturnEmpty()
    {
        // Arrange
        var utf8Bytes = Encoding.UTF8.GetBytes("&&&");

        // Act
        var result = QueryStringParser.Parse(utf8Bytes);

        // Assert
        result.Count.ShouldBe(0);
    }
}
