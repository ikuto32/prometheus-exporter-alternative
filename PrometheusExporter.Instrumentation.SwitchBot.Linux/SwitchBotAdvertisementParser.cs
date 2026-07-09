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

    internal static bool TryDecodeHub3(
        ReadOnlySpan<byte> manufacturerData,
        out double temperature,
        out double humidity,
        out double lightLevel,
        out double illuminance)
    {
        temperature = double.NaN;
        humidity = double.NaN;
        lightLevel = double.NaN;
        illuminance = double.NaN;

        if (manufacturerData.Length < 17)
        {
            return false;
        }

        var deviceData = manufacturerData[6..];
        lightLevel = deviceData[6] & 0x0f;
        illuminance = ConvertHub3Illuminance(lightLevel);

        return TryDecodeTemperatureHumidity(deviceData[7..10], out temperature, out humidity);
    }

    private static double ConvertHub3Illuminance(double lightLevel) => lightLevel switch
    {
        1 => 0,
        2 => 50,
        3 => 90,
        4 => 205,
        5 => 317,
        6 => 510,
        7 => 610,
        8 => 707,
        9 => 801,
        10 => 1023,
        _ => double.NaN
    };

    private static bool IsHub3ManufacturerData(ReadOnlySpan<byte> manufacturerData)
    {
        if (manufacturerData.Length < 17)
        {
            return false;
        }

        var deviceData = manufacturerData[6..];
        var lightLevel = deviceData[6] & 0x0f;
        return !double.IsNaN(ConvertHub3Illuminance(lightLevel))
            && TryDecodeTemperatureHumidity(deviceData[7..10], out _, out _);
    }

    internal static bool TryDecodeMeterTemperatureHumidity(
        ReadOnlySpan<byte> serviceData,
        ReadOnlySpan<byte> manufacturerData,
        out double temperature,
        out double humidity)
    {
        if (IsHub3ManufacturerData(manufacturerData))
        {
            temperature = double.NaN;
            humidity = double.NaN;
            return false;
        }

        if (manufacturerData.Length >= 11)
        {
            return TryDecodeTemperatureHumidity(manufacturerData[8..11], out temperature, out humidity);
        }

        if (IsMeterServiceDataWithoutTemperatureHumidity(serviceData))
        {
            temperature = double.NaN;
            humidity = double.NaN;
            return false;
        }

        if (serviceData.Length >= 6)
        {
            return TryDecodeTemperatureHumidity(serviceData[3..6], out temperature, out humidity);
        }

        temperature = double.NaN;
        humidity = double.NaN;
        return false;
    }

    private static bool IsMeterServiceDataWithoutTemperatureHumidity(ReadOnlySpan<byte> serviceData)
        => serviceData.Length == 7
            && serviceData[3] == 0x00
            && serviceData[4] == 0x10
            && serviceData[5] == 0xb9
            && (serviceData[6] == 0x40 || serviceData[6] == 0x41);

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
