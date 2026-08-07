using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SnowShot.Api.Resources;
using SnowShot.Application;
using SnowShot.Contracts;

namespace SnowShot.Api;

internal static class TableEndpoints
{
    public static IEndpointRouteBuilder MapTableEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/table/extract", ExtractAsync).WithName("TableExtraction").WithTags("Table")
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

    private static async Task ExtractAsync(HttpContext context, TableUseCase useCase, TableRequestLimits limits,
        PublicMessages messages,
        [FromHeader(Name = "X-Request-ID"), StringLength(64), RegularExpression(@"^[\x21-\x7E]+$")]
        string? suppliedRequestId,
        CancellationToken cancellationToken)
    {
        PooledImageBuffer? image = null;
        try
        {
            image = await TableMultipartReader.ReadAsync(context.Request, limits.MaximumUploadBytes, cancellationToken);
        }
        catch (TablePayloadTooLargeException)
        {
            await ApiResponse.Problem(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                messages["Invalid table image request"]).ExecuteAsync(context);
            return;
        }
        catch (TableMultipartException)
        {
            await ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request",
                messages["Invalid table image request"]).ExecuteAsync(context);
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
                new TableCommand(image.Memory), cancellationToken);
            if (execution.IsSuccess)
            {
                var result = execution.Value!;
                var response = result.Status switch
                {
                    TableExtractionStatus.Success => ApiResponse.Success(new TableExtractionData(result.Html!), messages),
                    TableExtractionStatus.InvalidRequest => ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request", messages["Invalid table image request"]),
                    TableExtractionStatus.NoTable => ApiResponse.Problem(context, StatusCodes.Status422UnprocessableEntity, "no_table", messages["Table extraction failed"]),
                    TableExtractionStatus.InferenceFailed => ApiResponse.Problem(context, StatusCodes.Status502BadGateway, "inference_failed", messages["Table extraction failed"]),
                    TableExtractionStatus.Timeout => ApiResponse.Problem(context, StatusCodes.Status504GatewayTimeout, "deadline_exceeded", messages["Table extraction service unavailable"]),
                    TableExtractionStatus.Busy => ApiResponse.Problem(context, StatusCodes.Status503ServiceUnavailable, "worker_busy", messages["Table extraction service unavailable"], TimeSpan.FromSeconds(1)),
                    _ => ApiResponse.Problem(context, StatusCodes.Status503ServiceUnavailable, "table_worker_unavailable", messages["Table extraction service unavailable"]),
                };
                await response.ExecuteAsync(context);
                return;
            }
            await ApiResponse.ApplicationProblem(context, execution.Error!, messages).ExecuteAsync(context);
        }
    }
}
