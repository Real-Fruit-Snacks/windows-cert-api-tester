using ApiTester.Core;

namespace ApiTester.Tests;

public class SelfTestRunnerTests
{
    [Fact]
    public async Task Self_test_passes_end_to_end()
    {
        var result = await new SelfTestRunner().RunAsync();
        Assert.True(result.Passed, result.Detail);
        Assert.Contains("succeeded", result.Detail);
    }
}
