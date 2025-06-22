using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using PoW.Data;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Data.SQLite;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace PoW.MiningServer
{
    public class MiningServer
    {
        readonly SQLiteConnection dbConnection;
        private long blockNumber = 1;
        private string previousHash = "0";
        private int currentDifficulty = 4;
        private DateTime lastBlockTime;
        private const int RewardPerBlock = 100;
        private readonly IHubContext<MiningHub> hubContext;
        private readonly ILogger<MiningServer> logger;
        private readonly string jwtSecret = "your-very-long-secret-key-for-jwt"; // Замените на безопасный ключ
        readonly ConcurrentDictionary<string, string> pools = [];

        public MiningServer(string dbPath, string jwtSecret, IHubContext<MiningHub> hubContext, ILogger<MiningServer> logger)
        {
            this.hubContext = hubContext;
            this.logger = logger;
            this.jwtSecret = jwtSecret;
            dbConnection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            dbConnection.Open();
            CreateTables();

            Block lastBlock = GetLastBlock();
            if(lastBlock != null)
            {
                lastBlockTime = DateTime.Parse(lastBlock.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                blockNumber = lastBlock.BlockNumber;
                previousHash = lastBlock.Hash;
                currentDifficulty = lastBlock.Difficulty;
            }
            else
            {
                blockNumber = 1;
                lastBlockTime = DateTime.UtcNow;
            }
        }

        Block GetLastBlock()
        {
            string sql = "Select * From Blocks Order By rowid Desc Limit 1";
            return dbConnection.QueryFirstOrDefault<Block>(sql);
        }

        private void CreateTables()
        {
            try
            {
                string sql = @"
                CREATE TABLE IF NOT EXISTS Blocks (BlockNumber INT, Timestamp TEXT, Data TEXT, PreviousHash TEXT, Nonce INT, Hash TEXT, PoolId TEXT, Difficulty INT);
                CREATE TABLE IF NOT EXISTS Rewards (PoolId TEXT, Amount INT);
                CREATE TABLE IF NOT EXISTS Difficulty (Id INT PRIMARY KEY, Value INT);
                CREATE TABLE IF NOT EXISTS Pools (PoolId TEXT PRIMARY KEY, JwtToken TEXT)";
                new SQLiteCommand(sql, dbConnection).ExecuteNonQuery();

                // Инициализация сложности
                sql = "INSERT OR IGNORE INTO Difficulty (Id, Value) VALUES (1, @Value)";
                var command = new SQLiteCommand(sql, dbConnection);
                command.Parameters.AddWithValue("@Value", currentDifficulty);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка создания таблиц в базе данных.");
                throw;
            }
        }

        public bool ValidateJwtToken(string jwtToken, string poolId)
        {
            try
            {
                // Проверяем, зарегистрирован ли пул
                string sql = "SELECT JwtToken FROM Pools WHERE PoolId = @PoolId";
                var command = new SQLiteCommand(sql, dbConnection);
                command.Parameters.AddWithValue("@PoolId", poolId);
                var storedToken = command.ExecuteScalar()?.ToString();

                if (storedToken != jwtToken)
                {
                    logger.LogWarning("Пул {poolId} не зарегистрирован или токен неверный.", poolId);
                    return false;
                }

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
                return jwt.Claims.FirstOrDefault(c => c.Type == "poolId")?.Value == poolId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка валидации JWT-токена для пула {poolId}.", poolId);
                return false;
            }
        }

        public async Task RegisterPool(string poolId, string connectionId)
        {
            try
            {
                pools.AddOrUpdate(poolId, connectionId, (_, oldValue) => oldValue = connectionId);
                await SendPoolTask(poolId, connectionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка отправки задачи пулу {poolId}.", poolId);
                throw;
            }
        }

        async Task SendPoolTask(string poolId, string connectionId)
        {
            Block block = new(blockNumber, DateTime.UtcNow.ToString("o"), $"Блок #{blockNumber}", previousHash, currentDifficulty);
            string taskJson = JsonConvert.SerializeObject(block);
            await hubContext.Clients.Client(connectionId).SendAsync("ReceiveTask", taskJson);
            logger.LogInformation("Задача для блока #{blockNumber} отправлена пулу {poolId}.", blockNumber, poolId);
        }

        public Task SubmitSolution(string poolId, string solutionJson)
        {
            lock(this)
            {
                try
                {
                    Block solution = JsonConvert.DeserializeObject<Block>(solutionJson);
                    if (solution.Verify())
                    {
                        var blockTime = DateTime.UtcNow;
                        var timeTaken = (blockTime - lastBlockTime).TotalSeconds;
                        lastBlockTime = blockTime;

                        // Корректировка сложности
                        if (timeTaken < 10)
                            currentDifficulty++;
                        else if (timeTaken > 20 && currentDifficulty > 1)
                            currentDifficulty--;

                        blockNumber++;
                        previousHash = solution.Hash;

                        // Сохранение блока
                        string sql = "INSERT INTO Blocks (BlockNumber, Timestamp, Data, PreviousHash, Nonce, Hash, PoolId, Difficulty) VALUES (@BlockNumber, @Timestamp, @Data, @PreviousHash, @Nonce, @Hash, @PoolId, @Difficulty)";
                        var command = new SQLiteCommand(sql, dbConnection);
                        command.Parameters.AddWithValue("@BlockNumber", solution.BlockNumber);
                        command.Parameters.AddWithValue("@Timestamp", solution.Timestamp);
                        command.Parameters.AddWithValue("@Data", solution.Data);
                        command.Parameters.AddWithValue("@PreviousHash", solution.PreviousHash);
                        command.Parameters.AddWithValue("@Nonce", solution.Nonce);
                        command.Parameters.AddWithValue("@Hash", solution.Hash);
                        command.Parameters.AddWithValue("@PoolId", poolId);
                        command.Parameters.AddWithValue("@Difficulty", currentDifficulty);
                        command.ExecuteNonQuery();

                        // Обновление сложности
                        sql = "UPDATE Difficulty SET Value = @Value WHERE Id = 1";
                        command = new SQLiteCommand(sql, dbConnection);
                        command.Parameters.AddWithValue("@Value", currentDifficulty);
                        command.ExecuteNonQuery();

                        // Выдача награды
                        sql = "INSERT OR REPLACE INTO Rewards (PoolId, Amount) VALUES (@PoolId, COALESCE((SELECT Amount FROM Rewards WHERE PoolId = @PoolId), 0) + @Amount)";
                        command = new SQLiteCommand(sql, dbConnection);
                        command.Parameters.AddWithValue("@PoolId", poolId);
                        command.Parameters.AddWithValue("@Amount", RewardPerBlock);
                        command.ExecuteNonQuery();

                        logger.LogInformation("Блок #{solution.BlockNumber} добыт пулом {poolId}! Награда: {RewardPerBlock}, Новая сложность: {currentDifficulty}", solution.BlockNumber, poolId, RewardPerBlock, currentDifficulty);

                        // Выдать новые задачи для пулов
                        foreach (var pool in pools)
                        {
                            SendPoolTask(pool.Key, pool.Value).GetAwaiter().GetResult();
                        }
                    }
                    else if (solution.BlockNumber == blockNumber)
                    {
                        logger.LogWarning("Неверное решение от пула {poolId} для блока #{solution.BlockNumber}.", poolId, solution.BlockNumber);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ошибка обработки решения от пула {poolId}.", poolId);
                }

                return Task.CompletedTask;
            }
        }
    }

}
