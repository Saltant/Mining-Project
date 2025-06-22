namespace PoW.Data
{
    using System;
    using System.Collections.Concurrent;
    using System.Security.Cryptography;
    using System.Text;

    public class Block
    {
        public long BlockNumber { get; set; }
        public string? Timestamp { get; set; }
        public string? Data { get; set; }
        public string? PreviousHash { get; set; }
        public long Nonce { get; set; }
        public string? Hash { get; set; }
        public int Difficulty { get; set; }
        private long hashCount = 0; // Счётчик вызовов CalculateHash

        public Block() { }

        public Block(long blockNumber, string timestamp, string data, string previousHash, int difficulty)
        {
            BlockNumber = blockNumber;
            Timestamp = timestamp;
            Data = data;
            PreviousHash = previousHash;
            Difficulty = difficulty;
            Nonce = 0;
            Hash = CalculateHash();
        }

        public string CalculateHash()
        {
            Interlocked.Increment(ref hashCount); // Увеличиваем счётчик хешей
            string rawData = BlockNumber + Timestamp + Data + PreviousHash + Nonce;
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
            return Convert.ToHexStringLower(bytes);
        }

        public bool MineBlock()
        {
            string target = new('0', Difficulty);
            while (Hash?[..Difficulty] != target)
            {
                Nonce++;
                Hash = CalculateHash();
            }
            return true;
        }

        public bool MineShare(long startNonce, long endNonce, int minDifficulty, out long foundNonce, out long hashesThisRun)
        {
            string target = new('0', minDifficulty);
            long resultNonce = -1;
            bool found = false;
            long initialHashCount = Interlocked.Read(ref hashCount);

            try
            {
                // Используем Parallel.For для параллельного перебора nonce
                Parallel.For(startNonce, endNonce + 1, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (nonce, state) =>
                {
                    if (found || state.IsStopped) return;

                    // Вычисляем хеш для текущего nonce
                    Nonce = nonce;
                    string hash = CalculateHash();

                    // Проверяем, удовлетворяет ли хеш условию
                    if (hash[..minDifficulty] == target)
                    {
                        // Безопасно обновляем результат
                        Interlocked.CompareExchange(ref resultNonce, nonce, -1);
                        found = true;
                        state.Stop(); // Останавливаем все потоки
                    }
                });

                hashesThisRun = Interlocked.Read(ref hashCount) - initialHashCount;

                if (found)
                {
                    Nonce = resultNonce;
                    Hash = CalculateHash();
                    hashesThisRun++; // Учитываем финальный CalculateHash
                    foundNonce = resultNonce;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в MineShare: {ex.Message}");
                throw;
            }

            foundNonce = -1;
            return false;
        }

        public bool Verify()
        {
            string target = new('0', Difficulty);
            return Hash?[..Difficulty] == target && Hash == CalculateHash();
        }

        public long GetCurrentHashCount()
        {
            return Interlocked.Read(ref hashCount); // Возвращаем текущее значение
        }

        public void ResetHashCount()
        {
            Interlocked.Exchange(ref hashCount, 0); // Сбрасываем счётчик
        }
    }
}
