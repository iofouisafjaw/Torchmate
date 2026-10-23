using System;
using System.Text.Json;

namespace TerrarAI.Content.Companion;

// Kept independent of Terraria so the wire contract can be exercised in isolation.
public static class CompanionProtocol
{
    public const int MaxCommandLength = 240;
    public const int MaxTextLength = 100;
    public const string SystemPrompt = """
        你是泰拉瑞亚同伴，只输出 JSON。先按 task 和工具选择 action，再根据 time 和 rules 选择 mode。
        rules 是本存档的规则列表，time 是当前时段。
        仅当规则 when 等于 time 或 any 时才适用。具体时段的确认规则优先于 any。
        采集时查看适用的 gather_confirmation：value=required 则 mode=ask；value=not_required 则 mode=act。
        rules 为空或没有适用规则时 mode=act。不要创造规则。其他时段的规则不适用。
        ask 提议具体 mine/chop 目标并等待玩家同意；act 直接行动。不能默认夜里都先问。
        需要确认时仍输出任务的 mine/chop，mode=ask。游戏自动生成确认问题并暂停，无需用 say 提问。
        action 只选一个：move(tileX,tileY)、mine(tileX,tileY)、chop(tileX,tileY)、say(text)、complete(message)。
        挖矿任务选择 mine，砍树任务选择 chop；mode 不改变任务的动作类型和目标。
        使用当前状态或任务中的绝对方块坐标。mine 挖 3×3 范围且需要 pickaxe>0；chop 需要 axe>0，二者自动靠近。
        缺少所需工具就选择 say 解释缺少什么工具，mode=act；move/say/complete 始终 mode=act。
        rules 中 key=mining/chopping 且 value=forbidden 禁止对应采集，选择 mode=act 和 say 说明原因。
        say 只用于缺工具、禁令或纯聊天，不能用来请求许可；complete 结束已完成任务。不要凭空声称成功。
        历史事件不是当前状态，address 是玩家称呼。
        格式示例，坐标必须改用当前任务的目标：
        挖矿且适用规则要求确认：{"action":{"type":"mine","params":{"tileX":80,"tileY":40}},"mode":"ask"}
        砍树且适用规则要求确认：{"action":{"type":"chop","params":{"tileX":80,"tileY":40}},"mode":"ask"}
        无确认规则的正常挖矿：{"action":{"type":"mine","params":{"tileX":80,"tileY":40}},"mode":"act"}
        挖矿但 pickaxe=0：{"action":{"type":"say","params":{"text":"需要一把镐。"}},"mode":"act"}
        /no_think
        """;

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "oneOf": [
            {
              "type": "object",
              "required": [
                "action",
                "mode"
              ],
              "additionalProperties": false,
              "properties": {
                "action": {
                  "oneOf": [
                    {
                      "type": "object",
                      "required": [
                        "type",
                        "params"
                      ],
                      "additionalProperties": false,
                      "properties": {
                        "type": {
                          "enum": [
                            "move",
                            "mine",
                            "chop"
                          ]
                        },
                        "params": {
                          "type": "object",
                          "required": [
                            "tileX",
                            "tileY"
                          ],
                          "additionalProperties": false,
                          "properties": {
                            "tileX": {
                              "type": "integer",
                              "minimum": 0
                            },
                            "tileY": {
                              "type": "integer",
                              "minimum": 0
                            }
                          }
                        }
                      }
                    },
                    {
                      "type": "object",
                      "required": [
                        "type",
                        "params"
                      ],
                      "additionalProperties": false,
                      "properties": {
                        "type": {
                          "const": "say"
                        },
                        "params": {
                          "type": "object",
                          "required": [
                            "text"
                          ],
                          "additionalProperties": false,
                          "properties": {
                            "text": {
                              "type": "string",
                              "minLength": 1,
                              "maxLength": 100
                            }
                          }
                        }
                      }
                    },
                    {
                      "type": "object",
                      "required": [
                        "type",
                        "params"
                      ],
                      "additionalProperties": false,
                      "properties": {
                        "type": {
                          "const": "complete"
                        },
                        "params": {
                          "type": "object",
                          "required": [
                            "message"
                          ],
                          "additionalProperties": false,
                          "properties": {
                            "message": {
                              "type": "string",
                              "minLength": 1,
                              "maxLength": 100
                            }
                          }
                        }
                      }
                    }
                  ]
                },
                "mode": {
                  "const": "act"
                }
              }
            },
            {
              "type": "object",
              "required": [
                "action",
                "mode"
              ],
              "additionalProperties": false,
              "properties": {
                "action": {
                  "type": "object",
                  "required": [
                    "type",
                    "params"
                  ],
                  "additionalProperties": false,
                  "properties": {
                    "type": {
                      "enum": [
                        "mine",
                        "chop"
                      ]
                    },
                    "params": {
                      "type": "object",
                      "required": [
                        "tileX",
                        "tileY"
                      ],
                      "additionalProperties": false,
                      "properties": {
                        "tileX": {
                          "type": "integer",
                          "minimum": 0
                        },
                        "tileY": {
                          "type": "integer",
                          "minimum": 0
                        }
                      }
                    }
                  }
                },
                "mode": {
                  "const": "ask"
                }
              }
            }
          ]
        }
        """).RootElement.Clone();

    public static CompanionDecision Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        bool hasMode = root.TryGetProperty("mode", out var modeElement);
        RequireKeys(root, hasMode ? new[] { "mode", "action" } : new[] { "action" });
        string mode = hasMode && modeElement.ValueKind == JsonValueKind.String ? modeElement.GetString()! : "unspecified";
        if (hasMode && mode is not ("act" or "ask")) throw new FormatException("Unsupported interaction mode.");
        var action = root.GetProperty("action");
        RequireKeys(action, "type", "params");
        if (action.GetProperty("type").ValueKind != JsonValueKind.String)
            throw new FormatException("Action type must be a string.");
        var type = action.GetProperty("type").GetString()!;
        var parameters = action.GetProperty("params");
        if (type is "move" or "mine" or "chop")
        {
            RequireKeys(parameters, "tileX", "tileY");
            foreach (var key in new[] { "tileX", "tileY" })
                if (!parameters.GetProperty(key).TryGetInt32(out var value) || value < 0)
                    throw new FormatException("Coordinates must be non-negative integers.");
        }
        else if (type is "say" or "complete")
        {
            var key = type == "say" ? "text" : "message";
            RequireKeys(parameters, key);
            var value = parameters.GetProperty(key);
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                || value.GetString()!.Length > MaxTextLength)
                throw new FormatException("Action text must contain 1–100 characters.");
        }
        else throw new FormatException("Unsupported companion action: " + type);
        if (mode == "ask" && type is not ("mine" or "chop")) throw new FormatException("Only collection targets can request approval.");
        string canonical = hasMode ? JsonSerializer.Serialize(new { mode, action }) : JsonSerializer.Serialize(new { action });
        return new CompanionDecision(type, parameters.Clone(), canonical, mode);
    }

    private static void RequireKeys(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) throw new FormatException("Expected an object.");
        int count = 0;
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var property in obj.EnumerateObject())
        {
            count++;
            if (Array.IndexOf(keys, property.Name) < 0 || !seen.Add(property.Name))
                throw new FormatException("Unexpected or duplicate field: " + property.Name);
        }
        if (count != keys.Length) throw new FormatException("Missing required action fields.");
    }

    public static string Clip(string? text, int limit) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= limit ? text : text[..limit];
}

public sealed record CompanionDecision(string Type, JsonElement Parameters, string Json, string Mode = "unspecified");
