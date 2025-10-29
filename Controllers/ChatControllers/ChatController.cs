using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.AppEnvs;
using SnowShotApi.Data;
using System.ComponentModel.DataAnnotations;
using System.Net;
using SnowShotApi.Services.ChatServices;
using SnowShotApi.Services.OrderServices;
using SnowShotApi.Services.UserServices;
using SnowShotApi.RequestValidations;
using System.Text.Json;

namespace SnowShotApi.Controllers.ChatControllers;

// 支持多模态内容的类
public class ChatContentImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public class ChatContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public ChatContentImageUrl? ImageUrl { get; set; }
}

public class ChatRequestMessage
{
    [Required]
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [Required]
    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }
}
public class ChatRequest
{
    [Required]
    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    [JsonPropertyName("messages")]
    [MaxLength(20)]
    public List<ChatRequestMessage> Messages { get; set; } = [];

    [JsonPropertyName("enable_thinking")]
    public bool EnableThinking { get; set; } = false;

    [JsonPropertyName("temperature")]
    [Range(0, 2)]
    public double Temperature { get; set; } = 1;

    [JsonPropertyName("max_tokens")]
    [Range(512, 8192)]
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("thinking_budget_tokens")]
    [Range(1024, 8192)]
    public int ThinkingBudgetTokens { get; set; } = 4096;
}

public class ChatModel
{
    [JsonPropertyName("model")]
    [ChatModel]
    public string Model { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("thinking")]
    public bool Thinking { get; set; } = false;

    [JsonPropertyName("support_vision")]
    public bool SupportVision { get; set; } = false;
}
[ApiController]
[Route("api/v1/[controller]")]
public class ChatController(
    ApplicationDbContext context,
    IStringLocalizer<AppControllerBase> localizer,
    IIpUserService ipUserService,
    IChatService chatService,
    IChatOrderStatsService chatOrderStatsService) : AppControllerBase(context, localizer)
{
    private readonly List<ChatModel> _models = [.. ChatModelAttribute.ValidModels.Select(model => {
        return new ChatModel
        {
            Model = model.Key,
            Name = ChatModelAttribute.ConvertToText(model.Key, localizer),
            Thinking = model.Value.SupportThinking,
            SupportVision = model.Value.SupportVision,
        };
    })];

    [HttpPost("chat/completions")]
    public async Task OldChatCompletions([FromBody] ChatRequest chatRequest)
    {
        await ChatService.ChatError(Response, HttpStatusCode.BadRequest, new ChatError
        {
            Message = _localizer["Upgrade tip"],
            Type = "upgrade",
            Code = "upgrade"
        });
        return;
    }

    [HttpPost("completions")]
    public async Task ChatCompletions([FromBody] ChatRequest chatRequest)
    {

        var user = await ipUserService.GetUserAsync(HttpContext);
        if (user == null)
        {
            await ChatService.ChatError(Response, HttpStatusCode.BadRequest, new ChatError
            {
                Message = _localizer["Cannot get client IP address"],
                Type = "invalid_parameter_error",
                Code = "invalid_parameter_error"
            });
            return;
        }

        if (!ChatModelAttribute.ValidModels.TryGetValue(chatRequest.Model, out var modelInfo) || modelInfo == null)
        {
            await ChatService.ChatError(Response, HttpStatusCode.BadRequest, new ChatError
            {
                Message = $"The model `{chatRequest.Model}` does not exist or you do not have access to it.",
                Type = "invalid_request_error",
                Code = "model_not_found",
                Param = "model"
            });
            return;
        }

        // 判断用户是否达到限额
        if (await chatOrderStatsService.IsLimitIpUserAsync(user.Id, chatRequest.Model))
        {
            await ChatService.ChatError(Response, HttpStatusCode.TooManyRequests, new ChatError
            {
                Message = _localizer["User chat limit reached"],
                Type = "limit_requests",
                Code = "limit_requests"
            });
            return;
        }

        Console.WriteLine(modelInfo);
        Console.WriteLine(chatRequest);

        await chatService.StreamChatCompletion(chatRequest, Response, user.Id);
    }

    [HttpGet("models")]
    public IActionResult GetModels()
    {
        return Success(_models);
    }
}