using Softbody.FluidGpu;

namespace Softbody.Tests;

static partial class Program
{
    static string FluidHlsl() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FluidSim.hlsl"));

    /// <summary>Compiles every kernel of the GPU fluid and prints its bindings (a quick loop while writing the HLSL).</summary>
    static int GpuCompileAll()
    {
        string src = FluidHlsl();
        int bad = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var k in GpuFluidSim.KernelNames)
        {
            try
            {
                var code = HlslCompiler.Compile(src, k);
                var b = HlslCompiler.Reflect(code);
                int uavs = b.Count(x => x.IsUav);
                Console.WriteLine($"  {k,-16} {code.Length,6} bytes  {uavs} UAVs: {string.Join(", ", b.Select(x => x.Name + (x.IsUav ? "u" : x.IsSrv ? "t" : "b") + x.Slot))}");
                if (uavs > 8) { Console.WriteLine("    more than 8 UAVs"); bad++; }
            }
            catch (Exception ex) { Console.WriteLine($"  {k}: {ex.Message}"); bad++; }
        }
        Console.WriteLine($"{GpuFluidSim.KernelNames.Length} kernels in {sw.ElapsedMilliseconds} ms, {bad} failed");
        return bad == 0 ? 0 : 1;
    }
}
