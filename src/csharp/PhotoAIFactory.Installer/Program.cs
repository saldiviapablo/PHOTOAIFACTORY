using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace PhotoAIFactory.Installer;

public static class Program
{
    public static int Main(string[] args)
    {
        var mode = "install";
        string? customSource = null;
        string? customTarget = null;
        var quiet = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)) mode = "uninstall";
            else if (arg.Equals("--repair", StringComparison.OrdinalIgnoreCase)) mode = "repair";
            else if (arg.Equals("--upgrade", StringComparison.OrdinalIgnoreCase)) mode = "upgrade";
            else if (arg.Equals("--install", StringComparison.OrdinalIgnoreCase)) mode = "install";
            else if (arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase) || arg.Equals("-q", StringComparison.OrdinalIgnoreCase)) quiet = true;
            else if (arg.Equals("--source-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) customSource = args[++i];
            else if (arg.Equals("--target-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) customTarget = args[++i];
        }

        if (!quiet)
        {
            Console.WriteLine("============================================================");
            Console.WriteLine(" PHOTO AI FACTORY -- NATIVE WINDOWS SETUP / INSTALLER v1.0.0-rc.1");
            Console.WriteLine("============================================================");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultTarget = Path.Combine(localAppData, "Programs", "PhotoAIFactory");
        var targetDir = customTarget ?? defaultTarget;

        try
        {
            switch (mode)
            {
                case "install":
                case "upgrade":
                    return ExecuteInstall(customSource, targetDir, mode == "upgrade", quiet);
                case "repair":
                    return ExecuteRepair(customSource, targetDir, quiet);
                case "uninstall":
                    return ExecuteUninstall(targetDir, quiet);
                default:
                    Console.WriteLine($"Unknown mode: {mode}");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"INSTALLER OPERATION FAILED: {ex.Message}");
                Console.ResetColor();
            }
            return 2;
        }
    }

    private static int ExecuteInstall(string? customSource, string targetDir, bool isUpgrade, bool quiet)
    {
        if (!quiet)
        {
            Console.WriteLine($"[1/4] Target Directory: {targetDir}");
            Console.WriteLine("[2/4] Deploying application payload...");
        }

        Directory.CreateDirectory(targetDir);

        // 1. Check for Embedded Resource ZIP payload
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("app_payload.zip", StringComparison.OrdinalIgnoreCase) || n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

        if (resourceName != null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var destinationPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!destinationPath.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ZipSlip detected in embedded payload.");
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    var dir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(customSource) && Directory.Exists(customSource))
        {
            var sourceDir = customSource;
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var dest = Path.Combine(targetDir, relative);
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                File.Copy(file, dest, overwrite: true);
            }
        }
        else
        {
            throw new InvalidOperationException("Standalone installer payload missing and no explicit --source-dir provided.");
        }

        var appExe = Path.Combine(targetDir, "PhotoAIFactory.App.exe");
        if (!File.Exists(appExe))
        {
            throw new FileNotFoundException("PhotoAIFactory.App.exe missing after deployment.", appExe);
        }

        // Copy setup tool itself into target directory for uninstaller registration
        var currentExe = Environment.ProcessPath;
        var targetSetup = Path.Combine(targetDir, "PhotoAIFactory-Setup.exe");
        if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
        {
            try
            {
                File.Copy(currentExe, targetSetup, overwrite: true);
            }
            catch
            {
            }
        }

        if (!quiet) Console.WriteLine("[3/4] Registering Windows Uninstall and Start Menu entries...");
        var uninstallerPath = File.Exists(targetSetup) ? targetSetup : appExe;
        RegisterUninstall(targetDir, uninstallerPath, appExe);
        CreateStartMenuShortcut(appExe);

        if (!quiet)
        {
            Console.WriteLine("[4/4] Finalizing installation...");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(isUpgrade ? "UPGRADE COMPLETED SUCCESSFULLY." : "INSTALLATION COMPLETED SUCCESSFULLY.");
            Console.ResetColor();
        }
        return 0;
    }

    private static int ExecuteRepair(string? customSource, string targetDir, bool quiet)
    {
        if (!quiet) Console.WriteLine($"Executing repair on {targetDir}...");
        return ExecuteInstall(customSource, targetDir, isUpgrade: false, quiet);
    }

    private static int ExecuteUninstall(string targetDir, bool quiet)
    {
        if (!quiet) Console.WriteLine($"[1/3] Target for uninstallation: {targetDir}");

        if (!quiet) Console.WriteLine("[2/3] Removing Windows Uninstall registry keys and shortcuts...");
        UnregisterUninstall();
        RemoveStartMenuShortcut();

        if (Directory.Exists(targetDir))
        {
            if (!quiet) Console.WriteLine("[3/3] Removing application binaries...");

            // Delete non-locked files and subdirectories
            foreach (var file in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { }
            }
            foreach (var dir in Directory.GetDirectories(targetDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { Directory.Delete(dir, false); } catch { }
            }

            try
            {
                Directory.Delete(targetDir, recursive: true);
            }
            catch
            {
                // If installer exe is locked, schedule deferred cleanup
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", $"/c timeout /t 1 /nobreak > NUL & rmdir /s /q \"{targetDir}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                }
                catch
                {
                }
            }
        }

        if (!quiet)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("UNINSTALL COMPLETED. User project data and managed originals preserved intact.");
            Console.ResetColor();
        }
        return 0;
    }

    private static void RegisterUninstall(string installDir, string uninstallerPath, string displayIconPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PhotoAIFactory");
                if (key != null)
                {
                    key.SetValue("DisplayName", "PHOTO AI FACTORY");
                    key.SetValue("DisplayVersion", "1.0.0-rc.1");
                    key.SetValue("Publisher", "PHOTO AI FACTORY Team");
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("DisplayIcon", displayIconPath);
                    key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
                    key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --quiet");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Registry uninstall registration skipped: {ex.Message}");
        }
    }

    private static void CreateStartMenuShortcut(string targetExe)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (!string.IsNullOrEmpty(startMenu) && Directory.Exists(startMenu))
                {
                    var shortcutFolder = Path.Combine(startMenu, "PHOTO AI FACTORY");
                    Directory.CreateDirectory(shortcutFolder);
                    var lnkPath = Path.Combine(shortcutFolder, "PHOTO AI FACTORY.lnk");
                    var workDir = Path.GetDirectoryName(targetExe) ?? "";

                    var psCmd = $"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{lnkPath}'); $s.TargetPath = '{targetExe}'; $s.WorkingDirectory = '{workDir}'; $s.Save()";
                    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCmd}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit();
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static void RemoveStartMenuShortcut()
    {
        try
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrEmpty(startMenu))
            {
                var shortcutFolder = Path.Combine(startMenu, "PHOTO AI FACTORY");
                if (Directory.Exists(shortcutFolder))
                {
                    Directory.Delete(shortcutFolder, recursive: true);
                }
            }
        }
        catch
        {
        }
    }

    private static void UnregisterUninstall()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PhotoAIFactory", throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Registry key cleanup skipped: {ex.Message}");
        }
    }
}
