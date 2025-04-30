using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.UserServices;

public interface IUserService
{
    Task<User> CreateUserAsync(UserType type, long assoId);
    Task<User?> GetUserAsync(UserType type, long assoId);
}

public class UserService(ApplicationDbContext context) : IUserService
{
    private readonly ApplicationDbContext _context = context;

    /**
     * 创建用户
     */
    public async Task<User> CreateUserAsync(UserType type, long assoId)
    {
        var user = new User
        {
            Type = type,
            AssoId = assoId,
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }

    /**
     * 获取用户
     */
    public async Task<User?> GetUserAsync(UserType type, long assoId)
    {
        return await _context.Users.FirstOrDefaultAsync(u => u.Type == type && u.AssoId == assoId);
    }
}