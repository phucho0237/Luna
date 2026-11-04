using System.Net;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Crypto;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

public sealed class GetAllFriendsTests
{
    private static string Page(params string[] ids) =>
        ApiTestKit.OkEnvelope("[" + string.Join(",", ids.Select(id => $"{{\"userId\":\"{id}\"}}")) + "]");

    private static (int Page, int Count) Requested(Uri uri)
    {
        string payload = new SessionCipher(ApiTestKit.FakeEnk).Decrypt(ApiTestKit.ParamsOf(uri));
        using JsonDocument doc = JsonDocument.Parse(payload);
        return (doc.RootElement.GetProperty("page").GetInt32(), doc.RootElement.GetProperty("count").GetInt32());
    }

    /// <summary>Fake server: <paramref name="pages"/>[page - 1] is the response for that page; outside the list it returns an empty page.</summary>
    private static (ApiClient Client, RoutingHandler Handler, ApiTestKit.FakeTime Time) New(params string[] pages)
    {
        var time = new ApiTestKit.FakeTime();
        var handler = new RoutingHandler((req, _) =>
        {
            int page = Requested(req.RequestUri!).Page;
            return page <= pages.Length ? pages[page - 1] : Page();
        });
        var client = new ApiClient(ApiTestKit.Context(), ApiTestKit.Login("https://profile.zalo.me"), time.Options(), handler);
        return (client, handler, time);
    }

    private static string[] Ids(FriendListResult result) => [.. result.Friends.Select(f => f.UserId)];

    [Fact]
    public async Task Walks_pages_until_an_empty_page()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a", "b"), Page("c", "d"), Page());
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2);

            Assert.Equal(["a", "b", "c", "d"], Ids(result));
            Assert.Equal(3, result.Pages);
            Assert.False(result.Truncated);
            Assert.False(result.PageParameterIgnored);
        }

        Assert.Equal([(1, 2), (2, 2), (3, 2)], handler.Calls.Select(c => Requested(c.Uri)));
    }

    [Fact]
    public async Task Empty_first_page_ends_immediately()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page());
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync();

            Assert.Empty(result.Friends);
            Assert.Equal(1, result.Pages);
            Assert.False(result.Truncated);
        }

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Short_page_does_not_stop_when_next_page_has_new_friends()
    {
        // The server has a count ceiling lower than pageSize: page 1 has only 2 elements although 5 were requested.
        (ApiClient client, _, _) = New(Page("a", "b"), Page("c", "d"), Page("e"), Page());
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 5);

            Assert.Equal(["a", "b", "c", "d", "e"], Ids(result));
            Assert.Equal(4, result.Pages);
        }
    }

    [Fact]
    public async Task Server_ignoring_page_parameter_is_detected_and_stops()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a", "b"), Page("a", "b"), Page("a", "b"));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2);

            Assert.Equal(["a", "b"], Ids(result));
            Assert.True(result.PageParameterIgnored);
            Assert.False(result.Truncated);
            Assert.Equal(2, result.Pages);
        }

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Page_with_only_known_friends_that_differs_from_previous_stops_without_flag()
    {
        (ApiClient client, _, _) = New(Page("a", "b"), Page("c"), Page("a"));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2);

            Assert.Equal(["a", "b", "c"], Ids(result));
            Assert.False(result.PageParameterIgnored);
            Assert.Equal(3, result.Pages);
        }
    }

    [Fact]
    public async Task Partial_overlap_with_previous_page_keeps_going_and_dedupes()
    {
        (ApiClient client, _, _) = New(Page("a", "b"), Page("b", "c"), Page());
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2);

            Assert.Equal(["a", "b", "c"], Ids(result));
            Assert.False(result.PageParameterIgnored);
        }
    }

    [Fact]
    public async Task Stops_at_maxPages_and_reports_truncated()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a"), Page("b"), Page("c"), Page("d"));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 1, maxPages: 2);

            Assert.Equal(["a", "b"], Ids(result));
            Assert.Equal(2, result.Pages);
            Assert.True(result.Truncated);
        }

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Empty_page_exactly_at_maxPages_is_not_truncated()
    {
        (ApiClient client, _, _) = New(Page("a"), Page());
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 1, maxPages: 2);

            Assert.Equal(["a"], Ids(result));
            Assert.False(result.Truncated);
        }
    }

    [Fact]
    public async Task Skipped_elements_are_summed_across_pages()
    {
        string withJunk = ApiTestKit.OkEnvelope("[{\"userId\":\"a\"},42,{\"name\":\"no id\"}]");
        string withJunk2 = ApiTestKit.OkEnvelope("[{\"userId\":\"b\"},null]");
        (ApiClient client, _, _) = New(withJunk, withJunk2, Page());
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 3);

            Assert.Equal(["a", "b"], Ids(result));
            Assert.Equal(3, result.Skipped);
        }
    }

    [Fact]
    public async Task onElement_sees_every_raw_element_including_duplicates()
    {
        (ApiClient client, _, _) = New(Page("a", "b"), Page("b", "c"), Page());
        var seen = new List<string>();
        using (client)
        {
            await client.GetAllFriendsAsync(pageSize: 2, onElement: e => seen.Add(e.GetProperty("userId").GetString()!));
        }

        Assert.Equal(["a", "b", "b", "c"], seen);
    }

    [Fact]
    public async Task Api_error_mid_walk_propagates_without_partial_result()
    {
        (ApiClient client, _, _) = New(Page("a"), ApiTestKit.Envelope("{\"error_code\":5}"));
        using (client)
        {
            await Assert.ThrowsAsync<ApiException>(() => client.GetAllFriendsAsync(pageSize: 1));
        }
    }

    [Fact]
    public async Task Rate_limit_mid_walk_propagates()
    {
        var time = new ApiTestKit.FakeTime();
        var handler = new RoutingHandler((req, _) => Page(Requested(req.RequestUri!).Page == 1 ? "a" : "b"));
        handler.Decorate = (req, resp) =>
        {
            if (Requested(req.RequestUri!).Page == 2)
            {
                resp.StatusCode = HttpStatusCode.TooManyRequests;
            }
        };
        using var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login("https://profile.zalo.me"), time.Options(), handler);

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAllFriendsAsync(pageSize: 1));
    }

    [Fact]
    public async Task Network_error_mid_walk_propagates()
    {
        var time = new ApiTestKit.FakeTime();
        var handler = new RoutingHandler((req, _) => Page("a"));
        handler.Decorate = (req, resp) =>
        {
            if (Requested(req.RequestUri!).Page == 2)
            {
                resp.StatusCode = HttpStatusCode.BadGateway;
            }
        };
        using var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login("https://profile.zalo.me"), time.Options(maxHostAttempts: 1), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAllFriendsAsync(pageSize: 1));
    }

    [Fact]
    public async Task Pages_are_spaced_by_MinInterval()
    {
        (ApiClient client, _, ApiTestKit.FakeTime time) = New(Page("a"), Page("b"), Page("c"), Page());
        using (client)
        {
            await client.GetAllFriendsAsync(pageSize: 1);
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], time.Delays);
    }

    [Fact]
    public async Task Cancellation_between_pages_throws_and_makes_no_more_calls()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a"), Page("b"), Page());
        using var cts = new CancellationTokenSource();
        using (client)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                client.GetAllFriendsAsync(pageSize: 1, onElement: _ => cts.Cancel(), cancellationToken: cts.Token));
        }

        Assert.Single(handler.Calls);
    }

    private static string InnerError(int code) => ApiTestKit.Envelope($"{{\"error_code\":{code}}}");

    private static string OuterError(int code) => $"{{\"error_code\":{code},\"error_message\":\"x\"}}";

    [Fact]
    public async Task Error_112_on_page_2_ends_the_list_and_keeps_page_1()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a", "b"), InnerError(112));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2);

            Assert.Equal(["a", "b"], Ids(result));
            Assert.True(result.EndedByNoData);
            Assert.Equal(2, result.Pages);
        }

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Error_112_on_page_3_keeps_both_good_pages()
    {
        (ApiClient client, _, _) = New(Page("a", "b"), Page("c", "d"), InnerError(112));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2);

            Assert.Equal(["a", "b", "c", "d"], Ids(result));
            Assert.True(result.EndedByNoData);
            Assert.Equal(3, result.Pages);
        }
    }

    [Fact]
    public async Task Outer_layer_112_on_page_2_is_also_recognised()
    {
        (ApiClient client, _, _) = New(Page("a", "b"), OuterError(112));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2);

            Assert.Equal(["a", "b"], Ids(result));
            Assert.True(result.EndedByNoData);
        }
    }

    [Fact]
    public async Task Error_112_on_page_1_still_throws()
    {
        (ApiClient client, _, _) = New(InnerError(112));
        using (client)
        {
            ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetAllFriendsAsync(pageSize: 2));
            Assert.Equal(112, ex.ErrorCode);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(111)]
    [InlineData(113)]
    public async Task Other_error_codes_on_page_2_still_throw(int code)
    {
        (ApiClient client, _, _) = New(Page("a", "b"), InnerError(code));
        using (client)
        {
            ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.GetAllFriendsAsync(pageSize: 2));
            Assert.Equal(code, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task Request_that_returned_112_is_still_spaced_by_MinInterval()
    {
        (ApiClient client, _, ApiTestKit.FakeTime time) = New(Page("a"), Page("b"), InnerError(112));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 1);

            Assert.True(result.EndedByNoData);
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], time.Delays);
    }

    [Fact]
    public async Task Cancellation_still_wins_over_112_handling()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a"), InnerError(112));
        using var cts = new CancellationTokenSource();
        using (client)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                client.GetAllFriendsAsync(pageSize: 1, onElement: _ => cts.Cancel(), cancellationToken: cts.Token));
        }

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Short_last_page_at_maxPages_is_still_truncated_because_length_is_not_a_signal()
    {
        (ApiClient client, _, _) = New(Page("a"));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2, maxPages: 1);

            Assert.Equal(["a"], Ids(result));
            Assert.True(result.Truncated);
        }
    }

    [Fact]
    public async Task Full_last_page_at_maxPages_is_truncated()
    {
        (ApiClient client, _, _) = New(Page("a", "b"));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2, maxPages: 1);

            Assert.True(result.Truncated);
        }
    }

    [Fact]
    public async Task Ending_by_112_is_not_truncated()
    {
        (ApiClient client, _, _) = New(Page("a", "b"), InnerError(112));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2, maxPages: 2);

            Assert.True(result.EndedByNoData);
            Assert.False(result.Truncated);
        }
    }

    [Fact]
    public async Task Simulated_real_run_short_last_page_then_112()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a", "b"), Page("c", "d"), Page("e"), InnerError(112));
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2, maxPages: 5);

            Assert.Equal(["a", "b", "c", "d", "e"], Ids(result));
            Assert.Equal(4, result.Pages);
            Assert.True(result.EndedByNoData);
            Assert.False(result.Truncated);
        }

        Assert.Equal(4, handler.Calls.Count);
    }

    [Fact]
    public async Task Simulated_real_run_short_last_page_then_empty_array()
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page("a", "b"), Page("c", "d"), Page("e"), Page());
        using (client)
        {
            FriendListResult result = await client.GetAllFriendsAsync(pageSize: 2, maxPages: 5);

            Assert.Equal(["a", "b", "c", "d", "e"], Ids(result));
            Assert.Equal(4, result.Pages);
            Assert.False(result.EndedByNoData);
            Assert.False(result.Truncated);
        }

        Assert.Equal(4, handler.Calls.Count);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(501, 3)]
    [InlineData(50, 0)]
    [InlineData(50, 101)]
    public async Task Rejects_out_of_range_arguments(int pageSize, int maxPages)
    {
        (ApiClient client, RoutingHandler handler, _) = New(Page());
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAllFriendsAsync(pageSize, maxPages));
        }

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Non_array_data_is_invalid()
    {
        (ApiClient client, _, _) = New(ApiTestKit.OkEnvelope("{}"));
        using (client)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAllFriendsAsync());
        }
    }
}
