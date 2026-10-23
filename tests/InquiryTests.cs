using System.Text.Json;
using TerrarAI.Content.Companion;

static class InquiryTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        var keys = new TextEntryKeys();
        keys.Reset(false, false);
        check(keys.Consume(false, false, true) == TextEntryAction.None &&
            keys.Consume(true, false, false) == TextEntryAction.None,
            "Enter that commits an IME composition does not also send the message");
        keys.Consume(false, false, false);
        check(keys.Consume(true, false, false) == TextEntryAction.Submit &&
            keys.Consume(true, false, false) == TextEntryAction.None,
            "fresh Enter submits once and holding it cannot duplicate the message");
        keys.Reset(true, false);
        check(keys.Consume(true, false, false) == TextEntryAction.None,
            "taking focus while Enter is held does not send a draft");
        keys.Consume(false, false, true);
        check(keys.Consume(false, true, true) == TextEntryAction.None,
            "Escape during IME composition stays with the IME");
        keys.Consume(false, false, false);
        check(keys.Consume(false, true, false) == TextEntryAction.Blur,
            "fresh Escape outside composition releases text focus");
        check(InquiryProtocol.Parse("""{"reply":"先搭一个安全的小屋。"}""") == "先搭一个安全的小屋。",
            "read-only inquiry accepts a bounded answer");
        foreach (var raw in new[] { "[]", "null", "{}", """{"reply":""}""", """{"reply":12}""",
            """{"reply":"好","action":{"type":"mine"}}""", """{"reply":"好","updates":[]}""",
            """{"reply":"好","reply":"另一个"}""", JsonSerializer.Serialize(new { reply = new string('a', 601) }) })
        {
            bool rejected = false;
            try { InquiryProtocol.Parse(raw); } catch { rejected = true; }
            check(rejected, "inquiry rejects empty, malformed, excessive or executable payload");
        }
        var handler = new FakeModelHandler("""{"reply":"没有提供箱子信息。"}""");
        using var client = new LocalModelClient(new HttpClient(handler));
        string response = await client.SendStructuredAsync(new ModelOptions("http://localhost/test", "qwen3-4b", "", 0, 256, 10),
            InquiryProtocol.SystemPrompt, "{\"question\":\"箱子里有什么\"}", InquiryProtocol.Schema, "companion_inquiry", default);
        using var payload = JsonDocument.Parse(handler.Payloads.Single());
        var contract = payload.RootElement.GetProperty("response_format").GetProperty("json_schema");
        check(contract.GetProperty("name").GetString() == "companion_inquiry" &&
            contract.GetProperty("schema").GetProperty("properties").EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "reply" }) &&
            InquiryProtocol.Parse(response).Contains("箱子"), "inquiry transport uses its answer-only contract");
    }
}
