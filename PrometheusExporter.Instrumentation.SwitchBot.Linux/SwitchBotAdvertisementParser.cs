namespace PrometheusExporter.Instrumentation.SwitchBot;

internal static class SwitchBotAdvertisementParser
{
    internal static bool TryDecodeTemperatureHumidity(ReadOnlySpan<byte> tempData, out double temperature, out double humidity)
    {
        temperature = double.NaN;
        humidity = double.NaN;

        if (tempData.Length < 3)
        {
            return false;
        }

        var decimalPart = (double)(tempData[0] & 0x0f) / 10;
        var integerPart = tempData[1] & 0x7f;
        var sign = (tempData[1] & 0x80) > 0 ? 1 : -1;

        temperature = (integerPart + decimalPart) * sign;
        humidity = tempData[2] & 0x7f;

        if ((temperature == 0) && (humidity == 0))
        {
            temperature = double.NaN;
            humidity = double.NaN;
            return false;
        }

        return true;
    }

    internal static bool TryDecodeMeterTemperatureHumidity(
        ReadOnlySpan<byte> serviceData,
        ReadOnlySpan<byte> manufacturerData,
        out double temperature,
        out double humidity)
    {
        if (manufacturerData.Length >= 11)
        {
            return TryDecodeTemperatureHumidity(manufacturerData[8..11], out temperature, out humidity);
        }

        if (serviceData.Length >= 6)
        {
            return TryDecodeTemperatureHumidity(serviceData[3..6], out temperature, out humidity);
        }

        temperature = double.NaN;
        humidity = double.NaN;
        return false;
    }

    internal static bool TryDecodeMeterProCo2(
        ReadOnlySpan<byte> serviceData,
        ReadOnlySpan<byte> manufacturerData,
        out double temperature,
        out double humidity,
        out double co2)
    {
        co2 = double.NaN;

        if (!TryDecodeMeterTemperatureHumidity(serviceData, manufacturerData, out temperature, out humidity))
        {
            return false;
        }

        if (manufacturerData.Length < 15)
        {
            return true;
        }

        var value = (manufacturerData[13] << 8) | manufacturerData[14];
        if (value <= 9999)
        {
            co2 = value;
        }

        return true;
    }
}
