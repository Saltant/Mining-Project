using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace PoW.MiningPool
{
    public class PoolHub(MiningPool pool, ILogger<PoolHub> logger) : Hub
    {
        private readonly MiningPool pool = pool;
        private readonly ILogger<PoolHub> logger = logger;

        public async Task RegisterClient(string clientId, string signature, string timestamp)
        {
            try
            {
                logger.LogInformation("Клиент {clientId} успешно зарегистрирован в пуле.", clientId);
                await pool.RegisterClient(clientId, Context.ConnectionId, signature, timestamp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка регистрации клиента {clientId}.", clientId);
                throw;
            }
        }

        public async Task SubmitShare(string clientId, string jwtToken, string shareJson, string signature)
        {
            try
            {
                if (pool.ValidateJwtToken(jwtToken, clientId))
                {
                    //logger.LogInformation("Получен share от клиента {clientId}.", clientId);
                    if (!await pool.SubmitShare(clientId, shareJson, signature))
                    {
                        //Context.Abort();
                    }
                }
                else
                {
                    logger.LogWarning("Неверный JWT-токен для клиента {clientId}.", clientId);
                    throw new HubException("Неверный токен.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка обработки share от клиента {clientId}.", clientId);
                throw;
            }
        }

        public async Task RequestNewNonceRange(string clientId, string jwtToken)
        {
            try
            {
                if (pool.ValidateJwtToken(jwtToken, clientId))
                {
                    await pool.AssignNewNonceRange(clientId, Context.ConnectionId);
                }
                else
                {
                    logger.LogWarning("Неверный JWT-токен для клиента {clientId}.", clientId);
                    throw new HubException("Неверный токен.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка запроса нового диапазона nonce для клиента {clientId}.", clientId);
                throw;
            }
        }
    }
}
