using System;
using System.IO;
using System.Net.Sockets;

namespace Povix.WalletBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            { WriteUsage(Console.Out); return 0; }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            { Console.Error.WriteLine("O auxiliar abre arquivos protegidos pelo Windows e deve ser executado no seu computador Windows."); return 1; }
            if (args.Length == 2 && args[0] == "--check-wallet")
            {
                Console.WriteLine("Usuário Windows do auxiliar: " + System.Security.Principal.WindowsIdentity.GetCurrent().Name);
                int result = CheckWallet(args[1], Console.Out, Console.Error);
                if (result != 0 && !Console.IsInputRedirected) { Console.WriteLine("Pressione Enter para fechar."); Console.ReadLine(); }
                return result;
            }
            return Run(args, Console.In, Console.Out, Console.Error, !Console.IsInputRedirected);
        }

        internal static int CheckWallet(string path, TextWriter output, TextWriter error, Func<byte[], WalletFileSummary> inspect = null)
        {
            byte[] contents = null;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) throw new IOException();
                if (file.Length > WalletFileConverter.MaximumFileBytes)
                    throw new WalletImportException("file_size", "O wallets.dat ultrapassa o limite de 6 MB do auxiliar local.");
                contents = File.ReadAllBytes(file.FullName);
                var open = inspect ?? WalletFileConverter.InspectFile;
                WalletFileSummary summary = open(contents);
                output.WriteLine("Arquivo aberto pelo Windows: " + summary.WalletCount + " carteira(s), " + summary.AddressCount + " endereço(s).");
                output.WriteLine("Diagnóstico concluído. O arquivo original foi apenas lido.");
                return 0;
            }
            catch (WalletImportException exception)
            {
                error.WriteLine(exception.Message);
                error.WriteLine("Código: " + exception.Code);
                if (exception.InnerException != null)
                    error.WriteLine("Detalhe técnico: " + exception.InnerException.GetType().Name + " 0x" + exception.InnerException.HResult.ToString("X8"));
                return 1;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException)
            {
                error.WriteLine("Não foi possível ler o arquivo informado. Confira o caminho e a permissão de leitura.");
                error.WriteLine("Código: file_access");
                return 1;
            }
            finally { if (contents != null) Array.Clear(contents, 0, contents.Length); }
        }

        internal static int Run(string[] args, TextReader input, TextWriter output, TextWriter error, bool pauseOnError)
        {
            string origin;
            if (args.Length == 0)
            {
                output.WriteLine("Povix.WalletBridge — abertura local de carteiras");
                output.WriteLine("Informe o endereço do DEX aberto no navegador (exemplo: https://localhost:44355).");
                output.Write("Endereço do DEX: ");
                origin = input.ReadLine()?.Trim();
            }
            else if (args.Length == 2 && args[0] == "--origin") origin = args[1];
            else
            {
                WriteUsage(error);
                return Fail("Argumentos inválidos. Execute sem argumentos para informar o endereço na janela.", input, output, error, pauseOnError);
            }
            if (string.IsNullOrWhiteSpace(origin))
                return Fail("Informe o endereço HTTPS do DEX (ou localhost para desenvolvimento).", input, output, error, pauseOnError);
            try
            {
                using (var server = new LoopbackWalletServer(origin))
                {
                    server.Start();
                    output.WriteLine("Abertura local de wallet.dat / wallets.dat ativa para " + server.AllowedOrigin);
                    output.WriteLine("Auxiliar ativo em http://127.0.0.1:" + LoopbackWalletServer.DefaultPort);
                    output.WriteLine("Mantenha esta janela aberta. Volte ao DEX, escolha o arquivo e selecione uma carteira.");
                    output.WriteLine("Pressione Enter aqui para encerrar.");
                    input.ReadLine();
                }
                return 0;
            }
            catch (ArgumentException)
            { return Fail("Endereço inválido. Use a origem HTTPS do DEX, como https://localhost:44355, ou HTTP em localhost.", input, output, error, pauseOnError); }
            catch (SocketException exception)
            {
                string message = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "A porta local 4781 já está em uso. Feche a outra janela do Povix.WalletBridge antes de iniciar novamente."
                    : "Não foi possível abrir a porta local 4781 (" + exception.SocketErrorCode + ").";
                return Fail(message, input, output, error, pauseOnError);
            }
        }

        private static int Fail(string message, TextReader input, TextWriter output, TextWriter error, bool pauseOnError)
        {
            error.WriteLine(message);
            if (pauseOnError)
            {
                output.WriteLine("Pressione Enter para fechar.");
                input.ReadLine();
            }
            return 1;
        }

        private static void WriteUsage(TextWriter output)
        {
            output.WriteLine("Uso: Povix.WalletBridge.exe [--origin https://endereco-do-dex]");
            output.WriteLine("Sem argumentos, o endereço do DEX é solicitado na janela.");
            output.WriteLine("Diagnóstico local: Povix.WalletBridge.exe --check-wallet \"C:\\pasta\\wallets.dat\"");
        }
    }
}
