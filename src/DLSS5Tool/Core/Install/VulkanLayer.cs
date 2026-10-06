using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace DLSS5Tool.Core.Install;

/// <summary>Instructions for the elevated helper run: mirrors what ReShade's own setup does for Vulkan.</summary>
public sealed class VulkanLayerSpec
{
    public string Action { get; set; } = "install";
    public string ExePath { get; set; } = string.Empty;
    public bool Is64Bit { get; set; }
    public string? Dll32 { get; set; }
    public string? Dll64 { get; set; }
    public string? Json32 { get; set; }
    public string? Json64 { get; set; }
    /// <summary>Defaults to %ProgramData%\ReShade. Tests point it elsewhere.</summary>
    public string? LayerRoot { get; set; }
    /// <summary>Registry root under which Software\Khronos\... is created; HKLM by default.</summary>
    public string? RegistryRoot { get; set; }
}

public static class VulkanLayer
{
    public const string ElevatedSwitch = "--elevated-step";

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade");

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Runs the spec, elevating through UAC when this process is not an administrator. Returns the exit code.</summary>
    public static int Run(VulkanLayerSpec spec, IProgress<string>? progress = null)
    {
        if (IsElevated())
        {
            Execute(spec);
            return 0;
        }
        AppPaths.EnsureDirectories();
        var specPath = Path.Combine(AppPaths.Stage, $"vulkan-{Guid.NewGuid():N}.json");
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec));
        progress?.Report("Waiting for administrator approval (Vulkan layer registration)...");
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.ExePath,
            Arguments = $"{ElevatedSwitch} \"{specPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the elevated helper.");
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223; // the UAC prompt was declined
        }
        finally
        {
            try { File.Delete(specPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Entry point of the elevated process.</summary>
    public static int ExecuteFromFile(string specPath)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<VulkanLayerSpec>(File.ReadAllText(specPath)) ?? throw new InvalidDataException("empty spec");
            Execute(spec);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(specPath + ".error", ex.ToString()); } catch { /* nothing else to do */ }
            return 1;
        }
    }

    public static void Execute(VulkanLayerSpec spec)
    {
        var root = spec.LayerRoot ?? DefaultRoot;
        Directory.CreateDirectory(root);
        var apps = Path.Combine(root, "ReShadeApps.ini");

        if (spec.Action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(apps))
            {
                var ini = IniFile.Load(apps);
                var list = ini.GetList(string.Empty, "Apps");
                var kept = list.Where(e => !e.TrimEnd('\\').Equals(spec.ExePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count != list.Count)
                {
                    ini.SetList(string.Empty, "Apps", kept);
                    ini.Save(apps);
                }
            }
            return;
        }

        foreach (var (bits, dll, json) in new[] { (32, spec.Dll32, spec.Json32), (64, spec.Dll64, spec.Json64) })
        {
            var dllTarget = Path.Combine(root, $"ReShade{bits}.dll");
            var jsonTarget = Path.Combine(root, $"ReShade{bits}.json");
            var replaceDll = dll != null && (!File.Exists(dllTarget) || (bits == 64) == spec.Is64Bit);
            if (replaceDll)
            {
                File.Copy(dll!, dllTarget, true);
            }
            if (json != null && !File.Exists(jsonTarget))
            {
                File.Copy(json, jsonTarget, true);
            }
            if (File.Exists(jsonTarget))
            {
                RegisterLayer(spec.RegistryRoot, bits, jsonTarget);
            }
        }

        try
        {
            // Let packaged (UWP) apps load the layer, exactly as ReShade's setup does.
            var sid = new SecurityIdentifier("S-1-15-2-1");
            var di = new DirectoryInfo(root);
            var acl = di.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not adjust the ReShade layer folder ACL: " + ex.Message);
        }

        var appsIni = IniFile.Load(apps);
        var current = appsIni.GetList(string.Empty, "Apps");
        var exeLeaf = Path.GetFileName(spec.ExePath);
        var cleaned = current.Where(e => e.TrimEnd('\\').Equals(spec.ExePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                         || !(Path.GetFileName(e).Equals(exeLeaf, StringComparison.OrdinalIgnoreCase) && !File.Exists(e))).ToList();
        if (!cleaned.Any(e => e.TrimEnd('\\').Equals(spec.ExePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            cleaned.Add(spec.ExePath);
        }
        appsIni.SetList(string.Empty, "Apps", cleaned);
        appsIni.Save(apps);
    }

    private static void RegisterLayer(string? registryRoot, int bits, string jsonPath)
    {
        var sub = bits == 32 && Environment.Is64BitOperatingSystem ? @"Software\Wow6432Node\Khronos\Vulkan\ImplicitLayers" : @"Software\Khronos\Vulkan\ImplicitLayers";
        RegistryKey baseKey;
        var dispose = false;
        if (registryRoot == null)
        {
            baseKey = Registry.LocalMachine;
        }
        else
        {
            baseKey = Registry.CurrentUser.CreateSubKey(registryRoot, true);
            dispose = true;
        }
        try
        {
            using var key = baseKey.CreateSubKey(sub, true);
            key.SetValue(jsonPath, 0, RegistryValueKind.DWord);
        }
        finally
        {
            if (dispose) baseKey.Dispose();
        }
    }

    /// <summary>True when the machine-wide layer for this bitness exists, is an add-on build, and is registered.</summary>
    public static (bool dllOk, bool registered, bool appListed, string? version) Inspect(string exePath, bool is64Bit, string? layerRoot = null)
    {
        var root = layerRoot ?? DefaultRoot;
        var bits = is64Bit ? 64 : 32;
        var dll = Path.Combine(root, $"ReShade{bits}.dll");
        var json = Path.Combine(root, $"ReShade{bits}.json");
        string? version = null;
        var dllOk = false;
        if (File.Exists(dll))
        {
            version = Analysis.GameAnalyzer.FileVersion(dll);
            var pe = PeFile.TryRead(dll, readExports: true);
            dllOk = pe != null && pe.Exports.Contains("ReShadeRegisterAddon");
        }
        var registered = false;
        try
        {
            var sub = bits == 32 && Environment.Is64BitOperatingSystem ? @"Software\Wow6432Node\Khronos\Vulkan\ImplicitLayers" : @"Software\Khronos\Vulkan\ImplicitLayers";
            using var key = Registry.LocalMachine.OpenSubKey(sub);
            registered = key != null && key.GetValueNames().Any(v => v.Equals(json, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* unreadable registry: report unregistered */ }
        var listed = false;
        var apps = Path.Combine(root, "ReShadeApps.ini");
        if (File.Exists(apps))
        {
            listed = IniFile.Load(apps).GetList(string.Empty, "Apps").Any(e => e.TrimEnd('\\').Equals(exePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        return (dllOk, registered, listed, version);
    }
}
