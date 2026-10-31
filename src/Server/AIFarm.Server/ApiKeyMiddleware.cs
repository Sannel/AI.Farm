using System.Security.Cryptography;
using System.Text;

namespace AIFarm.Server;

/// <summary>
/// Protects the agent-facing endpoints (MCP and the memory APIs) with a shared key when
/// "Security:ApiKey" is set: requests must send "Authorization: Bearer &lt;key&gt;" or
/// "X-Api-Key: &lt;key&gt;". With no key configured everything is open (local development).
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
	public static readonly PathString[] ProtectedPaths = ["/mcp", "/api/agents", "/api/entities"];

	public async Task InvokeAsync(HttpContext context)
	{
		var key = configuration["Security:ApiKey"];
		if (!string.IsNullOrEmpty(key) && ProtectedPaths.Any(p => context.Request.Path.StartsWithSegments(p))
			&& !Matches(Presented(context.Request), key))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.Response.Headers.WWWAuthenticate = "Bearer";
			return;
		}
		await next(context);
	}

	private static string? Presented(HttpRequest request)
	{
		var authorization = request.Headers.Authorization.ToString();
		if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
		{
			return authorization["Bearer ".Length..].Trim();
		}
		var header = request.Headers["X-Api-Key"].ToString();
		return header.Length > 0 ? header : null;
	}

	private static bool Matches(string? presented, string key) =>
		presented is not null
		&& CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(key));
}
