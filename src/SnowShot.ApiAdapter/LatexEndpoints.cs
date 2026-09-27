using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SnowShot.Api.Resources;
using SnowShot.Application;
using SnowShot.Contracts;

namespace SnowShot.Api;

internal static class LatexEndpoints
{
    public static IEndpointRouteBuilder MapLatexEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/latex/extract", ExtractAsync).WithName("LatexExtraction").WithTags("Latex")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<AppEnvelope>(StatusCodes.Status200OK)
            .Produces<PublicProblem>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status502BadGateway, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status504GatewayTimeout, "application/problem+json");
        return endpoints;
    }

    private static async Task ExtractAsync(HttpContext context, LatexUseCase useCase, LatexRequestLimits limits,
        PublicMessages messages,
        [FromHeader(Name = "X-Request-ID"), StringLength(64), RegularExpression(@"^[\x21-\x7E]+$")]
        string? suppliedRequestId,
        CancellationToken cancellationToken)
    {
        PooledImageBuffer? image = null;
        try
        {
            image = await ImageMultipartReader.ReadAsync(context.Request, limits.MaximumUploadBytes, cancellationToken);
        }
        catch (ImagePayloadTooLargeException)
        {
            await ApiResponse.Problem(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                messages["Invalid latex image request"]).ExecuteAsync(context);
            return;
        }
        catch (ImageMultipartException)
        {
            await ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request",
                messages["Invalid latex image request"]).ExecuteAsync(context);
            return;
        }

        using (image)
        {
            if (!RequestContextFactory.TryCreate(context, messages, out var requestContext, out var requestError))
            {
                await requestError!.ExecuteAsync(context); return;
            }
            context.Response.Headers["X-Request-ID"] = requestContext.ClientRequestId;
            var execution = await useCase.ExecuteAsync(requestContext,
                new LatexCommand(image.Memory), cancellationToken);
            if (execution.IsSuccess)
            {
                var result = execution.Value!;
                var response = result.Status switch
                {
                    LatexExtractionStatus.Success => ApiResponse.Success(new LatexExtractionData(result.Latex!), messages),
                    LatexExtractionStatus.InvalidRequest => ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request", messages["Invalid latex image request"]),
                    LatexExtractionStatus.NoFormula => ApiResponse.Problem(context, StatusCodes.Status422UnprocessableEntity, "no_formula", messages["Latex extraction failed"]),
                    LatexExtractionStatus.InferenceFailed => ApiResponse.Problem(context, StatusCodes.Status502BadGateway, "inference_failed", messages["Latex extraction failed"]),
                    LatexExtractionStatus.Timeout => ApiResponse.Problem(context, StatusCodes.Status504GatewayTimeout, "deadline_exceeded", messages["Latex extraction service unavailable"]),
                    LatexExtractionStatus.Busy => ApiResponse.Problem(context, StatusCodes.Status503ServiceUnavailable, "worker_busy", messages["Latex extraction service unavailable"], TimeSpan.FromSeconds(1)),
                    _ => ApiResponse.Problem(context, StatusCodes.Status503ServiceUnavailable, "latex_worker_unavailable", messages["Latex extraction service unavailable"]),
                };
                await response.ExecuteAsync(context);
                return;
            }
            await ApiResponse.ApplicationProblem(context, execution.Error!, messages).ExecuteAsync(context);
        }
    }
}
