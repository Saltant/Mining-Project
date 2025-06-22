using Microsoft.Extensions.Logging;
using PoW.Data;

namespace PoW.MiningClient
{
    internal class Program
    {
        static async Task Main(string[] _)
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<MiningClient>();

            // Генерация ключей клиента
            var (publicKey, privateKey) = EcdsaAuth.GenerateKeyPair();
            var client = new MiningClient("http://192.168.0.101:12000/poolHub", publicKey, privateKey, logger);
            Console.WriteLine($"Клиент запущен. ID: {publicKey}");
            Console.ReadKey();
            await client.StopAsync();
        }
    }
}
