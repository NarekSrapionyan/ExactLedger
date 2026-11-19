using el.Api.Errors;
using el.Application.Audit;
using Microsoft.AspNetCore.Http.Features;

namespace el.Api.Authentication;

public static class ApiSecurityExtensions
{
    public static WebApplication UseApiSecurity(this WebApplication app)
    {
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is null) { await ApiErrorResponses.WriteAsync(context, 404); return; }
            if (endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "Login")
            {
                if (context.Request.ContentLength > 4096) { await ApiErrorResponses.WriteAsync(context, 413); return; }
                var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 4096;
            }
            await next(context);
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var audit = context.RequestServices.GetRequiredService<AuditContext>();
                audit.ActorId = RequestIdentity.Get(context).Id;
                audit.CorrelationId = context.TraceIdentifier;
            }
            await next(context);
        });
        app.UseAuthorization();
        return app;
    }
}
