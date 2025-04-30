using System.Text;
using Microsoft.EntityFrameworkCore;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.TranslationServices;

public interface IUserOrderService
{
    /// <summary>
    /// 创建用户订单
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="type">订单类型</param>
    /// <param name="assoId">关联 ID</param>
    Task<UserOrder> CreateUserOrderAsync(long userId, UserOrderType type, long assoId);
}

public class UserOrderService(ApplicationDbContext context) : IUserOrderService
{
    protected readonly ApplicationDbContext _context = context;
    public async Task<UserOrder> CreateUserOrderAsync(long userId, UserOrderType type, long assoId)
    {
        var userOrder = new UserOrder
        {
            UserId = userId,
            Type = type,
            AssoId = assoId,
        };
        await _context.UserOrders.AddAsync(userOrder);
        await _context.SaveChangesAsync();

        return userOrder;
    }
}
