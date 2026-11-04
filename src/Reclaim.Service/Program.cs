using System;
using System.IO;
using System.ServiceProcess;
using Reclaim.Core.Domain;
using Reclaim.Core.Hosting;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Service
{
    internal static class Program
    {
        private const string Usage =
            "Reclaim.Service.exe <lệnh> [--result <file>]\r\n" +
            "  install     cài đặt và khởi động dịch vụ (cần quyền quản trị)\r\n" +
            "  uninstall   gỡ cài đặt (cần quyền quản trị)\r\n" +
            "  run         chạy trong console để gỡ lỗi (cần quyền quản trị, không cài dịch vụ)\r\n" +
            "  --service   (dành cho Windows Service Control Manager)";

        private static int Main(string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
            string resultFile = GetOption(args, "--result");

            try
            {
                switch (verb)
                {
                    case "--service":
                        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                        {
                            try { new FileLog(AppPaths.LogFile, false).Error("Unhandled: " + e.ExceptionObject); } catch { }
                        };
                        ServiceBase.Run(new ReclaimService());
                        return 0;

                    case "install":
                        AppInstaller.Install(AppDomain.CurrentDomain.BaseDirectory);
                        return Report(resultFile, true, "OK");

                    case "uninstall":
                        AppInstaller.Uninstall();
                        return Report(resultFile, true, "OK");

                    case "run":
                        return RunConsole();

                    default:
                        Console.WriteLine(Usage);
                        return 2;
                }
            }
            catch (ReclaimException ex)
            {
                return Report(resultFile, false, ex.Message);
            }
            catch (Exception ex)
            {
                return Report(resultFile, false, ex.Message);
            }
        }

        private static int RunConsole()
        {
            using (var host = new ReclaimHost(true))
            {
                host.Start();
                Console.WriteLine("Reclaim đang chạy ở chế độ console. Nhấn Enter để dừng.");
                Console.ReadLine();
                host.Stop(true);
            }
            return 0;
        }

        private static int Report(string resultFile, bool ok, string message)
        {
            if (!string.IsNullOrEmpty(resultFile))
            {
                try { File.WriteAllText(resultFile, message); } catch { }
            }
            if (ok) Console.WriteLine(message); else Console.Error.WriteLine(message);
            return ok ? 0 : 1;
        }

        private static string GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}
