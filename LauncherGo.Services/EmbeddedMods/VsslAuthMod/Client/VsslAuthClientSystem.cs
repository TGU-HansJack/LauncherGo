using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VsslAuth.Network;

namespace VsslAuth.Client;

public sealed class VsslAuthClientSystem : ModSystem
{
    private readonly HashSet<string> _openedChallenges = new(StringComparer.Ordinal);
    private ICoreClientAPI? _clientApi;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Client;
    }

    public override double ExecuteOrder()
    {
        return 0.12;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        _clientApi = api;
        api.Network.GetChannel(VsslAuthModSystem.ChannelName)
            .SetMessageHandler<AuthChallengePacket>(OnAuthChallenge)
            .SetMessageHandler<AuthStatePacket>(OnAuthState);

        api.Event.LeaveWorld += OnLeaveWorld;
    }

    private void OnLeaveWorld()
    {
        _openedChallenges.Clear();
    }

    private void OnAuthChallenge(AuthChallengePacket packet)
    {
        if (_clientApi is null || packet is null)
            return;

        if (!string.IsNullOrWhiteSpace(packet.Message))
            _clientApi.ShowChatMessage(packet.Message);

        if (string.IsNullOrWhiteSpace(packet.AuthUrl) ||
            string.IsNullOrWhiteSpace(packet.ChallengeId) ||
            !_openedChallenges.Add(packet.ChallengeId))
        {
            return;
        }

        try
        {
            OpenUrlInBrowser(packet.AuthUrl);
        }
        catch
        {
            _clientApi.ShowChatMessage("无法自动打开认证页面，请复制链接到浏览器：" + packet.AuthUrl);
        }
    }

    private void OnAuthState(AuthStatePacket packet)
    {
        if (_clientApi is null || packet is null || string.IsNullOrWhiteSpace(packet.Message))
            return;

        _clientApi.ShowChatMessage(packet.Message);
    }

    private static void OpenUrlInBrowser(string rawUrl)
    {
        var url = NormalizeAuthUrl(rawUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Invalid auth url");
        }

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", uri.ToString());
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Process.Start("xdg-open", uri.ToString());
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true
        });
    }

    private static string NormalizeAuthUrl(string rawUrl)
    {
        var url = rawUrl?.Trim() ?? string.Empty;
        return url.Replace("^&", "&", StringComparison.Ordinal);
    }
}
