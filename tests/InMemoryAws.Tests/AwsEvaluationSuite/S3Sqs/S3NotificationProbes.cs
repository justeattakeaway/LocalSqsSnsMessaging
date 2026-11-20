using System.Text;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>Small translations the <c>s3__sqs</c> notification ports share.</summary>
internal static class S3NotificationProbes
{
    /// <summary>
    /// Whether a HeadObject error is the bare 404 a HEAD answers with (no body). botocore reports it
    /// as code <c>404</c>, message <c>Not Found</c>; the .NET SDK reports the same wire response as
    /// <c>NotFound</c>, so either spelling of that one response is accepted.
    /// </summary>
    public static bool IsHeadNotFound(string? code, string? message) =>
        (code, message) is ("404", "Not Found") or ("NotFound", _);

    /// <summary><c>urllib.parse.unquote_plus</c>: '+' is a space, <c>%XX</c> an octet of UTF-8.</summary>
    public static string UnquotePlus(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}
