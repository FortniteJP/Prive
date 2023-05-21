namespace Prive.Server.Http;

public class EpicError {
    [K("errorCode")] public string ErrorCode { get; init; } = "unknown";
    [K("errorMessage")] public string ErrorMessage { get; init; } = "Error";
    [K("messageVars")] public string[] MessageVars { get; init; } = Array.Empty<string>();
    [K("numericErrorCode")] public int NumericErrorCode { get; init; }
    [K("originatingService")] public string OriginatingService { get; init; } = "unknown";
    [K("intent")] public string Intent { get; init; } = "prod";

    public static EpicError Create(string code, int numericCode, string message, string service, string? intent = null, string[]? variables = null) => new() {
        ErrorCode = code,
        ErrorMessage = message,
        NumericErrorCode = numericCode,
        OriginatingService = service,
        Intent = intent ?? "prod",
        MessageVars = variables ?? Array.Empty<string>()
    };

    public static EpicError Method(string service, string? intent = null) => new() {
        ErrorCode = "errors.com.epicgames.common.method_not_allowed",
        ErrorMessage = "Sorry the resource you were trying to access cannot be accessed with the HTTP method you used.",
        NumericErrorCode = 1009,
        OriginatingService = service,
        Intent = intent ?? "prod"
    };

    public static EpicError Permission(string permission, string type, string service, string? intent = null) => new() {
        ErrorCode = "errors.com.epicgames.common.oauth.permission_denied",
        ErrorMessage = $"Sorry your login does not posses the permissions '{permission} {type}' needed to perform the requested operation",
        NumericErrorCode = 1023,
        OriginatingService = service,
        Intent = intent ?? "prod"
    };
}