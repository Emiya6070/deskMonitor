using System.Net;

namespace DeskMonitor.Core;
public enum ProxyMode { System, Direct, Custom }
public sealed record NetworkProxy
{
    public ProxyMode Mode { get; init; } = ProxyMode.System;
    public string Address { get; init; } = "";
    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new InvalidDataException("代理模式无效。");
        if (Mode == ProxyMode.Custom && (!Uri.TryCreate(Address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "socks5") || string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535
            || uri.UserInfo.Length > 0 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            throw new InvalidDataException("请输入 HTTP 或 SOCKS5 代理地址，例如 http://127.0.0.1:7890；不支持地址内的账号密码、路径或参数。");
    }
    public IWebProxy? CreateProxy()
    {
        Validate();
        return Mode switch { ProxyMode.System => HttpClient.DefaultProxy, ProxyMode.Direct => null, ProxyMode.Custom => new WebProxy(Address), _ => throw new InvalidDataException("代理模式无效。") };
    }
}
