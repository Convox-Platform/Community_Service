using System.Security.Claims;
using Grpc.Core;

namespace Community_Service.Auth
{
    // Доступ к аутентифицированному пользователю внутри gRPC-хендлеров.
    // Токен уже проверен middleware'ом; здесь только извлекаем userId из claim'ов.
    public static class CallerContext
    {
        // userId вызывающего из JWT (claim NameIdentifier, как выпускает Auth_Service).
        // Бросает Unauthenticated, если токена нет или в нём нет корректного userId.
        public static ulong GetUserId(this ServerCallContext context)
        {
            var user = context.GetHttpContext().User;
            var raw = user.FindFirstValue(ClaimTypes.NameIdentifier);

            if (ulong.TryParse(raw, out var userId))
            {
                return userId;
            }

            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing user id in token"));
        }
    }
}
