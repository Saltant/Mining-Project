using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using PoW.Data;
using System.Security.Cryptography.Xml;
using System.Globalization;

namespace PoW.MiningPool
{
    public class MiningPool
    {
        private readonly HubConnection serverConnection;
        private readonly string poolId;
        private const int RewardPerBlock = 100;
        private Block currentBlock;
        private readonly ConcurrentDictionary<string, int> clientShares = [];
        private readonly ConcurrentDictionary<string, DateTime> lastShareTime = [];
        private readonly ConcurrentDictionary<string, int> clientWarnings = [];
        private readonly ConcurrentDictionary<string, long> clientNonceRanges = [];

        private readonly IHubContext<PoolHub> hubContext;
        private readonly ILogger<MiningPool> logger;
        private readonly string jwtSecret = "your-very-long-secret-key-for-jwt"; // Замените на безопасный ключ
        private long currentNonceOffset = 0;
        private const long NonceRangeSize = 100_000_000;

        public MiningPool(string serverUrl, string poolId, string jwtSecret, IHubContext<PoolHub> hubContext, ILogger<MiningPool> logger)
        {
            this.poolId = poolId;
            this.jwtSecret = jwtSecret;
            this.hubContext = hubContext;
            this.logger = logger;
            serverConnection = new HubConnectionBuilder()
                .WithUrl(serverUrl)
                .Build();
            serverConnection.On<string>("ReceiveTask", ReceiveTask);
            serverConnection.StartAsync().Wait();
            serverConnection.InvokeAsync("RegisterPool", poolId, jwtSecret).Wait();
        }

        private string GenerateJwtToken(string id)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(jwtSecret);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([new Claim("poolId", id)]),
                Expires = DateTime.UtcNow.AddDays(1),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        public bool ValidateJwtToken(string jwtToken, string clientId)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.ASCII.GetBytes(jwtSecret);
                tokenHandler.ValidateToken(jwtToken, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true
                }, out SecurityToken validatedToken);

                var jwt = (JwtSecurityToken)validatedToken;
                return jwt.Claims.FirstOrDefault(c => c.Type == "poolId")?.Value == clientId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка валидации JWT-токена для клиента {clientId}.", clientId);
                return false;
            }
        }

        private async Task ReceiveTask(string taskJson)
        {
            try
            {
                currentBlock = JsonConvert.DeserializeObject<Block>(taskJson);
                clientNonceRanges.Clear();
                currentNonceOffset = 0;
                logger.LogInformation("Получена задача для блока #{currentBlock.BlockNumber}.", currentBlock.BlockNumber);
                // Уведомляем всех клиентов о новой задаче
                foreach (var client in clientShares.Keys)
                {
                    await AssignNewNonceRange(client, null);
                }
                
                //await SubmitTaskToMiners();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка получения задачи от сервера.");
            }
        }

        public async Task RegisterClient(string clientId, string connectionId, string signature, string timestamp)
        {
            try
            {
                // Проверяем подпись
                string message = $"{clientId}:{timestamp}";
                if (!EcdsaAuth.VerifySignature(clientId, message, signature))
                {
                    logger.LogWarning("Неверная подпись для клиента {clientId}.", clientId);
                    throw new HubException("Неверная подпись.");
                }

                // Проверяем свежесть timestamp
                if (Math.Abs((DateTime.UtcNow - DateTime.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)).TotalSeconds) > 60)
                {
                    logger.LogWarning("Устаревший timestamp для клиента {clientId}.", clientId);
                    throw new HubException("Устаревший timestamp.");
                }

                // Выдаём JWT-токен клиенту
                string jwtToken = GenerateJwtToken(clientId);
                await hubContext.Clients.Client(connectionId).SendAsync("ReceiveJwtToken", jwtToken);
                await AssignNewNonceRange(clientId, connectionId);
                clientShares[clientId] = 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка отправки подзадачи клиенту {clientId}.", clientId);
            }
        }

        public async Task AssignNewNonceRange(string clientId, string connectionId)
        {
            try
            {
                if (currentBlock == null)
                {
                    logger.LogWarning("Нет активной задачи для клиента {clientId}.", clientId);
                    return;
                }

                long startNonce = currentNonceOffset;
                long endNonce = startNonce + NonceRangeSize;
                currentNonceOffset = endNonce;
                clientNonceRanges[clientId] = endNonce;

                string subTask = JsonConvert.SerializeObject(new { StartNonce = startNonce, EndNonce = endNonce, Block = currentBlock });
                if (connectionId != null)
                {
                    await hubContext.Clients.Client(connectionId).SendAsync("ReceiveSubTask", subTask);
                }
                else
                {
                    await hubContext.Clients.All.SendAsync("ReceiveSubTask", subTask);
                }
                //logger.LogInformation("Клиенту {clientId} назначен новый диапазон nonce: {startNonce}-{endNonce}.", clientId, startNonce, endNonce);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка назначения нового диапазона nonce для клиента {clientId}.", clientId);
            }
        }

        public async Task<bool> SubmitShare(string clientId, string shareJson, string signature)
        {
            try
            {
                if (clientWarnings.TryGetValue(clientId, out int warnings) && warnings > 10)
                {
                    logger.LogWarning("Клиент {clientId} заблокирован за подозрительную активность.", clientId);
                    return false;
                }

                // Проверка частоты
                //if (lastShareTime.TryGetValue(clientId, out var lastTime) && (DateTime.UtcNow - lastTime).TotalSeconds < 1)
                //{
                //    logger.LogWarning("Слишком частая отправка shares от клиента {clientId}.", clientId);
                //    return;
                //}
                //lastShareTime[clientId] = DateTime.UtcNow;

                // Проверяем подпись share
                if (!EcdsaAuth.VerifySignature(clientId, shareJson, signature))
                {
                    logger.LogWarning("Неверная подпись share от клиента {clientId}.", clientId);
                    return false;
                }

                dynamic share = JsonConvert.DeserializeObject<dynamic>(shareJson);
                long nonce = share.Nonce;
                string hash = share.Hash;
                long blockNumber = share.BlockNumber;

                // Проверяем что share принадлежит текущему блоку
                if (currentBlock.BlockNumber != blockNumber)
                {
                    logger.LogWarning("Share не принадлежит текущему блоку, игнорируем.");
                    return false;
                }

                // Проверяем минимальную сложность share
                int minShareDifficulty = Math.Max(1, currentBlock.Difficulty - 1);
                if (!hash.StartsWith(new string('0', minShareDifficulty)))
                {
                    logger.LogWarning("Невалидный share от клиента {clientId}: недостаточная сложность.", clientId);
                    clientWarnings.AddOrUpdate(clientId, 1, (key, oldValue) => oldValue + 1);
                    return false;
                }

                // Проверяем хеш
                Block tempBlock = JsonConvert.DeserializeObject<Block>(shareJson);
                if (tempBlock.CalculateHash() != hash)
                {
                    logger.LogWarning("Невалидный share от клиента {clientId}: неверный хеш.", clientId);
                    return false;
                }
                
                // Учитываем share
                clientShares.AddOrUpdate(clientId, 1, (key, oldValue) => oldValue + 1);

                // Проверяем, является ли это полным решением
                lock (this)
                {
                    //logger.LogInformation("Валидный share от клиента {clientId}, nonce: {nonce}, hash: {hash}.", clientId, nonce, hash);
                    if (currentBlock.BlockNumber == tempBlock.BlockNumber && hash.StartsWith(new string('0', currentBlock.Difficulty)))
                    {
                        Block solution = tempBlock;
                        if (solution.Verify())
                        {
                            DistributeRewards(RewardPerBlock);
                            clientShares.Clear(); // Сбрасываем shares для нового блока
                            serverConnection.InvokeAsync("SubmitSolution", poolId, jwtSecret, shareJson).GetAwaiter().GetResult();
                            logger.LogInformation("Полное решение отправлено серверу от клиента {clientId}.", clientId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка обработки share от клиента {clientId}.", clientId);
            }

            return await Task.FromResult(true);
        }

        private void DistributeRewards(int totalReward)
        {
            try
            {
                int totalShares = 0;
                foreach (var shares in clientShares.Values)
                    totalShares += shares;

                if (totalShares == 0) return;

                foreach (var client in clientShares)
                {
                    double share = (double)client.Value / totalShares;
                    int reward = (int)(share * totalReward);
                    logger.LogInformation("Клиент {client.Key} получает {reward} токенов за {client.Value} shares.", client.Key, reward, client.Value);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка распределения наград.");
            }
        }
    }

}
