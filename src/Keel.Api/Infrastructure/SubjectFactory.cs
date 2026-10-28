using System.Security.Claims;
using Keel.Core;
using Keel.Security;

namespace Keel.Api.Infrastructure;

public static class SubjectFactory
{
    public static Subject? FromPrincipal(ClaimsPrincipal principal) => SubjectClaims.FromPrincipal(principal);

    public static Subject Require(HttpContext context) =>
        FromPrincipal(context.User) ?? throw new InvalidOperationException("Endpoint requires an authenticated subject.");
}
