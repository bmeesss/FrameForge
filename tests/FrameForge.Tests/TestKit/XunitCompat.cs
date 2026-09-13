using System.Reflection;
using System.Text;

namespace Xunit;

/// <summary>
/// Minimal offline-friendly stand-in for xUnit attributes and Assert.
/// Allows writing tests in familiar xUnit style without nuget.org.
/// When nuget.org is available, replace with the real xunit packages.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute
{
    public string? Skip { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TheoryAttribute : Attribute
{
    public string? Skip { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute : Attribute
{
    public InlineDataAttribute(params object?[] data) => Data = data;
    public object?[] Data { get; }
}

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new XunitException(message ?? "Expected true.");
        }
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
        {
            throw new XunitException(message ?? "Expected false.");
        }
    }

    public static void Null(object? obj) => True(obj is null, "Expected null.");
    public static void NotNull(object? obj) => True(obj is not null, "Expected non-null.");

    public static void Equal<T>(T expected, T actual) =>
        True(Equals(expected, actual), $"Expected: {expected}{Environment.NewLine}Actual:   {actual}");

    public static void Equal(double expected, double actual, int precision)
    {
        var tol = Math.Pow(10, -precision);
        True(Math.Abs(expected - actual) <= tol + double.Epsilon,
            $"Expected: {expected} (±{tol}){Environment.NewLine}Actual:   {actual}");
    }

    public static void NotEqual<T>(T expected, T actual) =>
        True(!Equals(expected, actual), $"Expected values to differ: {expected}");

    public static void Contains(string expectedSubstring, string? actual) =>
        True(actual is not null && actual.Contains(expectedSubstring, StringComparison.Ordinal),
            $"Expected '{actual}' to contain '{expectedSubstring}'.");

    public static void Contains(string expectedSubstring, string? actual, StringComparison comparison) =>
        True(actual is not null && actual.Contains(expectedSubstring, comparison),
            $"Expected '{actual}' to contain '{expectedSubstring}' ({comparison}).");

    public static void Contains<T>(IEnumerable<T> collection, Predicate<T> predicate) =>
        True(collection.Any(x => predicate(x)), "Expected collection to contain a matching element.");

    public static void DoesNotContain(string expectedSubstring, string? actual) =>
        True(actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal),
            $"Expected '{actual}' not to contain '{expectedSubstring}'.");

    public static void DoesNotContain(string expectedSubstring, string? actual, StringComparison comparison) =>
        True(actual is null || !actual.Contains(expectedSubstring, comparison),
            $"Expected '{actual}' not to contain '{expectedSubstring}' ({comparison}).");

    public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> predicate) =>
        True(!collection.Any(x => predicate(x)), "Expected collection not to contain a matching element.");

    public static void All<T>(IEnumerable<T> collection, Action<T> action)
    {
        var index = 0;
        foreach (var item in collection)
        {
            try
            {
                action(item);
            }
            catch (Exception ex)
            {
                throw new XunitException($"Assert.All item[{index}] failed: {ex.Message}");
            }

            index++;
        }
    }

    public static void StartsWith(string expected, string? actual) =>
        True(actual is not null && actual.StartsWith(expected, StringComparison.Ordinal),
            $"Expected '{actual}' to start with '{expected}'.");

    public static void Empty<T>(IEnumerable<T> collection) =>
        True(!collection.Any(), "Expected empty collection.");

    public static void NotEmpty<T>(IEnumerable<T> collection) =>
        True(collection.Any(), "Expected non-empty collection.");

    public static void Single<T>(IEnumerable<T> collection) =>
        Equal(1, collection.Count());

    public static void Same(object? expected, object? actual) =>
        True(ReferenceEquals(expected, actual), "Expected same instance.");

    public static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new XunitException($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
        }

        throw new XunitException($"Expected {typeof(T).Name} but no exception was thrown.");
    }

    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new XunitException($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
        }

        throw new XunitException($"Expected {typeof(T).Name} but no exception was thrown.");
    }
}

public sealed class XunitException : Exception
{
    public XunitException(string message) : base(message) { }
}

/// <summary>
/// Simple discovery + runner used by FrameForge.Tests Program entry point.
/// </summary>
public static class MiniTestHost
{
    public static async Task<int> RunAsync(Assembly assembly, TextWriter output)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new StringBuilder();

        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var fact = method.GetCustomAttribute<FactAttribute>();
                var theory = method.GetCustomAttribute<TheoryAttribute>();
                if (fact is null && theory is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(fact?.Skip) || !string.IsNullOrEmpty(theory?.Skip))
                {
                    skipped++;
                    output.WriteLine($"  SKIP  {type.Name}.{method.Name}");
                    continue;
                }

                var cases = new List<object?[]>();
                if (theory is not null)
                {
                    var inlines = method.GetCustomAttributes<InlineDataAttribute>().ToList();
                    if (inlines.Count == 0)
                    {
                        cases.Add(Array.Empty<object?>());
                    }
                    else
                    {
                        cases.AddRange(inlines.Select(i => i.Data));
                    }
                }
                else
                {
                    cases.Add(Array.Empty<object?>());
                }

                foreach (var args in cases)
                {
                    var name = args.Length == 0
                        ? $"{type.Name}.{method.Name}"
                        : $"{type.Name}.{method.Name}({string.Join(", ", args.Select(a => a ?? "null"))})";

                    try
                    {
                        var instance = Activator.CreateInstance(type)
                            ?? throw new InvalidOperationException($"Could not create {type.Name}");
                        var result = method.Invoke(instance, args);
                        if (result is Task task)
                        {
                            await task.ConfigureAwait(false);
                        }

                        passed++;
                        output.WriteLine($"  PASS  {name}");
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException is not null)
                    {
                        failed++;
                        output.WriteLine($"  FAIL  {name}");
                        failures.AppendLine(name);
                        failures.AppendLine(tie.InnerException.ToString());
                        failures.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        output.WriteLine($"  FAIL  {name}");
                        failures.AppendLine(name);
                        failures.AppendLine(ex.ToString());
                        failures.AppendLine();
                    }
                }
            }
        }

        output.WriteLine();
        output.WriteLine($"Total: {passed + failed + skipped}  Passed: {passed}  Failed: {failed}  Skipped: {skipped}");
        if (failed > 0)
        {
            output.WriteLine();
            output.WriteLine("Failures:");
            output.WriteLine(failures.ToString());
        }

        return failed == 0 ? 0 : 1;
    }
}
