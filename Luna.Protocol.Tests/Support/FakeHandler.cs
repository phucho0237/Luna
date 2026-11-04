using System.Net;

namespace Luna.Protocol.Tests.Support;

internal sealed class FakeHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request)),
        };
        return Task.FromResult(response);
    }
}