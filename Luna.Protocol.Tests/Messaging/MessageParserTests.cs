using System.Text.Json;
using Xunit;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Messaging;

public class MessageParserTests
{
    private static SocketEvent CreateEvent(ushort cmd, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return new SocketEvent(cmd, 0, doc.RootElement.Clone());
    }

    [Fact]
    public void Parse_PrivateIncomingMessage_SetsFieldsCorrectly()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "actionId": "1001",
                        "msgId": "2002",
                        "cliMsgId": "3003",
                        "msgType": "chat.text",
                        "uidFrom": "84901234567",
                        "idTo": "0",
                        "dName": "Người gửi",
                        "ts": "1700000000000",
                        "status": 0,
                        "content": "Xin chào Luna",
                        "notify": "1",
                        "ttl": 0,
                        "userId": "84901234567",
                        "uin": "uin_123",
                        "cmd": 501,
                        "st": 1,
                        "at": 2,
                        "realMsgId": "real_2002"
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.Equal(ThreadType.User, msg.ThreadType);
        Assert.Equal("84901234567", msg.ThreadId);
        Assert.False(msg.IsSelf);
        Assert.Equal("1001", msg.ActionId);
        Assert.Equal("2002", msg.MsgId);
        Assert.Equal("3003", msg.CliMsgId);
        Assert.Equal("chat.text", msg.MsgType);
        Assert.Equal("84901234567", msg.UidFrom);
        Assert.Equal("0", msg.IdTo);
        Assert.Equal("Người gửi", msg.DisplayName);
        Assert.Equal("1700000000000", msg.Timestamp);
        Assert.Equal(0, msg.Status);
        Assert.Equal("1", msg.Notify);
        Assert.Equal("real_2002", msg.RealMsgId);
        Assert.False(msg.IsUndo);

        var textContent = Assert.IsType<TextContent>(msg.Content);
        Assert.Equal("Xin chào Luna", textContent.Text);
        Assert.Equal("Xin chào Luna", msg.Text);
    }

    [Fact]
    public void Parse_PrivateSelfSentMessage_RecognizedAsSelf()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "100",
                        "cliMsgId": "101",
                        "uidFrom": "0",
                        "idTo": "84999888777",
                        "content": "Tin nhắn tôi gửi"
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.Equal(ThreadType.User, msg.ThreadType);
        Assert.True(msg.IsSelf);
        Assert.Equal("84999888777", msg.ThreadId);
        Assert.Equal("0", msg.UidFrom);
        Assert.Equal("84999888777", msg.IdTo);
    }

    [Fact]
    public void Parse_WithSelfUid_RecognizesMessageAsSelfWhenUidFromMatches()
    {
        string myUid = "84900112233";
        string recipientUid = "84988776655";

        string json = $$"""
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "100",
                        "cliMsgId": "101",
                        "uidFrom": "{{myUid}}",
                        "idTo": "{{recipientUid}}",
                        "content": "Tin tôi gửi từ máy khác"
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = MessageParser.ParseEvent(socketEvent, selfUid: myUid);

        ChatMessage msg = Assert.Single(result.Messages);
        Assert.Equal(ThreadType.User, msg.ThreadType);
        Assert.True(msg.IsSelf);
        Assert.Equal(recipientUid, msg.ThreadId);
        Assert.Equal(myUid, msg.UidFrom);
        Assert.Equal(recipientUid, msg.IdTo);
    }

    [Fact]
    public void Parse_WithSelfUid_DoesNotRecognizeAsSelfWhenUidFromDiffers()
    {
        string myUid = "84900112233";
        string senderUid = "84933221100";

        string json = $$"""
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "102",
                        "cliMsgId": "103",
                        "uidFrom": "{{senderUid}}",
                        "idTo": "{{myUid}}",
                        "content": "Tin người khác gửi cho tôi"
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = MessageParser.ParseEvent(socketEvent, selfUid: myUid);

        ChatMessage msg = Assert.Single(result.Messages);
        Assert.Equal(ThreadType.User, msg.ThreadType);
        Assert.False(msg.IsSelf);
        Assert.Equal(senderUid, msg.ThreadId);
        Assert.Equal(senderUid, msg.UidFrom);
        Assert.Equal(myUid, msg.IdTo);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("", null)]
    public void Parse_WithNullOrEmptySelfUid_EmptyOrMissingUidFromIsNotSelf(string? selfUid, string? uidFrom)
    {
        string uidField = uidFrom is null ? "" : $$""" "uidFrom": "{{uidFrom}}", """;
        string json = $$"""
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "200",
                        {{uidField}}
                        "idTo": "84988776655",
                        "content": "Tin nhắn thử nghiệm"
                    }
                ]
            }
        }
        """;

        var parser = new MessageParser(selfUid);
        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = parser.Parse(socketEvent);

        ChatMessage msg = Assert.Single(result.Messages);
        Assert.False(msg.IsSelf);

        ParseResult eventResult = MessageParser.ParseEvent(socketEvent, selfUid);
        Assert.False(Assert.Single(eventResult.Messages).IsSelf);
    }

    [Fact]
    public void Parse_WithNumericJsonUidFrom_MatchesStringSelfUid()
    {
        string myUid = "84900112233";
        string recipientUid = "84988776655";

        // uidFrom is a JSON number without quotes: 84900112233
        string json = $$"""
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "201",
                        "cliMsgId": "202",
                        "uidFrom": 84900112233,
                        "idTo": "{{recipientUid}}",
                        "content": "Tin gửi từ tôi với uidFrom là số không có dấu nháy"
                    }
                ]
            }
        }
        """;

        var parser = new MessageParser(myUid);
        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = parser.Parse(socketEvent);

        ChatMessage msg = Assert.Single(result.Messages);
        Assert.True(msg.IsSelf);
        Assert.Equal(myUid, msg.UidFrom);
        Assert.Equal(recipientUid, msg.ThreadId);

        ParseResult eventResult = MessageParser.ParseEvent(socketEvent, selfUid: myUid);
        ChatMessage eventMsg = Assert.Single(eventResult.Messages);
        Assert.True(eventMsg.IsSelf);
        Assert.Equal(myUid, eventMsg.UidFrom);
        Assert.Equal(recipientUid, eventMsg.ThreadId);
    }

    [Fact]
    public void Parse_GroupMessage_SetsGroupThreadId()
    {
        string json = """
        {
            "data": {
                "groupMsgs": [
                    {
                        "msgId": "500",
                        "cliMsgId": "501",
                        "uidFrom": "84911111111",
                        "idTo": "g_777888999",
                        "content": "Chào cả nhóm"
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(521, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.Equal(ThreadType.Group, msg.ThreadType);
        Assert.False(msg.IsSelf);
        Assert.Equal("g_777888999", msg.ThreadId);
        Assert.Equal("84911111111", msg.UidFrom);
        Assert.Equal("g_777888999", msg.IdTo);
    }

    [Fact]
    public void Parse_GroupMessageSelfSent_RecognizedAsSelf()
    {
        string json = """
        {
            "data": {
                "groupMsgs": [
                    {
                        "msgId": "600",
                        "cliMsgId": "601",
                        "uidFrom": "0",
                        "idTo": "g_777888999",
                        "content": "Tin tôi gửi vào nhóm"
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(521, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.Equal(ThreadType.Group, msg.ThreadType);
        Assert.True(msg.IsSelf);
        Assert.Equal("g_777888999", msg.ThreadId);
    }

    [Fact]
    public void Parse_GroupMessageWithMentions_ParsesAllMentions()
    {
        string json = """
        {
            "data": {
                "groupMsgs": [
                    {
                        "msgId": "700",
                        "uidFrom": "111",
                        "idTo": "g_1",
                        "content": "@Alice @Bob chào",
                        "mentions": [
                            { "uid": "222", "pos": 0, "len": 6, "type": 0 },
                            { "uid": "333", "pos": 7, "len": 4, "type": 1 }
                        ]
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(521, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];
        Assert.Equal(2, msg.Mentions.Count);

        Assert.Equal("222", msg.Mentions[0].Uid);
        Assert.Equal(0, msg.Mentions[0].Pos);
        Assert.Equal(6, msg.Mentions[0].Len);
        Assert.Equal(0, msg.Mentions[0].Type);

        Assert.Equal("333", msg.Mentions[1].Uid);
        Assert.Equal(7, msg.Mentions[1].Pos);
        Assert.Equal(4, msg.Mentions[1].Len);
        Assert.Equal(1, msg.Mentions[1].Type);
    }

    [Fact]
    public void Parse_MessageWithQuote_ParsesQuoteCorrectly()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "800",
                        "uidFrom": "111",
                        "idTo": "0",
                        "content": "Tôi trả lời tin này",
                        "quote": {
                            "ownerId": "999",
                            "cliMsgId": 12345,
                            "globalMsgId": 67890,
                            "cliMsgType": 1,
                            "ts": 1700000000000,
                            "msg": "Tin nhắn gốc",
                            "attach": "attach_data",
                            "fromD": "Người nhắn gốc",
                            "ttl": 0
                        }
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.NotNull(msg.Quote);
        Assert.Equal("999", msg.Quote.OwnerId);
        Assert.Equal("12345", msg.Quote.CliMsgId);
        Assert.Equal("67890", msg.Quote.GlobalMsgId);
        Assert.Equal(1, msg.Quote.CliMsgType);
        Assert.Equal("1700000000000", msg.Quote.Timestamp);
        Assert.Equal("Tin nhắn gốc", msg.Quote.Msg);
        Assert.Equal("attach_data", msg.Quote.Attach);
        Assert.Equal("Người nhắn gốc", msg.Quote.FromD);
        Assert.Equal(0, msg.Quote.Ttl);
    }

    [Fact]
    public void Parse_MessageWithAttachment_ParsesAttachmentContent()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "900",
                        "uidFrom": "111",
                        "idTo": "0",
                        "msgType": "chat.file",
                        "content": {
                            "title": "Báo cáo.pdf",
                            "description": "Tài liệu dự án",
                            "href": "https://file.zalo.me/doc.pdf",
                            "thumb": "https://file.zalo.me/thumb.png",
                            "childnumber": 3,
                            "action": "download",
                            "params": "param_str",
                            "type": "file"
                        }
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];
        Assert.False(msg.IsUndo);
        Assert.Null(msg.Text);

        var attachment = Assert.IsType<AttachmentContent>(msg.Content);
        Assert.Equal("Báo cáo.pdf", attachment.Title);
        Assert.Equal("Tài liệu dự án", attachment.Description);
        Assert.Equal("https://file.zalo.me/doc.pdf", attachment.Href);
        Assert.Equal("https://file.zalo.me/thumb.png", attachment.Thumb);
        Assert.Equal(3, attachment.ChildNumber);
        Assert.Equal("download", attachment.Action);
        Assert.Equal("param_str", attachment.Params);
        Assert.Equal("file", attachment.AttachmentType);
        Assert.Equal(JsonValueKind.Object, attachment.Raw.ValueKind);
    }

    [Fact]
    public void Parse_AttachmentWithObjectParams_ParsesParamsAsJsonString()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "901",
                        "content": {
                            "title": "Ảnh chia sẻ",
                            "params": {
                                "width": 1920,
                                "height": 1080,
                                "format": "png"
                            },
                            "type": "photo"
                        }
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = MessageParser.ParseEvent(socketEvent);

        ChatMessage msg = Assert.Single(result.Messages);
        var attachment = Assert.IsType<AttachmentContent>(msg.Content);
        Assert.Equal("Ảnh chia sẻ", attachment.Title);
        Assert.Equal("photo", attachment.AttachmentType);
        Assert.NotNull(attachment.Params);
        using JsonDocument paramsDoc = JsonDocument.Parse(attachment.Params);
        Assert.Equal(1920, paramsDoc.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(1080, paramsDoc.RootElement.GetProperty("height").GetInt32());
        Assert.Equal("png", paramsDoc.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public void Parse_UndoMessage_RecognizedAsUndo()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "1000",
                        "cliMsgId": "1001",
                        "uidFrom": "111",
                        "idTo": "0",
                        "content": {
                            "deleteMsg": 1,
                            "globalMsgId": 88888,
                            "cliMsgId": 99999,
                            "srcId": 111,
                            "destId": 222
                        }
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];
        Assert.True(msg.IsUndo);

        var undo = Assert.IsType<UndoContent>(msg.Content);
        Assert.True(undo.IsUndo);
        Assert.Equal("1", undo.DeleteMsg);
        Assert.Equal("88888", undo.GlobalMsgId);
        Assert.Equal("99999", undo.CliMsgId);
        Assert.Equal("111", undo.SrcId);
        Assert.Equal("222", undo.DestId);
    }

    [Fact]
    public void Parse_MissingOptionalFields_DoesNotThrow()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {}
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.Equal(string.Empty, msg.MsgId);
        Assert.Equal(string.Empty, msg.ThreadId);
        Assert.Null(msg.PropertyExt);
        Assert.Null(msg.ParamsExt);
        Assert.Null(msg.Quote);
        Assert.Empty(msg.Mentions);
        Assert.IsType<TextContent>(msg.Content);
        Assert.Equal("null", msg.Content.Raw.GetRawText());
    }

    [Fact]
    public void Parse_MissingOrNullContent_UsesValidNullJsonElementAndDoesNotThrowOnGetRawText()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    { "msgId": "missing_1" },
                    { "msgId": "null_2", "content": null }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = MessageParser.ParseEvent(socketEvent);

        Assert.Equal(2, result.Messages.Count);
        foreach (ChatMessage msg in result.Messages)
        {
            Assert.Equal(string.Empty, msg.Text);
            Assert.Equal(JsonValueKind.Null, msg.Content.Raw.ValueKind);
            // No InvalidOperationException blow-up when calling GetRawText()
            Assert.Equal("null", msg.Content.Raw.GetRawText());
        }
    }

    [Fact]
    public void Parse_LargeNumbersBeyondInt64_PreservesPrecisionAsRawText()
    {
        // 18446744073709551615 > long.MaxValue (9223372036854775807)
        // 99999999999999999999999999999999 (32 digits)
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "actionId": 18446744073709551615,
                        "msgId": 99999999999999999999999999999999,
                        "cliMsgId": 18446744073709551615,
                        "uidFrom": 99999999999999999999999999999999,
                        "idTo": "18446744073709551615",
                        "ts": 18446744073709551615,
                        "content": "Test số lớn"
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.Equal("18446744073709551615", msg.ActionId);
        Assert.Equal("99999999999999999999999999999999", msg.MsgId);
        Assert.Equal("18446744073709551615", msg.CliMsgId);
        Assert.Equal("99999999999999999999999999999999", msg.UidFrom);
        Assert.Equal("18446744073709551615", msg.IdTo);
        Assert.Equal("18446744073709551615", msg.Timestamp);
    }

    [Fact]
    public void Parse_MalformedItemInBatch_DoesNotBreakOtherMessages()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    { "msgId": "1", "content": "Tin 1" },
                    "chữ thay vì đối tượng",
                    { "msgId": "3", "content": "Tin 3" }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = MessageParser.ParseEvent(socketEvent);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("1", result.Messages[0].MsgId);
        Assert.Equal("3", result.Messages[1].MsgId);
    }

    [Fact]
    public void Parse_TracksSkippedCount_ForVariousNonObjectElements()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    "string item",
                    12345,
                    true,
                    [1, 2, 3],
                    { "msgId": "valid_1", "content": "Hợp lệ" }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        ParseResult result = MessageParser.ParseEvent(socketEvent);

        Assert.Single(result.Messages);
        Assert.Equal("valid_1", result.Messages[0].MsgId);
        Assert.Equal(4, result.Skipped);
    }

    [Fact]
    public void Parse_NonMessageCmd_ReturnsEmpty()
    {
        string json = """{ "data": { "msgs": [{ "msgId": "1" }] } }""";

        Assert.Empty(MessageParser.ParseEvent(CreateEvent(504, json)).Messages);
        Assert.Empty(MessageParser.ParseEvent(CreateEvent(522, json)).Messages);
        Assert.Empty(MessageParser.ParseEvent(CreateEvent(601, json)).Messages);
        Assert.Empty(MessageParser.ParseEvent(CreateEvent(2, json)).Messages);
    }

    [Fact]
    public void Parse_PropertyExtAndParamsExt_ParsedCorrectly()
    {
        string json = """
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "1",
                        "propertyExt": {
                            "color": 1,
                            "size": 14,
                            "type": 2,
                            "subType": 3,
                            "ext": "extra_data"
                        },
                        "paramsExt": {
                            "countUnread": 4,
                            "containType": 5,
                            "platformType": 6
                        }
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        ChatMessage msg = list[0];

        Assert.NotNull(msg.PropertyExt);
        Assert.Equal(1, msg.PropertyExt.Color);
        Assert.Equal(14, msg.PropertyExt.Size);
        Assert.Equal(2, msg.PropertyExt.Type);
        Assert.Equal(3, msg.PropertyExt.SubType);
        Assert.Equal("extra_data", msg.PropertyExt.Ext);

        Assert.NotNull(msg.ParamsExt);
        Assert.Equal(4, msg.ParamsExt.CountUnread);
        Assert.Equal(5, msg.ParamsExt.ContainType);
        Assert.Equal(6, msg.ParamsExt.PlatformType);
    }

    [Fact]
    public void Parse_DirectPayloadWithoutDataWrapper_ParsesCorrectly()
    {
        string json = """
        {
            "msgs": [
                { "msgId": "direct_1", "content": "Tin trực tiếp" }
            ]
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        IReadOnlyList<ChatMessage> list = MessageParser.ParseEvent(socketEvent).Messages;

        Assert.Single(list);
        Assert.Equal("direct_1", list[0].MsgId);
        Assert.Equal("Tin trực tiếp", list[0].Text);
    }

    [Fact]
    public void ToString_DoesNotContainMessageContentOrSensitiveData()
    {
        string secretText = "MẬT MÃ BÍ MẬT 123456";
        string secretQuote = "TRÍCH DẪN BÍ MẬT 999";
        string secretAttachTitle = "FILE_BÍ_MẬT.DOCX";

        using JsonDocument doc = JsonDocument.Parse($$"""
        {
            "msgId": "msg_sec",
            "cliMsgId": "cli_sec",
            "content": "{{secretText}}",
            "quote": {
                "ownerId": "owner_1",
                "cliMsgId": "cli_1",
                "globalMsgId": "glob_1",
                "cliMsgType": 1,
                "ts": "123",
                "msg": "{{secretQuote}}",
                "ttl": 0
            }
        }
        """);

        SocketEvent socketEvent = CreateEvent(501, $$"""{ "data": { "msgs": [ {{doc.RootElement.GetRawText()}} ] } }""");
        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(socketEvent).Messages);

        string msgString = msg.ToString();
        Assert.DoesNotContain(secretText, msgString);
        Assert.DoesNotContain(secretQuote, msgString);

        string contentString = msg.Content.ToString();
        Assert.DoesNotContain(secretText, contentString);

        Assert.NotNull(msg.Quote);
        string quoteString = msg.Quote.ToString();
        Assert.DoesNotContain(secretQuote, quoteString);

        using JsonDocument attachDoc = JsonDocument.Parse($$"""{ "title": "{{secretAttachTitle}}" }""");
        var attach = new AttachmentContent(attachDoc.RootElement, Title: secretAttachTitle);
        Assert.DoesNotContain(secretAttachTitle, attach.ToString());
    }

    [Fact]
    public void ToString_DoesNotContainUidsOrThreadId()
    {
        string uidSender = "1234567890123456";
        string uidRecipient = "9876543210987654";
        string uidOwner = "5555555555555555";
        string uidSrc = "6666666666666666";
        string uidDest = "7777777777777777";

        string json = $$"""
        {
            "data": {
                "msgs": [
                    {
                        "msgId": "msg_safe_1",
                        "cliMsgId": "cli_safe_1",
                        "uidFrom": "{{uidSender}}",
                        "idTo": "{{uidRecipient}}",
                        "content": "Nội dung thường",
                        "quote": {
                            "ownerId": "{{uidOwner}}",
                            "cliMsgId": "cli_quote_1",
                            "globalMsgId": "glob_quote_1",
                            "cliMsgType": 1,
                            "ts": "100",
                            "msg": "Trích dẫn"
                        }
                    }
                ]
            }
        }
        """;

        SocketEvent socketEvent = CreateEvent(501, json);
        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(socketEvent).Messages);

        string chatMessageStr = msg.ToString();
        Assert.DoesNotContain(uidSender, chatMessageStr);
        Assert.DoesNotContain(uidRecipient, chatMessageStr);
        Assert.DoesNotContain(msg.ThreadId, chatMessageStr);

        Assert.NotNull(msg.Quote);
        string quoteStr = msg.Quote.ToString();
        Assert.DoesNotContain(uidOwner, quoteStr);

        using JsonDocument undoDoc = JsonDocument.Parse("{}");
        var undo = new UndoContent(
            undoDoc.RootElement,
            DeleteMsg: "1",
            GlobalMsgId: "glob_undo_1",
            CliMsgId: "cli_undo_1",
            SrcId: uidSrc,
            DestId: uidDest);

        string undoStr = undo.ToString();
        Assert.DoesNotContain(uidSrc, undoStr);
        Assert.DoesNotContain(uidDest, undoStr);
    }

    [Fact]
    public void MessageMention_ToString_DoesNotContainUid()
    {
        string secretUid = "9988776655443322";
        var mention = new MessageMention(secretUid, Pos: 0, Len: 10, Type: 1);
        string mentionStr = mention.ToString();

        Assert.DoesNotContain(secretUid, mentionStr);
        Assert.Contains("Pos = 0", mentionStr);
        Assert.Contains("Len = 10", mentionStr);
        Assert.Contains("Type = 1", mentionStr);
    }

    [Fact]
    public void MessagePropertyExt_ToString_DoesNotContainExt()
    {
        string secretExt = "gia-tri-ext-gia-lap";
        var propertyExt = new MessagePropertyExt(Color: 1, Size: 2, Type: 3, SubType: 4, Ext: secretExt);
        string text = propertyExt.ToString();

        Assert.DoesNotContain(secretExt, text);
        Assert.Contains("Color = 1", text);
        Assert.Contains("Size = 2", text);
        Assert.Contains("Type = 3", text);
        Assert.Contains("SubType = 4", text);
    }

    // ---- T1c: the tests below use fake data with the SHAPE of real messages (data types per PROTOCOL.md) ----

    [Fact]
    public void Parse_Sticker_ReadsNumericFieldsAsStrings()
    {
        string json = """
        { "data": { "msgs": [ { "msgId": "1001", "cliMsgId": "2001", "uidFrom": "111", "idTo": "222",
            "msgType": "chat.sticker", "ts": "1700000000000",
            "content": { "id": 12345, "catId": 678, "type": 7 } } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        StickerContent sticker = Assert.IsType<StickerContent>(msg.Content);
        Assert.Equal("12345", sticker.Id);
        Assert.Equal("678", sticker.CatId);
        Assert.Equal("7", sticker.Type);
        Assert.Equal(JsonValueKind.Object, sticker.Raw.ValueKind);
        Assert.Equal("StickerContent", sticker.ToString());
        Assert.Null(msg.Text);
    }

    [Fact]
    public void Parse_UndoWithAllNumbers_ReadsIdsAsStrings()
    {
        // A number larger than 2^53 to make sure it does not go through double.
        string json = """
        { "data": { "msgs": [ { "msgId": "1002", "cliMsgId": "2002", "uidFrom": "111", "idTo": "222",
            "msgType": "chat.undo",
            "content": { "globalMsgId": 7123456789012345678, "cliMsgId": 7123456789012345679,
                         "deleteMsg": 1, "srcId": 7123456789012345680, "destId": 7123456789012345681 } } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        Assert.True(msg.IsUndo);
        UndoContent undo = Assert.IsType<UndoContent>(msg.Content);
        Assert.Equal("1", undo.DeleteMsg);
        Assert.Equal("7123456789012345678", undo.GlobalMsgId);
        Assert.Equal("7123456789012345679", undo.CliMsgId);
        Assert.Equal("7123456789012345680", undo.SrcId);
        Assert.Equal("7123456789012345681", undo.DestId);
    }

    [Fact]
    public void Parse_QuoteWithNumericTs_ReadsTsAsString()
    {
        string json = """
        { "data": { "msgs": [ { "msgId": "1003", "uidFrom": "111", "idTo": "222", "msgType": "webchat",
            "content": "tin tra loi gia lap",
            "quote": { "ownerId": "333", "cliMsgId": "4001", "globalMsgId": "5001", "cliMsgType": 31,
                       "ts": 1700000000123, "msg": "noi dung trich dan gia lap", "attach": "",
                       "fromD": "Nguoi Gia", "ttl": 0 } } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        MessageQuote quote = Assert.IsType<MessageQuote>(msg.Quote);
        Assert.Equal("1700000000123", quote.Timestamp);
        Assert.Equal("333", quote.OwnerId);
        Assert.Equal("4001", quote.CliMsgId);
        Assert.Equal("5001", quote.GlobalMsgId);
        Assert.Equal(31, quote.CliMsgType);
    }

    [Fact]
    public void Parse_PhotoWithPreviewThumb_ReadsOuterPreviewThumb()
    {
        string json = """
        { "data": { "msgs": [ { "msgId": "1004", "uidFrom": "111", "idTo": "222", "msgType": "chat.photo",
            "previewThumb": "https://example.zalo.me/preview-gia-lap",
            "content": { "title": "", "description": "", "href": "https://example.zalo.me/anh-gia-lap",
                         "thumb": "https://example.zalo.me/thumb-gia-lap", "childnumber": 0,
                         "action": "recommened.link", "params": "{}", "type": "photo" } } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        Assert.Equal("https://example.zalo.me/preview-gia-lap", msg.PreviewThumb);
        AttachmentContent attachment = Assert.IsType<AttachmentContent>(msg.Content);
        Assert.Equal("https://example.zalo.me/thumb-gia-lap", attachment.Thumb);
        Assert.Equal("photo", attachment.AttachmentType);
    }

    [Fact]
    public void Parse_MessageWithoutPreviewThumb_HasNullPreviewThumb()
    {
        string json = """
        { "data": { "msgs": [ { "msgId": "1005", "uidFrom": "111", "idTo": "222", "msgType": "webchat",
            "content": "xin chao" } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        Assert.Null(msg.PreviewThumb);
    }

    [Fact]
    public void Parse_FileShare_ParsesAttachmentContent()
    {
        string json = """
        { "data": { "msgs": [ { "msgId": "1006", "uidFrom": "111", "idTo": "222", "msgType": "share.file",
            "content": { "title": "tai-lieu-gia.pdf", "description": "", "href": "https://example.zalo.me/file-gia-lap",
                         "thumb": "", "childnumber": 0, "action": "", "params": "{}", "type": "file" } } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        Assert.Equal(MessageTypes.File, msg.MsgType);
        AttachmentContent attachment = Assert.IsType<AttachmentContent>(msg.Content);
        Assert.Equal("tai-lieu-gia.pdf", attachment.Title);
    }

    [Fact]
    public void Parse_TextWithPropertyExt_ReadsAllNumbersAndExt()
    {
        string json = """
        { "data": { "msgs": [ { "msgId": "1007", "uidFrom": "111", "idTo": "222", "msgType": "webchat",
            "content": "van ban gia lap",
            "propertyExt": { "color": 0, "size": 0, "type": 0, "subType": 0, "ext": "{}" } } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        Assert.Equal(MessageTypes.Text, msg.MsgType);
        MessagePropertyExt ext = Assert.IsType<MessagePropertyExt>(msg.PropertyExt);
        Assert.Equal(0, ext.Color);
        Assert.Equal("{}", ext.Ext);
    }

    [Fact]
    public void Parse_GroupMessageSentBySelf_IsSelfAndThreadIsGroup()
    {
        const string selfUid = "900000000000000001";
        string json = $$"""
        { "data": { "groupMsgs": [ { "msgId": "1008", "cliMsgId": "2008", "uidFrom": "{{selfUid}}", "idTo": "8800", "msgType": "webchat",
            "content": "nhom gia lap",
            "mentions": [ { "uid": "333", "pos": 0, "len": 4, "type": 0 } ] } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(521, json), selfUid).Messages);

        Assert.Equal(ThreadType.Group, msg.ThreadType);
        Assert.True(msg.IsSelf);
        Assert.Equal("8800", msg.ThreadId);
        MessageMention mention = Assert.Single(msg.Mentions);
        Assert.Equal("333", mention.Uid);
        Assert.Equal(4, mention.Len);
    }

    [Fact]
    public void Parse_GroupMessageFromOther_IsNotSelf()
    {
        const string selfUid = "900000000000000001";
        string json = """
        { "data": { "groupMsgs": [ { "msgId": "1009", "cliMsgId": "2009", "uidFrom": "555", "idTo": "8800", "msgType": "webchat",
            "content": "nhom gia lap khac" } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(521, json), selfUid).Messages);

        Assert.Equal(ThreadType.Group, msg.ThreadType);
        Assert.False(msg.IsSelf);
        Assert.Equal("8800", msg.ThreadId);
        Assert.Equal("555", msg.UidFrom);
        Assert.Empty(msg.Mentions);
    }

    [Fact]
    public void Parse_ObjectContentWithoutKnownKeys_IsUnknownContentAndKeepsRaw()
    {
        string json = """
        { "data": { "msgs": [ { "msgId": "1010", "uidFrom": "111", "idTo": "222", "msgType": "chat.khac",
            "content": { "truongLa": 1, "khac": "gia" } } ] } }
        """;

        ChatMessage msg = Assert.Single(MessageParser.ParseEvent(CreateEvent(501, json)).Messages);

        UnknownContent unknown = Assert.IsType<UnknownContent>(msg.Content);
        Assert.Equal(1, unknown.Raw.GetProperty("truongLa").GetInt32());
        Assert.Equal("UnknownContent", unknown.ToString());
    }

    [Fact]
    public void MessageTypes_HaveRealObservedValues()
    {
        Assert.Equal("webchat", MessageTypes.Text);
        Assert.Equal("chat.sticker", MessageTypes.Sticker);
        Assert.Equal("chat.photo", MessageTypes.Photo);
        Assert.Equal("share.file", MessageTypes.File);
        Assert.Equal("chat.undo", MessageTypes.Undo);
    }
}
