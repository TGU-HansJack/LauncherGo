using System.Globalization;
using Avalonia.Data.Converters;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Converters;

public sealed class GatewayRoutingStateDisplayConverter : IValueConverter
{
    public bool IsChinese { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TcpGatewayBackendRoutingState state)
        {
            return string.Empty;
        }

        return IsChinese
            ? state switch
            {
                TcpGatewayBackendRoutingState.Online => "在线",
                TcpGatewayBackendRoutingState.Draining => "排空中",
                TcpGatewayBackendRoutingState.Disabled => "已禁用",
                TcpGatewayBackendRoutingState.Offline => "离线",
                _ => "未知"
            }
            : state.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
}
