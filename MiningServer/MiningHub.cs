using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace PoW.MiningServer
{
    public class MiningHub(MiningServer server, ILogger<MiningHub> logger) : Hub
    {
        private readonly MiningServer server = server;
        private readonly ILogger<MiningHub> logger = logger;

        public async Task RegisterPool(string poolId, string jwtToken)
        {
            try
            {
                if (server.ValidateJwtToken(jwtToken, poolId))
                {
                    logger.LogInformation("Пул {poolId} успешно зарегистрирован.", poolId);
                    await server.RegisterPool(poolId, Context.ConnectionId);
                }
                else
                {
                    logger.LogWarning("Неверный JWT-токен для пула {poolId}.", poolId);
                    throw new HubException("Неверный токен.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка регистрации пула {poolId}.", poolId);
                throw;
            }
        }

        public async Task SubmitSolution(string poolId, string jwtToken, string solutionJson)
        {
            try
            {
                if (server.ValidateJwtToken(jwtToken, poolId))
                {
                    logger.LogInformation("Получено решение от пула {poolId}.", poolId);
                    await server.SubmitSolution(poolId, solutionJson);
                }
                else
                {
                    logger.LogWarning("Неверный JWT-токен для пула {poolId}.", poolId);
                    throw new HubException("Неверный токен.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка обработки решения от пула {poolId}.", poolId);
                throw;
            }
        }
    }
}
