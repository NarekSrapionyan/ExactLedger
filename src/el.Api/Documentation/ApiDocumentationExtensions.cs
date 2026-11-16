using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace el.Api.Documentation;

public static class ApiDocumentationExtensions
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "ExactLedger API";
                document.Info.Version = "v1";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
                document.Security = [new OpenApiSecurityRequirement
                { [new OpenApiSecuritySchemeReference("Bearer", document, null)] = [] }];

                foreach (var name in new[] { "ProblemDetails", "HttpValidationProblemDetails" })
                {
                    if (document.Components.Schemas is not { } schemas || !schemas.TryGetValue(name, out var item)
                        || item is not OpenApiSchema schema) continue;
                    schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
                    schema.Description = "Shared API error contract for HTTP 400, 401, 403, 404 and business-state 409 conflicts; validation errors also include field errors.";
                    schema.Required ??= new HashSet<string>();
                    foreach (var field in new[] { "code", "message", "correlationId" })
                    {
                        schema.Properties[field] = new OpenApiSchema { Type = JsonSchemaType.String };
                        schema.Required.Add(field);
                    }
                }
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
                    operation.Security = [];
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
