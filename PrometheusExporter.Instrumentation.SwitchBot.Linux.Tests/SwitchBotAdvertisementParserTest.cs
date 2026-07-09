namespace PrometheusExporter.Instrumentation.SwitchBot.Tests;

using Xunit;

public sealed class SwitchBotAdvertisementParserTest
{
    [Theory]
    [InlineData(0x05, 0x97, 45, 23.5)]
    [InlineData(0x08, 0x83, 12, 3.8)]
    [InlineData(0x04, 0x02, 67, -2.4)]
    public void TryDecodeTemperatureHumidityDecodesTemperatureAndHumidity(byte decimalAndFlags, byte integerAndSign, byte humidityByte, double expectedTemperature)
    {
        var actual = SwitchBotAdvertisementParser.TryDecodeTemperatureHumidity(
            [decimalAndFlags, integerAndSign, humidityByte],
            out var temperature,
            out var humidity);

        Assert.True(actual);
        Assert.Equal(expectedTemperature, temperature, 1);
        Assert.Equal((double)humidityByte, humidity);
    }

    [Fact]
    public void TryDecodeTemperatureHumidityRejectsZeroTemperatureAndZeroHumidity()
    {
        var actual = SwitchBotAdvertisementParser.TryDecodeTemperatureHumidity(
            [0x00, 0x80, 0x00],
            out var temperature,
            out var humidity);

        Assert.False(actual);
        Assert.True(double.IsNaN(temperature));
        Assert.True(double.IsNaN(humidity));
    }

    [Fact]
    public void TryDecodeMeterTemperatureHumidityUsesManufacturerData()
    {
        byte[] manufacturerData = [0, 0, 0, 0, 0, 0, 0, 0, 0x07, 0x95, 55];

        var actual = SwitchBotAdvertisementParser.TryDecodeMeterTemperatureHumidity(
            [],
            manufacturerData,
            out var temperature,
            out var humidity);

        Assert.True(actual);
        Assert.Equal(21.7, temperature, 1);
        Assert.Equal(55d, humidity);
    }

    [Fact]
    public void TryDecodeMeterTemperatureHumidityUsesServiceDataWhenManufacturerDataIsMissing()
    {
        byte[] serviceData = [0, 0, 0, 0x06, 0x92, 48];

        var actual = SwitchBotAdvertisementParser.TryDecodeMeterTemperatureHumidity(
            serviceData,
            [],
            out var temperature,
            out var humidity);

        Assert.True(actual);
        Assert.Equal(18.6, temperature, 1);
        Assert.Equal(48d, humidity);
    }

    [Fact]
    public void TryDecodeMeterProCo2DecodesCo2BigEndian()
    {
        byte[] manufacturerData = [0, 0, 0, 0, 0, 0, 0, 0, 0x05, 0x96, 40, 0, 0, 0x01, 0xc6];

        var actual = SwitchBotAdvertisementParser.TryDecodeMeterProCo2(
            [],
            manufacturerData,
            out var temperature,
            out var humidity,
            out var co2);

        Assert.True(actual);
        Assert.Equal(22.5, temperature, 1);
        Assert.Equal(40d, humidity);
        Assert.Equal(454d, co2);
    }

    [Fact]
    public void TryDecodeMeterProCo2DropsCo2Over9999Ppm()
    {
        byte[] manufacturerData = [0, 0, 0, 0, 0, 0, 0, 0, 0x05, 0x96, 40, 0, 0, 0x27, 0x10];

        var actual = SwitchBotAdvertisementParser.TryDecodeMeterProCo2(
            [],
            manufacturerData,
            out _,
            out _,
            out var co2);

        Assert.True(actual);
        Assert.True(double.IsNaN(co2));
    }

    [Fact]
    public void TryDecodeMeterTemperatureHumidityDoesNotReadHub3AsMeter()
    {
        var manufacturerData = CreateHub3ManufacturerData();

        var actual = SwitchBotAdvertisementParser.TryDecodeMeterTemperatureHumidity(
            [],
            manufacturerData,
            out var temperature,
            out var humidity);

        Assert.False(actual);
        Assert.True(double.IsNaN(temperature));
        Assert.True(double.IsNaN(humidity));
    }

    [Fact]
    public void TryDecodeHub3DecodesTemperatureHumidityLightLevelAndIlluminance()
    {
        var manufacturerData = CreateHub3ManufacturerData();

        var actual = SwitchBotAdvertisementParser.TryDecodeHub3(
            manufacturerData,
            out var temperature,
            out var humidity,
            out var lightLevel,
            out var illuminance);

        Assert.True(actual);
        Assert.Equal(24.6, temperature, 1);
        Assert.Equal(63d, humidity);
        Assert.Equal(4d, lightLevel);
        Assert.Equal(205d, illuminance);
    }

    private static byte[] CreateHub3ManufacturerData() => [
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
        0x04,
        0x06, 0x98, 63,
        0];
}
