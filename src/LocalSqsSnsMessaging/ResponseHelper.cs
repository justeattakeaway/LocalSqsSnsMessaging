using System.Net;
using Amazon.Runtime;

namespace LocalSqsSnsMessaging;

internal static class ResponseHelper
{
    public static T SetCommonProperties<T>(this T response) where T : AmazonWebServiceResponse
    {
        response.HttpStatusCode = HttpStatusCode.OK;
        response.ResponseMetadata = new ResponseMetadata
        {
            RequestId = Guid.NewGuid().ToString()
        };

        response.ContentLength = 0L;

        return response;
    }
}