using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
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
        if (_clientApi is null || packet is null)
            return;

        if (!string.IsNullOrWhiteSpace(packet.Message))
            _clientApi.ShowChatMessage(packet.Message);

        if (!packet.OpenCharacterSelection)
            return;

        _clientApi.Event.EnqueueMainThreadTask(OpenCharacterSelection, "serverauth-open-character-selection");
    }

    private void OpenCharacterSelection()
    {
        if (_clientApi is null)
            return;

        var modSystem = _clientApi.ModLoader.GetModSystem<CharacterSystem>(true);
        if (modSystem is null)
        {
            _clientApi.ShowChatMessage("注册已完成，但无法打开职业选择界面，请输入 /charsel 再试一次。");
            return;
        }

        if (_clientApi.Gui.LoadedGuis.Any(dialog => dialog is GuiDialogCreateCharacter && dialog.IsOpened()))
            return;

        var dialog = new GuiDialogCreateCharacter(_clientApi, modSystem);
        dialog.PrepAndOpen();
        dialog.OnClosed += () => _clientApi.PauseGame(false);
        _clientApi.Event.EnqueueMainThreadTask(() => _clientApi.PauseGame(true), "serverauth-pause-character-selection");
        _clientApi.Event.PushEvent("begincharacterselection", null);
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
