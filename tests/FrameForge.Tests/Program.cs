using System.Reflection;
using Xunit;

namespace FrameForge.Tests;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("FrameForge test suite");
        Console.WriteLine("=====================");
        return await MiniTestHost.RunAsync(Assembly.GetExecutingAssembly(), Console.Out).ConfigureAwait(false);
    }
}
