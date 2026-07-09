namespace PrometheusExporter.Instrumentation.SwitchBot;

using PrometheusExporter.Abstractions;
using PrometheusExporter.Infrastructure.Linux.BlueZ;

internal sealed class SwitchBotInstrumentation : IAsyncDisposable
{
    private readonly Lock sync = new();

    private readonly Device[] devices;

    private readonly BleScanSession session;

    public SwitchBotInstrumentation(
        SwitchBotOptions options,
        IMetricManager manager)
    {
        var rssiMetric = manager.CreateGauge("sensor_rssi");
        var temperatureMetric = manager.CreateGauge("sensor_temperature");
        var humidityMetric = manager.CreateGauge("sensor_humidity");
        var co2Metric = manager.CreateGauge("sensor_co2");
        var lightLevelMetric = manager.CreateGauge("sensor_light_level");
        var illuminanceMetric = manager.CreateGauge("sensor_illuminance");
        var powerMetric = manager.CreateGauge("sensor_power");

        var list = new List<Device>();
        foreach (var entry in options.Device)
        {
            var tags = MakeTags(entry.Address, entry.Name);
            list.Add(entry.Type switch
            {
                DeviceType.Meter => new MeterDevice(
                    entry.Address,
                    rssiMetric.Create(tags),
                    temperatureMetric.Create(tags),
                    humidityMetric.Create(tags),
                    co2Metric.Create(tags)),
                DeviceType.MeterPro => new MeterProDevice(
                    entry.Address,
                    rssiMetric.Create(tags),
                    temperatureMetric.Create(tags),
                    humidityMetric.Create(tags)),
                DeviceType.MeterProCO2 => new MeterProCo2Device(
                    entry.Address,
                    rssiMetric.Create(tags),
                    temperatureMetric.Create(tags),
                    humidityMetric.Create(tags),
                    co2Metric.Create(tags)),
                DeviceType.Hub3 => new Hub3Device(
                    entry.Address,
                    rssiMetric.Create(tags),
                    temperatureMetric.Create(tags),
                    humidityMetric.Create(tags),
                    lightLevelMetric.Create(tags),
                    illuminanceMetric.Create(tags)),
                DeviceType.PlugMini => new PlugMiniDevice(
                    entry.Address,
                    rssiMetric.Create(tags),
                    powerMetric.Create(tags)),
                _ => throw new InvalidOperationException($"Unsupported SwitchBot device type: {entry.Type}")
            });
        }
        devices = list.ToArray();

#pragma warning disable CA2012
        session = BleScanSession.CreateAsync().GetAwaiter().GetResult();
#pragma warning restore CA2012
        session.DeviceEvent += OnDeviceEvent;
        _ = session.StartAsync();
    }

    public ValueTask DisposeAsync()
    {
        return session.DisposeAsync();
    }

    //--------------------------------------------------------------------------------
    // Event
    //--------------------------------------------------------------------------------

    private void OnDeviceEvent(BleScanEvent args)
    {
        if (args.Type == BleScanEventType.Lost)
        {
            lock (sync)
            {
                foreach (var device in devices)
                {
                    if (device.Path == args.DevicePath)
                    {
                        device.Clear();
                        device.Path = null;
                        break;
                    }
                }
            }
        }
        else
        {
            lock (sync)
            {
                var device = devices.FirstOrDefault(x => x.Path == args.DevicePath);
                if ((device is null) && (args.Address is not null))
                {
                    device = devices.FirstOrDefault(x => x.Address == args.Address);
                    device?.Path = args.DevicePath;
                }

                if (device is null)
                {
                    return;
                }

                if (args.Rssi.HasValue)
                {
                    device.Rssi.Value = args.Rssi.Value;
                }

                TryGetSwitchBotData(args, out var serviceData, out var manufacturerData);

                if (device is Hub3Device hub3)
                {
                    if (SwitchBotAdvertisementParser.TryDecodeHub3(manufacturerData, out var temperature, out var humidity, out var lightLevel, out var illuminance))
                    {
                        hub3.Temperature.Value = temperature;
                        hub3.Humidity.Value = humidity;
                        hub3.LightLevel.Value = lightLevel;
                        hub3.Illuminance.Value = illuminance;
                    }
                }
                else if (device is MeterProCo2Device meterProCo2)
                {
                    if (SwitchBotAdvertisementParser.TryDecodeMeterProCo2(serviceData, manufacturerData, out var temperature, out var humidity, out var co2))
                    {
                        meterProCo2.Temperature.Value = temperature;
                        meterProCo2.Humidity.Value = humidity;

                        if (!double.IsNaN(co2))
                        {
                            meterProCo2.Co2.Value = co2;
                        }
                    }
                }
                else if (device is TemperatureHumidityDevice meter)
                {
                    if (SwitchBotAdvertisementParser.TryDecodeMeterTemperatureHumidity(serviceData, manufacturerData, out var temperature, out var humidity))
                    {
                        meter.Temperature.Value = temperature;
                        meter.Humidity.Value = humidity;
                    }
                }
                else if (device is PlugMiniDevice plug)
                {
                    var buffer = manufacturerData.Length > 0 ? manufacturerData : serviceData;
                    if (buffer.Length >= 12)
                    {
                        plug.Power.Value = (double)(((buffer[10] & 0b00111111) << 8) + (buffer[11] & 0b01111111)) / 10;
                    }
                }
            }
        }
    }

    //--------------------------------------------------------------------------------
    // Helper
    //--------------------------------------------------------------------------------

    private static KeyValuePair<string, object?>[] MakeTags(string address, string name) =>
        [new("model", "switchbot"), new("address", address), new("name", name)];

    private static void TryGetSwitchBotData(BleScanEvent args, out byte[] serviceData, out byte[] manufacturerData)
    {
        serviceData = [];
        manufacturerData = [];

        if (args.ManufacturerData?.TryGetValue(0x0969, out var md) == true)
        {
            manufacturerData = md;
        }

        if (args.ServiceData?.TryGetValue("0000fd3d-0000-1000-8000-00805f9b34fb", out var sd) == true)
        {
            serviceData = sd;
            return;
        }

        if (args.ServiceData?.TryGetValue("fd3d", out sd) == true)
        {
            serviceData = sd;
        }
    }

    //--------------------------------------------------------------------------------
    // Device
    //--------------------------------------------------------------------------------

    private abstract class Device
    {
        public string? Path { get; set; }

        public string Address { get; }

        public IMetricSeries Rssi { get; }

        protected Device(string address, IMetricSeries rssi)
        {
            Address = address;
            Rssi = rssi;
            Rssi.Value = double.NaN;
        }

        public abstract void Clear();
    }

    private abstract class TemperatureHumidityDevice : Device
    {
        public IMetricSeries Temperature { get; }

        public IMetricSeries Humidity { get; }

        protected TemperatureHumidityDevice(string address, IMetricSeries rssi, IMetricSeries temperature, IMetricSeries humidity)
            : base(address, rssi)
        {
            Temperature = temperature;
            Humidity = humidity;
            Temperature.Value = double.NaN;
            Humidity.Value = double.NaN;
        }

        public override void Clear()
        {
            Rssi.Value = double.NaN;
            Temperature.Value = double.NaN;
            Humidity.Value = double.NaN;
        }
    }

    private abstract class Co2Device : TemperatureHumidityDevice
    {
        public IMetricSeries Co2 { get; }

        protected Co2Device(string address, IMetricSeries rssi, IMetricSeries temperature, IMetricSeries humidity, IMetricSeries co2)
            : base(address, rssi, temperature, humidity)
        {
            Co2 = co2;
            Co2.Value = double.NaN;
        }

        public override void Clear()
        {
            base.Clear();
            Co2.Value = double.NaN;
        }
    }

    private sealed class MeterDevice : Co2Device
    {
        public MeterDevice(string address, IMetricSeries rssi, IMetricSeries temperature, IMetricSeries humidity, IMetricSeries co2)
            : base(address, rssi, temperature, humidity, co2)
        {
        }
    }

    private sealed class MeterProDevice : TemperatureHumidityDevice
    {
        public MeterProDevice(string address, IMetricSeries rssi, IMetricSeries temperature, IMetricSeries humidity)
            : base(address, rssi, temperature, humidity)
        {
        }
    }

    private sealed class MeterProCo2Device : Co2Device
    {
        public MeterProCo2Device(string address, IMetricSeries rssi, IMetricSeries temperature, IMetricSeries humidity, IMetricSeries co2)
            : base(address, rssi, temperature, humidity, co2)
        {
        }
    }

    private sealed class Hub3Device : TemperatureHumidityDevice
    {
        public IMetricSeries LightLevel { get; }

        public IMetricSeries Illuminance { get; }

        public Hub3Device(
            string address,
            IMetricSeries rssi,
            IMetricSeries temperature,
            IMetricSeries humidity,
            IMetricSeries lightLevel,
            IMetricSeries illuminance)
            : base(address, rssi, temperature, humidity)
        {
            LightLevel = lightLevel;
            Illuminance = illuminance;
            LightLevel.Value = double.NaN;
            Illuminance.Value = double.NaN;
        }

        public override void Clear()
        {
            base.Clear();
            LightLevel.Value = double.NaN;
            Illuminance.Value = double.NaN;
        }
    }

    private sealed class PlugMiniDevice : Device
    {
        public IMetricSeries Power { get; }

        public PlugMiniDevice(string address, IMetricSeries rssi, IMetricSeries power)
            : base(address, rssi)
        {
            Power = power;
            Power.Value = double.NaN;
        }

        public override void Clear()
        {
            Rssi.Value = double.NaN;
            Power.Value = double.NaN;
        }
    }
}
