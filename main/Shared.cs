using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Linq;
using System.IO;
using OtpNet;
using Microsoft.Extensions.Configuration;
using Controllers.Environment;
using Microsoft.AspNetCore.StaticFiles;

namespace Internal.Shared;

public class DefaultAvatar 
{
    public string mime_type {get; set;}
    public string extension {get; set;}
    public long file_size {get; set;}
}

public class SharedMethods
{
    private static EnvironmentService Envir;
    private static IConfiguration configuration;
    private static DefaultAvatar[] DefaultAvatarInformation = new DefaultAvatar[5];
    private static readonly Dictionary<int, string> ServerTagImageUrls = new()
    {
        [1] = "https://discord.com"
    };
    private static readonly HashSet<string> AllowedMime = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };
    public static readonly Dictionary<string, string> TypeInfo = new(StringComparer.OrdinalIgnoreCase)
    {
        {"Avatar", "avatar_uploads"},
        {"RoleIcons", "role_icon_uploads"},
        {"Webhook", "webhook_uploads"},
        {"Reaction", "reaction_uploads"}
    };
    private readonly WebSocketSessionManager Manager;
    private readonly WebSocketChannelIdConnections websocketconns_;
    private readonly ServerIdUserIdConnections ServerIdUserIdConnections_;

    public SharedMethods(IConfiguration configuration_, EnvironmentService Envir_, WebSocketSessionManager manager, WebSocketChannelIdConnections  websocketconns, ServerIdUserIdConnections ServerIdUserIdConnectionn)
    {
        Manager = manager;
        websocketconns_ = websocketconns;
        configuration = configuration_;
        ServerIdUserIdConnections_ = ServerIdUserIdConnectionn;
        Envir = Envir_;
    }

    public Dictionary<string, string> UploadsInfo ()
    {
        return TypeInfo;
    }

    public class ServerIdUserIdConnections
    {
        public ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ServerIdUsers = new(); 
    }

    public class WebSocketChannelIdConnections
    {
        public ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ChannelUsers = new();
    }

    public class WebSocketSessionManager 
    {
        public ConcurrentDictionary<string, WebSocket> Users = new();
    }

    public class WebSocketSessionIds 
    {
        public ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> SessionIds = new();
    }

    public async Task SendSocketMessage(Guid? DiscordChannelId, string? SocketJSONType, bool? ChannelIdsProvided = null, ConcurrentDictionary<string, byte>? ChannelIdDict = null, bool? ServerWideNotification = null)
    {
        ConcurrentDictionary<string, byte> ChannelIds;

        if (ChannelIdsProvided == true)
        {
            ChannelIds = ChannelIdDict;
        } else
        {
            if (ServerWideNotification == null)
            {
                if (!websocketconns_.ChannelUsers.TryGetValue(DiscordChannelId.ToString(), out ChannelIds))
                {
                    return;
                }
            } else
            {
                if (!ServerIdUserIdConnections_.ServerIdUsers.TryGetValue(DiscordChannelId.ToString(), out ChannelIds))
                {
                    return;
                }
            }
        }

        var SocketType = Encoding.UTF8.GetBytes(SocketJSONType);
        var SocketTypeBuffer = new ArraySegment<byte> (SocketType);
        var MessageTasks = new List<Task>();

        async Task SendMessage(string ChannelUserId)
        {
            if (Manager.Users.TryGetValue(ChannelUserId, out var UserSocket)) {
                try {
                    if (UserSocket.State != WebSocketState.Open)
                    {
                        UserSocket.Dispose();
                        Manager.Users.TryRemove(ChannelUserId, out _);
                        return;
                    }

                    await UserSocket.SendAsync(SocketTypeBuffer, WebSocketMessageType.Text, true, CancellationToken.None);
                } catch
                {
                    try
                    {
                        if (UserSocket.State == WebSocketState.CloseReceived || UserSocket.State == WebSocketState.Open)
                        {
                            await UserSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Internal Socket Error.", CancellationToken.None);
                        }
                    }
                    finally
                    {
                        UserSocket.Dispose();
                        Manager.Users.TryRemove(ChannelUserId, out _);
                    }
                }
            }
        }

        foreach (var ChannelUserId in ChannelIds.Keys)
        {
            MessageTasks.Add(SendMessage(ChannelUserId));
        } 

        await Task.WhenAll(MessageTasks);
    }

    public static bool AllowedExtension(string Extension)
    {
        if (!AllowedExtensions.Contains(Extension)) {
            return false;
        }

        return true;
    }

    public static bool IsAllowedMime(string Mime)
    {
        if (!AllowedMime.Contains(Mime)) {
            return false;
        }

        return true;
    }

    public static string GetServerTagImageUrl(int ServerTagId)
    {
        if (ServerTagImageUrls.TryGetValue(ServerTagId, out var ServerTagImageUrl))
        {
            return ServerTagImageUrl;
        }

        return "Unknown Id";
    }

    public static string Get2FACode (string Secret)
    {
        var SecretBytes = Base32Encoding.ToBytes(Secret);
        var Totp = new Totp(SecretBytes);
        var Code = Totp.ComputeTotp();
        return Code;
    }

    public static bool Verify2FACode (string UserEnteredCode, string Secret)
    {
        var SecretBytes = Base32Encoding.ToBytes(Secret);
        var Totp = new Totp(SecretBytes);
        return Totp.VerifyTotp(UserEnteredCode, out long timeStepMatched, VerificationWindow.RfcSpecifiedNetworkDelay);
    }

    public static void InitializeDefaultAvatarInformation () 
    {
        var MainProjectDir = Envir.GetEnvironmentPath();
        var DefaultAvatarsPath = Path.Combine(MainProjectDir, "defaultavatars");
        string[] AvatarDefaultPaths = Directory.GetFiles(DefaultAvatarsPath);
        var provider = new FileExtensionContentTypeProvider();

        for (var i = 0; i < AvatarDefaultPaths.Length; ++i) 
        {
            var StringPath = AvatarDefaultPaths[i];
            FileInfo AvatarFileInfo = new FileInfo(StringPath);

            if (AvatarFileInfo.Exists) 
            {
                var FileSize = AvatarFileInfo.Length;
                var MimeType = "";

                if (provider.TryGetContentType(StringPath, out var contentType))
                {
                    MimeType = contentType; 
                }

                if (MimeType.Length > 0) 
                {
                    DefaultAvatarInformation[i] = new DefaultAvatar
                    {
                        mime_type = MimeType,
                        extension = Path.GetExtension(StringPath),
                        file_size = FileSize
                    };
                }
            }
        }
    }

    public static DefaultAvatar GetInfoOffIndex (int Index) 
    {
        return DefaultAvatarInformation[Index];
    }
}
