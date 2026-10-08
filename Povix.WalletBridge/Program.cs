using System;

namespace Povix.WalletBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            { Console.Error.WriteLine("O auxiliar abre arquivos protegidos pelo Windows e deve ser executado no seu computador Windows."); return 1; }
            if (args.Length != 2 || args[0] != "--origin")
            { Console.Error.WriteLine("Uso: Povix.WalletBridge.exe --origin https://endereco-do-dex"); return 1; }
            try
            {
                using (var server = new LoopbackWalletServer(args[1]))
                {
                    server.Start();
                    Console.WriteLine("Abertura local de wallet.dat / wallets.dat ativa para " + server.AllowedOrigin);
                    Console.WriteLine("Volte ao DEX, escolha o arquivo e selecione uma carteira. Pressione Enter aqui para encerrar.");
                    Console.ReadLine();
                }
                return 0;
            }
            catch (Exception error) when (error is ArgumentException || error is System.Net.Sockets.SocketException)
            { Console.Error.WriteLine("Não foi possível iniciar. Confira o endereço do DEX e se a porta local 4781 está livre."); return 1; }
        }
    }
}
