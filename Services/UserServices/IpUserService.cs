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
     * 获取 IP 对应的用户，如果用户不存在，则创建一个
     * @param ipAddress 用户 IP 地址
     * @returns 用户
     */
    private async Task<IpUser?> GetIpUserAsync(HttpContext httpContext)
    {
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "";
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