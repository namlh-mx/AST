using System.Text.Json;

namespace AST.Infrastructure.Security;

// On-disk JSON format for File A/B: camelCase keys matching spec §4 ({v, host, admins,...}). Shared by both stores (DRY).
internal static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
