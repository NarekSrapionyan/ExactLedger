using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using el.Domain.Common;

namespace el.Api.Documentation;

public static class ApiDocumentationExtensions
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type != typeof(Money)) return Task.CompletedTask;
                schema.Type = JsonSchemaType.Object | (schema.Type.GetValueOrDefault() & JsonSchemaType.Null);
                schema.Description = "Exact money; amount is a canonical decimal string in the currency's precision.";
                schema.Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["amount"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "For example 250.00 USD, 250 JPY or 250.000 KWD." },
                    ["currency"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "USD, JPY or KWD." },
                };
                schema.Required = new HashSet<string> { "amount", "currency" };
                schema.AdditionalPropertiesAllowed = false;
                return Task.CompletedTask;
            });
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
                    schema.Description = "API error with code, message and correlationId; validation errors also include field errors.";
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
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().Any(item => item.EndpointName == "ConfirmClearingBatch"))
                {
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["text/csv"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String } },
                        },
                    };
                }
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().Any(item => item.EndpointName is "SubmitSettlement" or "RefundSettlement" or "ExportFinancialReport"))
                {
                    operation.Parameters ??= new List<IOpenApiParameter>();
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true,
                        Description = "Stable client command key. Reuse with identical input for a safe retry.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128 },
                    });
                }
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().Any(item => item.EndpointName is "ReadSettlement" or "ReadSettlementTrace") &&
                    operation.Responses is not null && operation.Responses.TryGetValue("200", out var response) &&
                    response.Content is not null && response.Content.TryGetValue("application/json", out var media))
                {
                    media.Schema = new OpenApiSchema { OneOf =
                        [new OpenApiSchemaReference("FullSettlementResponse"), new OpenApiSchemaReference("PayeeSettlementResponse")] };
                }
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
                    operation.Security = [];
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
