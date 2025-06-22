using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PoW.Data;
using System.Timers;

namespace PoW.MiningClient
{
    public class MiningClient
    {
        private readonly HubConnection poolConnection;
        private readonly string clientId;
        private readonly string privateKey;
        private readonly ILogger<MiningClient> logger;
        private string jwtToken;
        private bool isMining = true;
        long currentBlockNumber;
        private long shareCount = 0; // Счётчик отправленных shares
        private long lastShareCount = 0; // Для расчёта shares/min
        private long totalHashCount = 0; // Общее количество хешей
        private long lastHashCount = 0; // Для расчёта MH/s
        private readonly DateTime startTime; // Время начала работы клиента
        private readonly System.Timers.Timer monitoringTimer; // Таймер для мониторинга
        private readonly System.Timers.Timer hashUpdateTimer; // Таймер для обновления хешей
        private Block currentBlock; // Текущий блок для статистики
        private long lastBlockHashCount = 0; // Последнее значение hashCount блока

        public MiningClient(string poolUrl, string clientId, string privateKey, ILogger<MiningClient> logger)
        {
            this.clientId = clientId;
            this.privateKey = privateKey;
            this.logger = logger;
            startTime = DateTime.UtcNow;

            // Инициализация таймера для мониторинга
            monitoringTimer = new System.Timers.Timer(10000); // 10 секунд
            monitoringTimer.Elapsed += OnMonitoringTimerElapsed;
            monitoringTimer.AutoReset = true;

            // Инициализация таймера для обновления хешей
            hashUpdateTimer = new System.Timers.Timer(1000); // 1 секунда
            hashUpdateTimer.Elapsed += OnHashUpdateTimerElapsed;
            hashUpdateTimer.AutoReset = true;

            try
            {
                poolConnection = new HubConnectionBuilder().WithUrl(poolUrl).WithAutomaticReconnect().Build();
                poolConnection.On<string>("ReceiveJwtToken", token => jwtToken = token);
                poolConnection.On<string>("ReceiveSubTask", ReceiveSubTask);
                poolConnection.Closed += PoolConnection_Closed;
            }
            catch (Exception ex)
            {
                logger.LogError("{ex}", ex);
                Environment.Exit(1);
            }

            try
            {
                _ = StartAsync();
            }
            catch (Exception ex)
            {
                logger.LogError("{ex}", ex);
                Environment.Exit(1);
            }
        }

        private async Task StartAsync()
        {
            try
            {
                await poolConnection.StartAsync();
                logger.LogInformation("Клиент {clientId} подключен к пулу.", clientId);

                string timestamp = DateTime.UtcNow.ToString("o");
                string message = $"{clientId}:{timestamp}";
                string signature = EcdsaAuth.SignMessage(privateKey, message);
                await poolConnection.InvokeAsync("RegisterClient", clientId, signature, timestamp);
                monitoringTimer.Start();
                hashUpdateTimer.Start();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка подключения клиента {clientId} к пулу.", clientId);
                await Task.Delay(5000);
                await StartAsync();
            }
        }

        private void OnMonitoringTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                // Вычисляем скорость shares и хешей
                long currentShareCount = Interlocked.Read(ref shareCount);
                long currentHashCount = Interlocked.Read(ref totalHashCount);
                double sharesSinceLast = currentShareCount - lastShareCount;
                double hashesSinceLast = currentHashCount - lastHashCount;
                double sharesPerMinute = (sharesSinceLast / 10.0) * 60.0; // shares/min
                double megaHashesPerSecond = (hashesSinceLast / 10.0) / 1000000.0; // MH/s
                lastShareCount = currentShareCount;
                lastHashCount = currentHashCount;

                // Время работы и средний хешрейт
                TimeSpan uptime = DateTime.UtcNow - startTime;
                double avgMegaHashesPerSecond = (totalHashCount / uptime.TotalSeconds) / 1000000.0;

                // Формируем статистику
                string blockInfo = currentBlock != null
                    ? $"блок #{currentBlock.BlockNumber}, сложность {currentBlock.Difficulty}"
                    : "нет активной задачи";
                string stats = $"Хешрейт: {megaHashesPerSecond:F2} MH/s, Средний хешрейт: {avgMegaHashesPerSecond:F2} MH/s, " +
                               $"Shares: {sharesPerMinute:F2} shares/min, Всего shares: {currentShareCount}, {blockInfo}, Время работы: {uptime:hh\\:mm\\:ss}";


                //Console.WriteLine($"[Мониторинг] Клиент {clientId}: {stats}");
                logger.LogInformation("{stats}", stats);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в мониторинге клиента {clientId}.", clientId);
            }
        }


        private void OnHashUpdateTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (currentBlock != null)
                {
                    long currentBlockHashes = currentBlock.GetCurrentHashCount();
                    long hashesThisUpdate = currentBlockHashes - lastBlockHashCount;
                    if (hashesThisUpdate > 0)
                    {
                        Interlocked.Add(ref totalHashCount, hashesThisUpdate);
                        lastBlockHashCount = currentBlockHashes;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в обновлении хешей клиента {clientId}.", clientId);
            }
        }

        async Task PoolConnection_Closed(Exception error)
        {
            logger.LogWarning("Соединение с пулом разорвано: {error?.Message}", error?.Message);
            monitoringTimer.Stop();
            hashUpdateTimer.Stop();
            await Task.Delay(5000);
            await StartAsync();
        }

        private async Task ReceiveSubTask(string subTaskJson)
        {
            try
            {
                var subTask = JsonConvert.DeserializeObject<dynamic>(subTaskJson);
                long startNonce = subTask.StartNonce;
                long endNonce = subTask.EndNonce;
                Block block = JsonConvert.DeserializeObject<Block>(subTask.Block.ToString());
                // Сбрасываем hashCount при получении новой задачи
                if (currentBlock == null || currentBlock.BlockNumber != block.BlockNumber)
                {
                    if (currentBlock != null)
                    {
                        long hashesThisBlock = currentBlock.GetCurrentHashCount();
                        Interlocked.Add(ref totalHashCount, hashesThisBlock - lastBlockHashCount);
                        lastBlockHashCount = 0;
                    }
                    block.ResetHashCount();
                }
                currentBlock = block;

                if (currentBlockNumber != block.BlockNumber)
                {
                    currentBlockNumber = block.BlockNumber;
                    logger.LogInformation("Клиент {clientId} получил подзадачу: nonce {startNonce}-{endNonce}, блок #{blockNumber}, сложность {difficulty}.",
                        clientId, startNonce, endNonce, block.BlockNumber, block.Difficulty);
                }

                int minDifficulty = Math.Max(1, block.Difficulty - 1);
                while (isMining && poolConnection.State == HubConnectionState.Connected)
                {
                    // Запускаем MineShare с подсчётом хешей
                    bool isFoundNonce = block.MineShare(startNonce, endNonce, minDifficulty, out long foundNonce, out long hashesThisRun);

                    // Обновляем totalHashCount после MineShare
                    Interlocked.Add(ref totalHashCount, hashesThisRun);
                    lastBlockHashCount = block.GetCurrentHashCount();

                    if (isFoundNonce)
                    {
                        block.Nonce = foundNonce;
                        block.Hash = block.CalculateHash();
                        Interlocked.Add(ref totalHashCount, 1); // Учитываем финальный CalculateHash
                        lastBlockHashCount++;

                        // Проверяем минимальную сложность share
                        int minShareDifficulty = Math.Max(1, block.Difficulty - 1);
                        if (block.Hash.StartsWith(new string('0', minShareDifficulty)))
                        {
                            string shareJson = JsonConvert.SerializeObject(block);
                            string signature = EcdsaAuth.SignMessage(privateKey, shareJson);
                            await poolConnection.InvokeAsync("SubmitShare", clientId, jwtToken, shareJson, signature);
                            logger.LogInformation("Отправлен share: nonce={nonce}, hash={hash}.", foundNonce, block.Hash);
                            Interlocked.Increment(ref shareCount);
                            startNonce = foundNonce + 1;
                        }
                    }
                    else
                    {
                        //logger.LogInformation("Диапазон nonce {startNonce}-{endNonce} исчерпан. Запрашиваем новый диапазон.", startNonce, endNonce);
                        await poolConnection.InvokeAsync("RequestNewNonceRange", clientId, jwtToken);
                        return; // Прерываем текущую задачу, ждём новую подзадачу
                    }
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка обработки подзадачи для клиента {clientId}.", clientId);
            }
        }

        public async Task StopAsync()
        {
            isMining = false;
            monitoringTimer.Stop();
            hashUpdateTimer.Stop();
            if (currentBlock != null)
            {
                long hashesThisBlock = currentBlock.GetCurrentHashCount();
                Interlocked.Add(ref totalHashCount, hashesThisBlock - lastBlockHashCount);
            }
            await poolConnection.StopAsync();
            logger.LogInformation("Клиент {clientId} остановлен.", clientId);
        }
    }
}
