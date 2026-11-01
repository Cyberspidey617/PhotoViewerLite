// Installer for Photo Viewer Lite. Installs for the current user only (no administrator rights):
//   %LOCALAPPDATA%\Programs\PhotoViewerLite\PhotoViewerLite.exe  (embedded in this file)
//   %LOCALAPPDATA%\Programs\PhotoViewerLite\Uninstall.exe        (a copy of this installer)
//   Start menu shortcut, optional desktop shortcut
//   "Open with" + Default Apps registration for picture types (never changes your current defaults)
//   Settings > Apps entry that runs Uninstall.exe
//
// Command line: Setup.exe [/quiet]            install (quiet: no UI, Open with on, no desktop shortcut)
//               Setup.exe /uninstall [/quiet] remove everything listed above

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PhotoViewerLite.Setup
{
    static class Product
    {
        public const string Name = "Photo Viewer Lite";
        public const string Version = "1.0.0";
        public const string Publisher = "Photo Viewer Lite";
        public const string Description = "A lightweight picture viewer modelled on Windows Photo Viewer.";
        public const string ExeName = "PhotoViewerLite.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string ProgId = "PhotoViewerLite.Image";
        public const string KeyName = "PhotoViewerLite";

        public const string ClassesKey = @"Software\Classes\";
        public const string CapabilitiesKey = @"Software\PhotoViewerLite\Capabilities";
        public const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + ExeName;
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + KeyName;
        public const string RegisteredApplicationsKey = @"Software\RegisteredApplications";

        public static readonly string[] Extensions =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff",
            ".ico", ".wdp", ".jxr", ".hdp", ".heic", ".heif", ".avif", ".webp", ".jxl", ".dds",
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf"
        };

        public static string InstallDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Programs", "PhotoViewerLite");
            }
        }

        public static string AppPath { get { return Path.Combine(InstallDir, ExeName); } }
        public static string UninstallerPath { get { return Path.Combine(InstallDir, UninstallerName); } }

        public static string StartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); }
        }

        public static string DesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); }
        }

        public static bool IsInstalled
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    return key != null && File.Exists(AppPath);
            }
        }
    }

    static class Installer
    {
        public static void Install(bool desktopShortcut, bool openWith)
        {
            EnsureViewerNotRunning();
            Directory.CreateDirectory(Product.InstallDir);
            ExtractViewer(Product.AppPath);

            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(Product.UninstallerPath),
                               StringComparison.OrdinalIgnoreCase))
                File.Copy(self, Product.UninstallerPath, true);

            RegisterApplication();
            if (openWith)
                RegisterOpenWith();
            else
                UnregisterOpenWith();

            Shortcuts.Create(Product.StartMenuShortcut, Product.AppPath);
            if (desktopShortcut)
                Shortcuts.Create(Product.DesktopShortcut, Product.AppPath);
            else
                DeleteFileIfPresent(Product.DesktopShortcut);

            NativeMethods.NotifyAssociationsChanged();
        }

        public static void Uninstall()
        {
            EnsureViewerNotRunning();
            DeleteFileIfPresent(Product.StartMenuShortcut);
            DeleteFileIfPresent(Product.DesktopShortcut);
            UnregisterOpenWith();
            RegistryKey hkcu = Registry.CurrentUser;  // shared root key: never dispose it
            {
                hkcu.DeleteSubKeyTree(Product.ClassesKey + Product.ProgId, false);
                hkcu.DeleteSubKeyTree(Product.ClassesKey + @"Applications\" + Product.ExeName, false);
                hkcu.DeleteSubKeyTree(@"Software\" + Product.KeyName, false);
                hkcu.DeleteSubKeyTree(Product.AppPathsKey, false);
                hkcu.DeleteSubKeyTree(Product.UninstallKey, false);
                using (RegistryKey registered = hkcu.OpenSubKey(Product.RegisteredApplicationsKey, true))
                {
                    if (registered != null)
                        registered.DeleteValue(Product.Name, false);
                }
            }
            NativeMethods.NotifyAssociationsChanged();

            DeleteFileIfPresent(Product.AppPath);
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (self.StartsWith(Path.GetFullPath(Product.InstallDir) + "\\", StringComparison.OrdinalIgnoreCase))
            {
                // A running program can't delete itself; let cmd remove the folder once this process exits.
                var cleanup = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 > nul & rd /s /q \"" + Product.InstallDir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath()
                };
                Process.Start(cleanup);
            }
            else if (Directory.Exists(Product.InstallDir))
            {
                Directory.Delete(Product.InstallDir, true);
            }
        }

        static void EnsureViewerNotRunning()
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Product.ExeName)))
            {
                // An unreadable path counts as ours: refusing is safer than replacing a running program.
                string path = NativeMethods.ImagePath(process.Id);
                if (path == null || string.Equals(path, Product.AppPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Product.Name + " is running. Close it and try again.");
            }
        }

        static void ExtractViewer(string destination)
        {
            string temp = destination + ".new";
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(Product.ExeName))
            {
                if (payload == null)
                    throw new InvalidOperationException("This installer is damaged: the program is missing from it.");
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    payload.CopyTo(output);
            }
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(temp, destination);
        }

        static void RegisterApplication()
        {
            string app = Product.AppPath;
            string command = "\"" + app + "\" \"%1\"";
            string icon = "\"" + app + "\",0";

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.ClassesKey + Product.ProgId))
            {
                key.SetValue("", Product.Name + " picture");
                key.SetValue("FriendlyTypeName", Product.Name + " picture");
                using (RegistryKey sub = key.CreateSubKey("DefaultIcon")) sub.SetValue("", icon);
                using (RegistryKey sub = key.CreateSubKey(@"shell\open")) sub.SetValue("FriendlyAppName", Product.Name);
                using (RegistryKey sub = key.CreateSubKey(@"shell\open\command")) sub.SetValue("", command);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.ClassesKey + @"Applications\" + Product.ExeName))
            {
                key.SetValue("FriendlyAppName", Product.Name);
                using (RegistryKey sub = key.CreateSubKey(@"shell\open\command")) sub.SetValue("", command);
                using (RegistryKey sub = key.CreateSubKey("SupportedTypes"))
                {
                    foreach (string ext in Product.Extensions)
                        sub.SetValue(ext, "");
                }
            }

            // Lets Settings > Default apps offer Photo Viewer Lite for each type.
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.CapabilitiesKey))
            {
                key.SetValue("ApplicationName", Product.Name);
                key.SetValue("ApplicationDescription", Product.Description);
                key.SetValue("ApplicationIcon", icon);
                using (RegistryKey sub = key.CreateSubKey("FileAssociations"))
                {
                    foreach (string ext in Product.Extensions)
                        sub.SetValue(ext, Product.ProgId);
                }
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.RegisteredApplicationsKey))
                key.SetValue(Product.Name, Product.CapabilitiesKey);

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.AppPathsKey))
            {
                key.SetValue("", app);
                key.SetValue("Path", Product.InstallDir);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.UninstallKey))
            {
                key.SetValue("DisplayName", Product.Name);
                key.SetValue("DisplayVersion", Product.Version);
                key.SetValue("Publisher", Product.Publisher);
                key.SetValue("DisplayIcon", app + ",0");
                key.SetValue("InstallLocation", Product.InstallDir);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString", "\"" + Product.UninstallerPath + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + Product.UninstallerPath + "\" /uninstall /quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)Math.Ceiling(DirectorySize(Product.InstallDir) / 1024.0),
                             RegistryValueKind.DWord);
            }
        }

        // Adds Photo Viewer Lite to each type's "Open with" list without touching the user's default app.
        static void RegisterOpenWith()
        {
            foreach (string ext in Product.Extensions)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.ClassesKey + ext + @"\OpenWithProgids"))
                    key.SetValue(Product.ProgId, new byte[0], RegistryValueKind.None);
            }
        }

        static void UnregisterOpenWith()
        {
            foreach (string ext in Product.Extensions)
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Product.ClassesKey + ext + @"\OpenWithProgids", true))
                {
                    if (key != null)
                        key.DeleteValue(Product.ProgId, false);
                }
            }
        }

        static long DirectorySize(string dir)
        {
            return new DirectoryInfo(dir).GetFiles().Sum(f => f.Length);
        }

        static void DeleteFileIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static class Shortcuts
    {
        public static void Create(string shortcutPath, string target)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                                  new object[] { shortcutPath });
                Type type = shortcut.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                                  new object[] { Path.GetDirectoryName(target) });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                                  new object[] { Product.Description });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                                  new object[] { target + ",0" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null)
                    Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    static class NativeMethods
    {
        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // Needs only limited query rights, unlike Process.MainModule.
        public static string ImagePath(int processId)
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var name = new System.Text.StringBuilder(1024);
                int size = name.Capacity;
                return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public static void NotifyAssociationsChanged()
        {
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    sealed class SetupWindow : Window
    {
        readonly CheckBox desktop = new CheckBox { Content = "Create a desktop shortcut", Margin = new Thickness(0, 4, 0, 4) };
        readonly CheckBox openWith = new CheckBox { Content = "Add to \"Open with\" for picture files", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        readonly CheckBox launch = new CheckBox { Content = "Open " + Product.Name + " when finished", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        readonly Button install = new Button { Width = 96, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        readonly Button cancel = new Button { Content = "Cancel", Width = 96, Height = 30, IsCancel = true };

        public SetupWindow()
        {
            bool upgrade = Product.IsInstalled;
            Title = Product.Name + " Setup";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock
            {
                Text = (upgrade ? "Update " : "Install ") + Product.Name + " " + Product.Version,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            panel.Children.Add(new TextBlock
            {
                Text = Product.Description + "\nIt installs for your account only, so no administrator rights are needed. " +
                       "Your current default picture app isn't changed.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock { Text = "Install location:", Opacity = 0.7 });
            panel.Children.Add(new TextBlock
            {
                Text = Product.InstallDir,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(desktop);
            panel.Children.Add(openWith);
            panel.Children.Add(launch);

            install.Content = upgrade ? "Update" : "Install";
            install.Click += OnInstall;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            buttons.Children.Add(install);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
        }

        void OnInstall(object sender, RoutedEventArgs e)
        {
            install.IsEnabled = false;
            cancel.IsEnabled = false;
            try
            {
                Installer.Install(desktop.IsChecked == true, openWith.IsChecked == true);
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException ||
                      error is COMException || error is System.Security.SecurityException))
                    throw;
                MessageBox.Show(this, "Setup couldn't finish.\n\n" + error.Message, Title,
                                MessageBoxButton.OK, MessageBoxImage.Error);
                install.IsEnabled = true;
                cancel.IsEnabled = true;
                return;
            }

            if (launch.IsChecked == true)
                Process.Start(new ProcessStartInfo(Product.AppPath) { UseShellExecute = true });
            else
                MessageBox.Show(this, Product.Name + " is installed. You'll find it in the Start menu.", Title,
                                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool quiet = args.Any(a => string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase));

            if (uninstall)
                return RunUninstall(quiet);

            if (quiet)
            {
                try
                {
                    Installer.Install(false, true);
                    return 0;
                }
                catch (Exception error)
                {
                    if (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException ||
                        error is COMException || error is System.Security.SecurityException)
                        return 1;
                    throw;
                }
            }

            return new Application().Run(new SetupWindow());
        }

        static int RunUninstall(bool quiet)
        {
            const string title = Product.Name + " Setup";
            if (!quiet && MessageBox.Show("Remove " + Product.Name + " from this PC?\n\nYour pictures aren't affected.",
                                          title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return 1;
            try
            {
                Installer.Uninstall();
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException ||
                      error is System.Security.SecurityException))
                    throw;
                if (!quiet)
                    MessageBox.Show("Uninstall couldn't finish.\n\n" + error.Message, title,
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            if (!quiet)
                MessageBox.Show(Product.Name + " has been removed.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
    }
}
