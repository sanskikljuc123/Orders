using Xunit.Abstractions;
using Xunit.Sdk;

// Linked into the unit, contract and integration test projects (see their .csproj). Runs test
// collections and test cases in a random order, so a test that only passes after another one
// (shared state, leftover data) fails instead of hiding. The seed is printed; replay a failing
// order with TEST_ORDER_SEED=<seed>.

namespace TestSupport;

internal static class TestOrderSeed
{
    private static readonly Lazy<int> Seed = new(() =>
        int.TryParse(Environment.GetEnvironmentVariable("TEST_ORDER_SEED"), out var s) ? s : Random.Shared.Next());

    public static int Value => Seed.Value;
}

public sealed class RandomTestCaseOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase
    {
        var random = new Random(TestOrderSeed.Value);
        return testCases.OrderBy(t => t.UniqueID, StringComparer.Ordinal).OrderBy(_ => random.Next()).ToList();
    }
}

public sealed class RandomTestCollectionOrderer : ITestCollectionOrderer
{
    private readonly IMessageSink _diagnostics;

    public RandomTestCollectionOrderer(IMessageSink diagnostics)
    {
        _diagnostics = diagnostics;
        _diagnostics.OnMessage(new DiagnosticMessage($"Random test order seed: {TestOrderSeed.Value} (replay with TEST_ORDER_SEED={TestOrderSeed.Value})"));
    }

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> collections)
    {
        var random = new Random(TestOrderSeed.Value);
        return collections.OrderBy(c => c.UniqueID).OrderBy(_ => random.Next()).ToList();
    }
}
