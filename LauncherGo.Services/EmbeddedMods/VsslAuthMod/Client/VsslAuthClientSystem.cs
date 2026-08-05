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

    public override void Dispose()
    {
        if (_clientApi is not null)
            _clientApi.Event.LeaveWorld -= OnLeaveWorld;
        _clientApi = null;
        base.Dispose();
    }

    private void OnLeaveWorld()
    {
        _openedChallenges.Clear();
    }

    private void OnAuthChallenge(AuthChallengePacket packet)
    {
        if (_clientApi is null || packet is null)
            return;

        var url = NormalizeAuthUrl(packet.AuthUrl);
        if (string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(packet.ChallengeId) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        if (!_openedChallenges.Add(packet.ChallengeId))
            return;

        _clientApi.Event.EnqueueMainThreadTask(
            () => HandleAuthChallenge(url),
            "serverauth-handle-auth-challenge");
    }

    private void HandleAuthChallenge(string url)
    {
        if (_clientApi is null)
            return;

        bool copied = CopyAuthUrl(url);
        bool opened = OpenAuthUrl(url);

        if (copied && opened)
        {
            _clientApi.ShowChatMessage("认证链接已自动复制到剪贴板，浏览器已尝试打开。若浏览器没有打开，请粘贴链接到浏览器。");
        }
        else if (copied)
        {
            _clientApi.ShowChatMessage("认证链接已自动复制到剪贴板，请粘贴链接到浏览器完成认证。");
        }
        else if (opened)
        {
            _clientApi.ShowChatMessage("浏览器已尝试打开认证页面，但自动复制失败，请手动复制链接：" + url);
        }
        else
        {
            _clientApi.ShowChatMessage("无法自动打开浏览器或复制链接，请手动复制链接：" + url);
        }
    }

    private bool CopyAuthUrl(string url)
    {
        if (_clientApi is null || string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            _clientApi.Forms.SetClipboardText(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool OpenAuthUrl(string url)
    {
        try
        {
            OpenUrlInBrowser(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnAuthState(AuthStatePacket packet)
    {
        if (_clientApi is null || packet is null)
            return;

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
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(uri.ToString());
            Process.Start(startInfo);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(uri.ToString());
            Process.Start(startInfo);
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
