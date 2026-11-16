using System.Security.Claims;
using el.Application.Security;

namespace el.Api.Authentication;

public static class RequestIdentity
{
    public static AuthenticatedIdentity Get(HttpContext context) => new(
        context.User.FindFirstValue("sub") ?? throw new InvalidOperationException("An authenticated subject is required."),
        context.User.FindFirstValue("name") ?? string.Empty,
        Array.AsReadOnly(context.User.FindAll("role").Select(claim => claim.Value).ToArray()),
        context.User.FindFirstValue("client_id"),
        Guid.TryParse(context.User.FindFirstValue("party_id"), out var partyId) ? partyId : null);
}
