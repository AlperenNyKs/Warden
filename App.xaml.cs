using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Warden
{
    public partial class App : System.Windows.Application
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private static readonly System.Security.Principal.SecurityIdentifier AdminsSid =
            new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly System.Security.Principal.SecurityIdentifier EveryoneSid =
            new(System.Security.Principal.WellKnownSidType.WorldSid, null);

        private TrayApplicationContext? _trayContext;
        private static Mutex? _instanceMutex;
        private static EventWaitHandle? _wakeEvent;

        protected override async void OnStartup(StartupEventArgs e)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Global crash logging en başta kurulur (başlangıç adımlarındaki hatalar da loglansın)
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => LogCrash("AppDomain", ev.ExceptionObject as Exception);
            this.DispatcherUnhandledException += (s, ev) => { LogCrash("Dispatcher", ev.Exception); ev.Handled = true; };
            TaskScheduler.UnobservedTaskException += (s, ev) => { LogCrash("TaskScheduler", ev.Exception); ev.SetObserved(); };

            // 0. CLI modu (--discover): tek-örnek kontrolünden ÖNCE işlenir. Eskiden Warden tepside açıkken
            //    "Warden.exe --discover" çalıştırıldığında yalnızca mevcut pencere öne getiriliyor, keşif yapılmıyordu.
            if (e.Args.Length > 0 && (e.Args[0] == "--discover" || e.Args[0] == "-d"))
            {
                try { await DiscoverPresetsCliAsync(); }
                catch (Exception ex) { LogCrash("CLI", ex); }
                this.Shutdown();
                return;
            }

            // 1. Single Instance Check (Tek uygulama çalışmasını garantile)
            const string mutexName = "Global\\Warden_SingleInstance";
            const string eventName = "Global\\Warden_WakeupEvent";

            bool createdNew;
            try
            {
                // En az yetki: yalnızca Administrators tam yetkili (ikinci kopya MutexAcl.Create ile tam erişim ister;
                // uygulama requireAdministrator olduğundan her kopya yönetici). Everyone yalnızca bekleyebilir.
                // Eskiden Everyone'a FullControl veriliyordu (ACL değiştirme/sahiplenme dahil).
                var mSec = new System.Security.AccessControl.MutexSecurity();
                mSec.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(AdminsSid, System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                mSec.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(EveryoneSid, System.Security.AccessControl.MutexRights.Synchronize, System.Security.AccessControl.AccessControlType.Allow));
                _instanceMutex = System.Threading.MutexAcl.Create(true, mutexName, out createdNew, mSec);
            }
            catch
            {
                try
                {
                    _instanceMutex = new Mutex(true, mutexName, out createdNew);
                }
                catch (UnauthorizedAccessException)
                {
                    // Mutex var ama erişilemiyor → başka bir kopya çalışıyor demektir. Eskiden exception
                    // OnStartup'tan kaçıyor, tepsi ikonu olmayan "zombi" bir süreç kalıyordu.
                    createdNew = false;
                }
            }

            if (!createdNew)
            {
                // Uygulama zaten arka planda çalışıyor.
                // Çalışan ilk kopyaya pencereyi ekrana getirmesi için sinyal gönder.
                try
                {
                    using var wakeEvent = EventWaitHandle.OpenExisting(eventName);
                    wakeEvent.Set();
                }
                catch { }

                this.Shutdown();
                return;
            }

            // Sinyal dinleyicisini başlat (Kullanıcı masaüstünden tekrar açınca pencere açılsın)
            try
            {
                // Uyandırma sinyali: başka kopyanın OpenExisting + Set yapabilmesi için Synchronize | Modify yeterli
                var eSec = new System.Security.AccessControl.EventWaitHandleSecurity();
                eSec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(AdminsSid, System.Security.AccessControl.EventWaitHandleRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                eSec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(EveryoneSid,
                    System.Security.AccessControl.EventWaitHandleRights.Synchronize | System.Security.AccessControl.EventWaitHandleRights.Modify,
                    System.Security.AccessControl.AccessControlType.Allow));
                _wakeEvent = System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, eventName, out _, eSec);
            }
            catch
            {
                _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            }

            try
            {
                _ = Task.Run(() =>
                {
                    while (true)
                    {
                        try
                        {
                            _wakeEvent.WaitOne();
                            this.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _trayContext?.ShowMainWindow();
                            }));
                        }
                        catch { break; }
                    }
                });
            }
            catch { }

            base.OnStartup(e);


            try
            {
                // Start Tray and Watcher
                _trayContext = new TrayApplicationContext(this);
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Exception? inner = ex.InnerException;
                while (inner != null) {
                    msg += "\nINNER: " + inner.Message;
                    inner = inner.InnerException;
                }
                System.Windows.MessageBox.Show("ONSTARTUP CRASH:\n" + msg);
                LogCrash("OnStartup", ex);
                this.Shutdown();
            }
        }

        private static readonly object CrashLogLock = new();

        private void LogCrash(string source, Exception? ex)
        {
            // Crash handler'ın kendisi asla exception fırlatmamalı (aksi halde asıl hata da kaybolur)
            try
            {
                lock (CrashLogLock) WriteCrashLog(source, ex);
            }
            catch { }
        }

        private static void WriteCrashLog(string source, Exception? ex)
        {
            string logDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Warden", "logs");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "crash.log");

            // 512 KB'ı aşarsa eski logu .bak'a taşı
            try
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024)
                {
                    string backupPath = logPath + ".bak";
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(logPath, backupPath);
                }
            }
            catch { }

            // ex.ToString() iç exception'ları da içerir; ayrıca tekrar eklemeye gerek yok
            string message = $"[{DateTime.Now}] CRASH in {source}: {ex?.ToString() ?? "No exception details"}{Environment.NewLine}{Environment.NewLine}";

            try { File.AppendAllText(logPath, message); } catch { }
            // MessageBox kaldırıldı — crash tray notification veya log dosyasından görülür
        }

        private async Task DiscoverPresetsCliAsync()
        {
            AttachConsole(-1);
            
            // Redirect standard output to console
            using var writer = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);

            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine(" SteelSeries Sonar EQ Preset Discovery");
            Console.WriteLine("========================================");
            
            try
            {
                using var client = new SteelSeriesClient();
                Console.WriteLine("Finding SteelSeries GG API port...");
                string address = await client.GetSonarAddressAsync();
                Console.WriteLine($"Sonar Web Server found at: {address}");
                Console.WriteLine("Fetching presets list...");

                var list = await client.GetConfigsAsync();
                Console.WriteLine("\nAvailable Sonar Presets (Game Channel):");
                Console.WriteLine("----------------------------------------");
                
                var sb = new StringBuilder();
                sb.AppendLine("========================================");
                sb.AppendLine(" SteelSeries Sonar EQ Preset Discovery");
                sb.AppendLine("========================================");
                sb.AppendLine($"Sonar Web Server: {address}");
                sb.AppendLine("----------------------------------------");

                foreach (var item in list)
                {
                    if (item.virtualAudioDevice == "game")
                    {
                        string line = $"Name: {item.name,-30} | UUID: {item.id}";
                        Console.WriteLine(line);
                        sb.AppendLine(line);
                    }
                }
                Console.WriteLine("----------------------------------------");
                Console.WriteLine("You can copy these UUIDs into your config.json rules.");
                sb.AppendLine("----------------------------------------");
                
                string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presets_list.txt");
                await File.WriteAllTextAsync(outputPath, sb.ToString());
                Console.WriteLine($"Presets list saved to: {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during discovery: {ex.Message}");
            }
            
            Console.WriteLine("========================================");
            Console.WriteLine();
        }
    }
}
