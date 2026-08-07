using SnowShot.Api.Resources;
using SnowShot.Application;
using SnowShot.Contracts;

namespace SnowShot.Api;

internal static class TranslationEndpoints
{
    public static IEndpointRouteBuilder MapTranslationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v2/translation/translate", TranslateAsync).WithName("Translate").WithTags("Translation")
            .Accepts<TranslationRequest>("application/json")
            .Produces<AppEnvelope>(StatusCodes.Status200OK)
            .Produces<PublicProblem>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status502BadGateway, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status504GatewayTimeout, "application/problem+json");
        endpoints.MapGet("/api/v2/translation/types", Types).WithName("TranslationTypes").WithTags("Translation")
            .Produces<AppEnvelope>(StatusCodes.Status200OK);
        return endpoints;
    }

    private static IResult Types(PublicMessages messages) => ApiResponse.Success(
        new[] { new TranslationTypeOption(TranslationType.AI, messages["AI Translation"]) }, messages);

    private static async Task TranslateAsync(HttpContext context, TranslationUseCase useCase,
        PublicMessages messages,
        [FromHeader(Name = "X-Request-ID"), StringLength(64), RegularExpression(@"^[\x21-\x7E]+$")]
        string? suppliedRequestId,
        CancellationToken cancellationToken)
    {
        var read = await ApiResponse.ReadJsonAsync<TranslationRequest>(context, 512 * 1024,
            messages["Validation failed"], cancellationToken);
        if (read.Error is not null) { await read.Error.ExecuteAsync(context); return; }
        var wire = read.Value!;
        if (!Enum.IsDefined(wire.Type))
        {
            await ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request",
                messages["Validation failed"]).ExecuteAsync(context);
            return;
        }
        var command = new TranslationCommand(wire.Content, wire.From, wire.To, wire.Domain);
        var errors = TranslationUseCase.Validate(command);
        if (errors.Count > 0)
        {
            await ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request",
                string.Join("; ", errors.Select(messages.Validation))).ExecuteAsync(context);
            return;
        }
        if (!RequestContextFactory.TryCreate(context, messages, out var requestContext, out var requestError))
        {
            await requestError!.ExecuteAsync(context); return;
        }

        var result = await useCase.ExecuteAsync(requestContext, command, cancellationToken);
        if (result.IsSuccess)
        {
            var value = result.Value!;
            await ApiResponse.Success(new TranslationResponseData(
                value.Results.Select(item => new TranslationContent(item)).ToArray(), value.From, value.To), messages).ExecuteAsync(context);
            return;
        }
        await ApiResponse.ApplicationProblem(context, result.Error!, messages).ExecuteAsync(context);
    }
}
