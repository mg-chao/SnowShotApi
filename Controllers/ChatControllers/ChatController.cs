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

namespace SnowShotApi.Controllers.ChatControllers;

public class ChatRequestMessage
{
    [Required]
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [Required]
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}
public class ChatRequest
{
    [Required]
    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    [JsonPropertyName("messages")]
    [MaxLength(20)]
    public List<ChatRequestMessage> Messages { get; set; } = [];

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
    private readonly DeepseekApiEnv _deepseekApiEnv = new();
    private readonly ClaudeApiEnv _claudeApiEnv = new();
    private readonly List<ChatModel> _models = [.. ChatModelAttribute.ValidModels.Select(model => {
        var thinking = false;
        if (model == "deepseek-reasoner" || model.EndsWith("_thinking"))
        {
            thinking = true;
        }

        return new ChatModel
        {
            Model = model,
            Name = ChatModelAttribute.ConvertToText(model, localizer),
            Thinking = thinking,
        };
    })];

    [HttpPost("chat/completions")]
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

        // 判断用户是否达到限额
        var stats = await chatOrderStatsService.GetAsync(user.Id, chatRequest.Model);
        int tokenLimit = chatRequest.Model.StartsWith("claude")
            ? _claudeApiEnv.TokensLimit
            : _deepseekApiEnv.TokensLimit;

        if (chatRequest.Model.StartsWith("claude-3-5-haiku"))
        {
            tokenLimit = _claudeApiEnv.HaikuTokensLimit;
        }
        else if (chatRequest.Model.StartsWith("claude-3-7-sonnet"))
        {
            tokenLimit = _claudeApiEnv.SonnetTokensLimit;
        }

        if (stats != null && stats.PromptTokensSum + stats.CompletionTokensSum >= tokenLimit)
        {
            await ChatService.ChatError(Response, HttpStatusCode.TooManyRequests, new ChatError
            {
                Message = _localizer["User chat limit reached"],
                Type = "limit_requests",
                Code = "limit_requests"
            });
            return;
        }

        await chatService.StreamChatCompletion(chatRequest, Response, user.Id);
    }

    [HttpGet("models")]
    public IActionResult GetModels()
    {
        return Success(_models);
    }
}