namespace el.Api.Errors;

public static class ApiErrorHandlingExtensions
{
    public static IServiceCollection AddApiErrorHandling(this IServiceCollection services)
    {
        // Default request-start logs contain raw query strings, which callers may misuse for secrets.
        services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning));
        services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware", LogLevel.Warning));
        services.AddProblemDetails();
        services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<ApiExceptionHandler>();
        return services;
    }

    public static IApplicationBuilder UseApiErrorHandling(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            // Correlation IDs are server-generated; untrusted headers cannot inject log content.
            var correlationId = Guid.NewGuid().ToString("N");
            context.TraceIdentifier = correlationId;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Correlation-ID"] = correlationId;
                return Task.CompletedTask;
            });
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("el.Api.Request");
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
            await next(context);
            if (context.Response.StatusCode is >= 400 and < 500)
                logger.LogWarning("API request rejected ({StatusCode}). CorrelationId: {CorrelationId}",
                    context.Response.StatusCode, correlationId);
        });

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            // The handler records safe metadata; framework exception diagnostics
            // would also record the original exception message, potentially leaking secrets.
            SuppressDiagnosticsCallback = _ => true,
        });
        app.UseStatusCodePages(context =>
            ApiErrorResponses.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode));
        return app;
    }
}
