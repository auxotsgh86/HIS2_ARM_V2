using HIS2.ARM.Client.Middlewares;
using Microsoft.AspNetCore.Builder;

namespace HIS2.ARM.Client.Extensions
{
    public static class ArmMiddlewareExtensions
    {
        /// <summary>
        /// 啟用 HIS2 ARM 授權防護網。
        /// 必須放置於 UseRouting() 之後，MapControllers() 之前。
        /// </summary>
        public static IApplicationBuilder UseArmAuthorization(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<ArmAuthorizationMiddleware>();
        }
    }
}