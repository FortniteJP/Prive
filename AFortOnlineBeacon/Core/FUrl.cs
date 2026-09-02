using System.Net;
using System.Text;

namespace AFortOnlineBeacon.Core;

public class FUrl {
    private const string DefaultProtocol = "unreal";
    private static readonly IPAddress DefaultHost = IPAddress.Any;
    private const int DefaultPort = 7777;
    
    public string Protocol { get; set; } = DefaultProtocol;
    public IPAddress Host { get; set; } = DefaultHost;
    public int Port { get; set; } = DefaultPort;
    public string Map { get; set; } = "GearStart"; // TODO: UGameMapsSettings::GetGameDefaultMap()
    public string RedirectUrl { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new List<string>();
    public string Portal { get; set; } = string.Empty;
    public bool Valid { get; set; } = true;

    /// <summary>
    ///     FURL::FURL - splits `map?opt=a?opt=b#portal` into <see cref="Map"/>, <see cref="Options"/>
    ///     and <see cref="Portal"/>. Options are separated by '?', each carrying its own, which is
    ///     what the client sends:
    ///
    ///         /Game/Maps/Frontend?Name=dev?AuthTicket=e9dbeebb...?Platform=WIN?bIsFirstServerJoin=1
    ///
    ///     This is what the two "TODO: Implement proper FUrl constructor" sites in UWorld were
    ///     standing in for, and the stand-in threw the options away: the login path put the whole
    ///     option string inside Map and left Options empty, and the JOIN path built a blank FUrl and
    ///     handed that to the game mode. So AGameModeBase.Login has never seen a single option the
    ///     client sent - `?Name=` included, which is why the player name fell back to Player&lt;N&gt;
    ///     however correctly it was parsed.
    /// </summary>
    public static FUrl FromString(string url) {
        var result = new FUrl();

        var portalAt = url.IndexOf('#');
        if (portalAt >= 0) {
            result.Portal = url[(portalAt + 1)..];
            url = url[..portalAt];
        }

        var parts = url.Split('?');
        result.Map = parts[0];

        for (var i = 1; i < parts.Length; i++) {
            if (parts[i].Length > 0) result.Options.Add(parts[i]);
        }

        return result;
    }

    /// <summary>
    ///     Drops every option with this key, whatever its value. FURL::RemoveOption matches on the
    ///     KEY - List.Remove matches the whole "Key=Value" string, so the caller that removes
    ///     SplitscreenCount to stop a client specifying it was removing nothing at all.
    /// </summary>
    public void RemoveOption(string key) =>
        Options.RemoveAll(option =>
            option.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
            (option.Length == key.Length || option[key.Length] == '='));

    public string? GetOption(string match, string? defaultValue) {
        var len = match.Length;
        if (len > 0) {
            foreach (var option in Options) {
                if (option.StartsWith(match)) {
                    if (option[len - 1] == '=' || option[len] == '=' || option.Length == len) return option.Substring(len);
                }
            }
        }

        return defaultValue;
    }

    public string OptionsToString() {
        var optionsBuilder = new StringBuilder();
        
        foreach (var op in Options) {
            optionsBuilder.Append('?');
            optionsBuilder.Append(op);
        }

        return optionsBuilder.ToString();
    }
    
    public string ToString(bool fullyQualified) {
        var result = new StringBuilder();
        
        // Emit protocol.
        if ((Protocol != DefaultProtocol) || fullyQualified) {
            result.Append(Protocol);
            result.Append(':');

            if (!Equals(Host, DefaultHost)) result.Append("//");
        }
        
        // Emit host and port
        if (!Equals(Host, DefaultHost) || (Port != DefaultPort)) {
            result.Append(Port);

            if (!Map.StartsWith("/") && !Map.StartsWith("\\")) result.Append('/');
        }

        // Emit map.
        if (!string.IsNullOrEmpty(Map)) result.Append(Map);

        // Emit options.
        foreach (var option in Options) {
            result.Append('?');
            result.Append(option);
        }
        
        // Emit portal.
        if (!string.IsNullOrEmpty(Portal)) {
            result.Append('#');
            result.Append(Portal);
        }

        return result.ToString();
    }
    
    public override string ToString() => ToString(false);
}