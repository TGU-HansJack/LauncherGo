using LauncherGo.Domains.Enums;

namespace LauncherGo.Domains.Models;

public sealed class FrpIntegrationSettings
{
    public const string DefaultFrpCommand = "frpc.exe -c frpc.toml";

    public const string DefaultThirdPartyFrpcConfigCommand = "frpc.exe";

    public const string DefaultThirdPartyFrpcCommand = "frpc.exe -f <访问密钥>:<隧道ID>";

    public string FrpCommand { get; set; } = DefaultFrpCommand;

    public ThirdPartyFrpcLaunchMode ThirdPartyFrpcLaunchMode { get; set; } = ThirdPartyFrpcLaunchMode.ConfigFile;

    public string ThirdPartyFrpcCommand { get; set; } = DefaultThirdPartyFrpcConfigCommand;
}
