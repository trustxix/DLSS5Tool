using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DLSS5Tool.Core;

public enum GpuTier
{
    /// <summary>Not NVIDIA, or an NVIDIA card without tensor cores.</summary>
    Unsupported,
    Rtx20,
    Rtx30,
    Rtx40,
    Rtx50,
}

public sealed record GpuInfo(string Name, GpuTier Tier, string? DriverVersion)
{
    public bool IsRtx => Tier != GpuTier.Unsupported;

    public string TierId => Tier switch
    {
        GpuTier.Rtx20 => "rtx20",
        GpuTier.Rtx30 => "rtx30",
        GpuTier.Rtx40 => "rtx40",
        GpuTier.Rtx50 => "rtx50",
        _ => "none",
    };

    public override string ToString() => DriverVersion == null ? Name : $"{Name} (driver {DriverVersion})";

    private static readonly Regex RtxModel = new(@"\bRTX\s*(?:A|PRO\s*)?(\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GpuTier Classify(string adapterName)
    {
        var n = adapterName.ToLowerInvariant();
        if (n.Contains("remote display") || n.Contains("hyper-v") || n.Contains("virtual") || n.Contains("basic render") || n.Contains("parsec") || n.Contains("vmware") || n.Contains("virtualbox"))
        {
            return GpuTier.Unsupported;
        }
        if (!(n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("quadro")))
        {
            return GpuTier.Unsupported;
        }
        var m = RtxModel.Match(adapterName);
        if (!m.Success) return GpuTier.Unsupported;
        if (adapterName.Contains("RTX A", StringComparison.OrdinalIgnoreCase) || adapterName.Contains("RTX PRO", StringComparison.OrdinalIgnoreCase))
        {
            return GpuTier.Unsupported;
        }
        var model = int.Parse(m.Groups[1].Value);
        return model switch
        {
            >= 5000 and <= 5999 => GpuTier.Rtx50,
            >= 4000 and <= 4999 => GpuTier.Rtx40,
            >= 3000 and <= 3999 => GpuTier.Rtx30,
            >= 2000 and <= 2999 => GpuTier.Rtx20,
            _ => GpuTier.Unsupported,
        };
    }

    /// <summary>NVIDIA's registry DriverVersion (e.g. 32.0.16.1692) expressed as the public number (616.92).</summary>
    public static string? ToPublicDriverVersion(string? registryVersion)
    {
        if (string.IsNullOrWhiteSpace(registryVersion)) return null;
        var parts = registryVersion.Split('.');
        if (parts.Length < 4) return registryVersion;
        var digits = (parts[2] + parts[3]).Where(char.IsDigit).ToArray();
        if (digits.Length < 5) return registryVersion;
        var tail = new string(digits[^5..]);
        return tail[..3] + "." + tail[3..];
    }

    /// <summary>Enumerates display adapters from the device class registry key; the best DLSS-capable one wins.</summary>
    public static IReadOnlyList<GpuInfo> Detect()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!sub.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;
                var provider = k?.GetValue("ProviderName") as string ?? string.Empty;
                var ver = k?.GetValue("DriverVersion") as string;
                var tier = Classify(desc);
                var driver = provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    ? ToPublicDriverVersion(ver)
                    : ver;
                list.Add(new GpuInfo(desc, tier, driver));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GPU detection failed: " + ex.Message);
        }
        return list.DistinctBy(g => g.Name).ToList();
    }

    public static GpuInfo Best(IEnumerable<GpuInfo> gpus)
    {
        GpuInfo? best = null;
        foreach (var g in gpus)
        {
            if (best == null || Rank(g.Tier) < Rank(best.Tier)) best = g;
        }
        return best ?? new GpuInfo("No display adapter found", GpuTier.Unsupported, null);

        static int Rank(GpuTier t) => t switch { GpuTier.Rtx50 => 0, GpuTier.Rtx40 => 1, GpuTier.Rtx30 => 2, GpuTier.Rtx20 => 3, _ => 9 };
    }

    public static GpuInfo DetectBest() => Best(Detect());
}
