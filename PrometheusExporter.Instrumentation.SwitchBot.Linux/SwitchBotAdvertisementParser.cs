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
}
