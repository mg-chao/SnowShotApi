using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.UserServices;

public interface IIpUserService
{
    Task<User?> GetUserAsync(HttpContext httpContext);
}

public class IpUserService(ApplicationDbContext context, IUserService userService) : IIpUserService
{
    private readonly ApplicationDbContext _context = context;
    private readonly IUserService _userService = userService;

    /**
     * 获取真实的客户端 IP 地址
     * @param httpContext 请求上下文
     * @returns IP 地址字符串
     */
    private static string? GetRealIpAddress(HttpContext httpContext)
    {
        // 优先检查常见的代理头部
        // X-Forwarded-For: 可能包含多个 IP，格式为 "client, proxy1, proxy2"
        var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // 取第一个 IP（真实客户端 IP）
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ips.Length > 0)
            {
                var ip = ips[0].Trim();
                if (!string.IsNullOrEmpty(ip))
                {
                    return ip;
                }
            }
        }

        // 检查 X-Real-IP（Nginx 常用）
        var realIp = httpContext.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
        {
            return realIp.Trim();
        }

        // 检查 CF-Connecting-IP（Cloudflare）
        var cfConnectingIp = httpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(cfConnectingIp))
        {
            return cfConnectingIp.Trim();
        }

        // 检查 X-Original-For
        var originalFor = httpContext.Request.Headers["X-Original-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(originalFor))
        {
            return originalFor.Trim();
        }

        // 如果都没有，回退到使用 RemoteIpAddress
        return httpContext.Connection.RemoteIpAddress?.ToString();
    }

    /**
     * 获取 IP 对应的用户，如果用户不存在，则创建一个
     * @param httpContext 请求上下文
     * @returns 用户
     */
    private async Task<IpUser?> GetIpUserAsync(HttpContext httpContext)
    {
        var ipAddress = GetRealIpAddress(httpContext);
        if (string.IsNullOrEmpty(ipAddress))
        {
            return null;
        }

        var ipUser = await _context.IpUsers.FirstOrDefaultAsync(u => u.IpAddress == ipAddress);
        if (ipUser == null)
        {
            ipUser = new IpUser
            {
                IpAddress = ipAddress,
            };
            await _context.IpUsers.AddAsync(ipUser);
            await _context.SaveChangesAsync();

            await _userService.CreateUserAsync(UserType.Ip, ipUser.Id);
        }

        return ipUser;
    }

    /**
     * 获取 IP 对应的用户
     * @param httpContext 请求上下文
     * @returns 用户
     */
    public async Task<User?> GetUserAsync(HttpContext httpContext)
    {
        var ipUser = await GetIpUserAsync(httpContext);
        if (ipUser == null)
        {
            return null;
        }

        return await _userService.GetUserAsync(UserType.Ip, ipUser.Id);
    }
}