using System.Net;

namespace Luna.Protocol.Tests.Support;

/// <summary>Fake server that also lets the test read the form content of the request.</summary>
internal sealed class RoutingHandler(Func<HttpRequestMessage, string, string> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string Body)> Calls { get; } = [];

    /// <summary>Lets the test adjust the response (status code, headers) after the content exists.</summary>
    public Action<HttpRequestMessage, HttpResponseMessage>? Decorate { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Calls.Add((request.Method, request.RequestUri!, body));

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request, body)),
        };
        Decorate?.Invoke(request, response);
        return response;
    }
}