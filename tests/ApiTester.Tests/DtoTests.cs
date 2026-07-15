using ApiTester.Core;

namespace ApiTester.Tests;

public class DtoTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(299, true)]
    [InlineData(301, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    public void IsSuccess_true_only_for_2xx_without_error(int status, bool expected)
    {
        var resp = new ApiResponse { StatusCode = status };
        Assert.Equal(expected, resp.IsSuccess);
    }

    [Fact]
    public void IsSuccess_false_when_error_present()
    {
        var resp = new ApiResponse { StatusCode = 200, Error = new ApiError(ApiErrorKind.Network, "boom") };
        Assert.False(resp.IsSuccess);
    }
}
