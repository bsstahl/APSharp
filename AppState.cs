using Microsoft.Extensions.Logging;

namespace Fedi;

public static class AppState
{
    public static string Origin { get; set; } = "https://localhost:65380";
    public static List<string> BlockedDomains { get; set; } = new();
    public static string? Name { get; set; }
    public static string? InviteCode { get; set; }
    public static string? UploadDir { get; set; }
    public static Database? Db { get; set; }
    public static ILogger? Logger { get; set; }
}
