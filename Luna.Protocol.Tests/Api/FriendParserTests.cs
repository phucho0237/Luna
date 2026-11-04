using System.Text.Json;
using Xunit;
using Luna.Protocol.Api;

namespace Luna.Protocol.Tests.Api;

public sealed class FriendParserTests
{
    // Every value is fake, built to the real shape recorded in PROTOCOL.md section 8.
    private const string FullFriend =
        "{\"userId\":\"7000000000000000001\",\"username\":\"fake.user\",\"displayName\":\"Ten Gia\",\"zaloName\":\"Ten Profile Gia\","
        + "\"avatar\":\"https://s120.zalo.me/fake-avatar.jpg\",\"bgavatar\":\"bg-fake\",\"cover\":\"cover-fake\",\"gender\":1,"
        + "\"dob\":946684800,\"sdob\":\"01/01/2000\",\"status\":\"trang thai gia\",\"phoneNumber\":\"0901234567\","
        + "\"isFr\":1,\"isBlocked\":0,\"lastActionTime\":1700000000000,\"lastUpdateTime\":1700000001000,\"isActive\":1,"
        + "\"key\":5,\"type\":0,\"userKey\":\"userkey-fake\",\"oaInfo\":null,\"globalId\":\"global-fake\","
        + "\"bizPkg\":{\"label\":null,\"pkgId\":0},\"createdTs\":1600000000,\"oa_status\":null}";

    private static JsonElement Array(params string[] items) => JsonDocument.Parse("[" + string.Join(",", items) + "]").RootElement;

    [Fact]
    public void Parses_full_friend()
    {
        FriendListResult result = FriendParser.Parse(Array(FullFriend));

        Friend f = Assert.Single(result.Friends);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("7000000000000000001", f.UserId);
        Assert.Equal("fake.user", f.Username);
        Assert.Equal("Ten Gia", f.DisplayName);
        Assert.Equal("Ten Profile Gia", f.ProfileName);
        Assert.Equal("https://s120.zalo.me/fake-avatar.jpg", f.Avatar);
        Assert.Equal("0901234567", f.PhoneNumber);
        Assert.Equal("trang thai gia", f.Status);
        Assert.Equal(1, f.Gender);
        Assert.True(f.IsFriend);
        Assert.False(f.IsBlocked);
        Assert.True(f.IsActive);
        Assert.Equal(1700000000000L, f.LastActionTime);
        Assert.Equal(1700000001000L, f.LastUpdateTime);
        Assert.Equal(1600000000L, f.CreatedTs);
    }

    [Fact]
    public void Missing_fields_become_null_or_false()
    {
        FriendListResult result = FriendParser.Parse(Array("{\"userId\":\"42\"}"));

        Friend f = Assert.Single(result.Friends);
        Assert.Equal("42", f.UserId);
        Assert.Null(f.Username);
        Assert.Null(f.DisplayName);
        Assert.Null(f.Gender);
        Assert.Null(f.LastActionTime);
        Assert.Null(f.CreatedTs);
        Assert.False(f.IsFriend);
        Assert.False(f.IsBlocked);
        Assert.False(f.IsActive);
    }

    [Fact]
    public void Wrong_types_become_null_without_throwing()
    {
        FriendListResult result = FriendParser.Parse(Array(
            "{\"userId\":\"42\",\"username\":{\"a\":1},\"displayName\":[1],\"gender\":\"abc\",\"lastActionTime\":\"x\","
            + "\"createdTs\":1.5,\"isFr\":\"yes\",\"isBlocked\":null,\"isActive\":{}}"));

        Friend f = Assert.Single(result.Friends);
        Assert.Null(f.Username);
        Assert.Null(f.DisplayName);
        Assert.Null(f.Gender);
        Assert.Null(f.LastActionTime);
        Assert.Null(f.CreatedTs);
        Assert.False(f.IsFriend);
        Assert.False(f.IsBlocked);
        Assert.False(f.IsActive);
    }

    [Fact]
    public void Numeric_user_id_is_kept_as_exact_string()
    {
        FriendListResult result = FriendParser.Parse(Array("{\"userId\":7000000000000000001}"));

        Assert.Equal("7000000000000000001", Assert.Single(result.Friends).UserId);
    }

    [Fact]
    public void Numbers_given_as_strings_are_parsed()
    {
        FriendListResult result = FriendParser.Parse(Array("{\"userId\":\"1\",\"gender\":\"0\",\"isFr\":\"1\"}"));

        Friend f = Assert.Single(result.Friends);
        Assert.Equal(0, f.Gender);
        Assert.True(f.IsFriend);
    }

    [Fact]
    public void Unusable_elements_are_counted_as_skipped()
    {
        FriendListResult result = FriendParser.Parse(Array(
            "42",
            "\"text\"",
            "null",
            "[]",
            "{\"displayName\":\"khong co id\"}",
            "{\"userId\":null}",
            "{\"userId\":true}",
            "{\"userId\":{}}",
            "{\"userId\":\"\"}",
            "{\"userId\":\"ok\"}"));

        Assert.Equal("ok", Assert.Single(result.Friends).UserId);
        Assert.Equal(9, result.Skipped);
    }

    [Fact]
    public void Lone_surrogate_in_text_field_only_nulls_that_field()
    {
        FriendListResult result = FriendParser.Parse(Array(
            "{\"userId\":\"1\",\"displayName\":\"\\ud800\"}",
            "{\"userId\":\"2\"}"));

        Assert.Equal(2, result.Friends.Count);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("1", result.Friends[0].UserId);
        Assert.Null(result.Friends[0].DisplayName);
    }

    [Fact]
    public void Lone_surrogate_in_user_id_skips_the_element()
    {
        FriendListResult result = FriendParser.Parse(Array(
            "{\"userId\":\"\\ud800\",\"displayName\":\"Ten Gia\"}",
            "{\"userId\":\"2\"}"));

        Assert.Equal("2", Assert.Single(result.Friends).UserId);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Several_corrupt_text_fields_keep_the_friend_and_other_fields()
    {
        FriendListResult result = FriendParser.Parse(Array(
            "{\"userId\":\"1\",\"username\":\"\\udc00\",\"zaloName\":\"\\ud800\",\"displayName\":\"Ten Gia\",\"gender\":1,\"isFr\":1}"));

        Friend f = Assert.Single(result.Friends);
        Assert.Equal(0, result.Skipped);
        Assert.Null(f.Username);
        Assert.Null(f.ProfileName);
        Assert.Equal("Ten Gia", f.DisplayName);
        Assert.Equal(1, f.Gender);
        Assert.True(f.IsFriend);
    }

    [Fact]
    public void Unknown_extra_fields_are_ignored()
    {
        FriendListResult result = FriendParser.Parse(Array("{\"userId\":\"1\",\"brandNewField\":{\"x\":[1,2]}}"));

        Assert.Single(result.Friends);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Empty_array_gives_empty_result()
    {
        FriendListResult result = FriendParser.Parse(Array());

        Assert.Empty(result.Friends);
        Assert.Equal(0, result.Skipped);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("7")]
    public void Non_array_data_is_invalid(string json)
    {
        JsonElement data = JsonDocument.Parse(json).RootElement;

        Assert.Throws<InvalidDataException>(() => FriendParser.Parse(data));
    }

    [Fact]
    public void ToString_hides_personal_data()
    {
        FriendListResult result = FriendParser.Parse(Array(FullFriend));
        Friend f = Assert.Single(result.Friends);

        foreach (string text in new[] { f.ToString(), result.ToString() })
        {
            Assert.DoesNotContain("7000000000000000001", text, StringComparison.Ordinal);
            Assert.DoesNotContain("fake.user", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Ten Gia", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Ten Profile Gia", text, StringComparison.Ordinal);
            Assert.DoesNotContain("0901234567", text, StringComparison.Ordinal);
            Assert.DoesNotContain("fake-avatar", text, StringComparison.Ordinal);
            Assert.DoesNotContain("trang thai gia", text, StringComparison.Ordinal);
        }

        Assert.Contains("IsFriend", f.ToString(), StringComparison.Ordinal);
        Assert.Contains("IsBlocked", f.ToString(), StringComparison.Ordinal);
    }
}
