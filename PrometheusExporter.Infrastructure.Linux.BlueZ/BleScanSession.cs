namespace PrometheusExporter.Infrastructure.Linux.BlueZ;

using System.Collections.Concurrent;

using Tmds.DBus;

public enum BleScanEventType
{
    Discover,
    Update,
    Lost
}

public sealed class BleScanEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public BleScanEventType Type { get; init; }

    public string DevicePath { get; init; } = string.Empty;

    public IReadOnlyCollection<string> Keys { get; init; } = [];

    public string? Address { get; init; }

    public string? Name { get; init; }

    public string? Alias { get; init; }

    public short? Rssi { get; init; }

    public IReadOnlyDictionary<ushort, byte[]>? ManufacturerData { get; init; }

    public IReadOnlyDictionary<string, byte[]>? ServiceData { get; init; }
}

public sealed class BleScanSession : IAsyncDisposable
{
#pragma warning disable CA1003
    public event Action<BleScanEvent>? DeviceEvent;
#pragma warning restore CA1003

    private readonly Connection connection;
    private readonly IObjectManager objectManager;
    private readonly IAdapter1 adapter;

    private IDisposable? addedSubscription;
    private IDisposable? removedSubscription;

    private readonly ConcurrentDictionary<ObjectPath, IDisposable> devicePropertySubscriptions = new();

    private volatile bool discovering;

    private BleScanSession(Connection connection, IObjectManager objectManager, IAdapter1 adapter)
    {
        this.connection = connection;
        this.objectManager = objectManager;
        this.adapter = adapter;
    }

    public static async ValueTask<BleScanSession> CreateAsync()
    {
        var con = new Connection(Address.System);
        await con.ConnectAsync().ConfigureAwait(false);

        var manager = con.CreateProxy<IObjectManager>("org.bluez", new ObjectPath("/"));
        var objects = await manager.GetManagedObjectsAsync().ConfigureAwait(false);
        var adapterPath = objects.Keys.FirstOrDefault(p => objects[p].ContainsKey("org.bluez.Adapter1"));
        if (adapterPath == default)
        {
            throw new InvalidOperationException("Bluetooth adapter (org.bluez.Adapter1) not found.");
        }

        var adapter = con.CreateProxy<IAdapter1>("org.bluez", adapterPath);

        return new BleScanSession(con, manager, adapter);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        connection.Dispose();
    }

    private void RaiseEvent(BleScanEvent e) => DeviceEvent?.Invoke(e);

    public async Task StartAsync()
    {
        if (discovering)
        {
            return;
        }

        // Device added subscription
        addedSubscription = await objectManager.WatchInterfacesAddedAsync(ev =>
        {
            if (!ev.Interfaces.TryGetValue("org.bluez.Device1", out var props))
            {
                return;
            }

            RaiseEvent(new BleScanEvent
            {
                Timestamp = DateTimeOffset.Now,
                Type = BleScanEventType.Discover,
                DevicePath = ev.ObjectPath.ToString(),
                Keys = props.Keys.ToArray(),
                Address = TryGetString(props, "Address"),
                Name = TryGetString(props, "Name"),
                Alias = TryGetString(props, "Alias"),
                Rssi = TryGetInt16(props, "RSSI"),
                ServiceData = TryGetServiceData(props)
            });

#pragma warning disable CA2012
            _ = SubscribeDevicePropertyAsync(ev.ObjectPath);
#pragma warning restore CA2012
        }).ConfigureAwait(false);
        // Device removed subscription
        removedSubscription = await objectManager.WatchInterfacesRemovedAsync(ev =>
        {
            if (devicePropertySubscriptions.TryRemove(ev.ObjectPath, out var subscription))
            {
                subscription.Dispose();
            }

            RaiseEvent(new BleScanEvent
            {
                Timestamp = DateTimeOffset.Now,
                Type = BleScanEventType.Lost,
                DevicePath = ev.ObjectPath.ToString(),
                Keys = ev.Interfaces.ToArray()
            });
        }).ConfigureAwait(false);

        var objects = await objectManager.GetManagedObjectsAsync().ConfigureAwait(false);
        foreach (var (key, value) in objects)
        {
            if (!value.TryGetValue("org.bluez.Device1", out var props))
            {
                continue;
            }

            RaiseEvent(new BleScanEvent
            {
                Timestamp = DateTimeOffset.Now,
                Type = BleScanEventType.Discover,
                DevicePath = key.ToString(),
                Keys = props.Keys.ToArray(),
                Address = TryGetString(props, "Address"),
                Name = TryGetString(props, "Name"),
                Alias = TryGetString(props, "Alias"),
                Rssi = TryGetInt16(props, "RSSI"),
                ServiceData = TryGetServiceData(props)
            });

#pragma warning disable CA2012
            _ = SubscribeDevicePropertyAsync(key);
#pragma warning restore CA2012
        }

        await adapter.StartDiscoveryAsync().ConfigureAwait(false);

        discovering = true;
    }

    private async ValueTask SubscribeDevicePropertyAsync(ObjectPath devicePath)
    {
        if (devicePropertySubscriptions.ContainsKey(devicePath))
        {
            return;
        }

        var properties = connection.CreateProxy<IProperties>("org.bluez", devicePath);
        var subscription = await properties.WatchPropertiesChangedAsync(ev =>
        {
            if (!String.Equals(ev.Interface, "org.bluez.Device1", StringComparison.Ordinal))
            {
                return;
            }

            if (ev.Changed.Count == 0)
            {
                return;
            }

            var props = ev.Changed;
            var md = TryGetManufacturerData(props);
            var serviceData = TryGetServiceData(props);
            RaiseEvent(new BleScanEvent
            {
                Timestamp = DateTimeOffset.Now,
                Type = BleScanEventType.Update,
                DevicePath = devicePath.ToString(),
                Keys = props.Keys.ToArray(),
                Address = TryGetString(props, "Address"),
                Name = TryGetString(props, "Name"),
                Alias = TryGetString(props, "Alias"),
                Rssi = TryGetInt16(props, "RSSI"),
                ManufacturerData = md,
                ServiceData = serviceData
            });
        }).ConfigureAwait(false);

        if (!devicePropertySubscriptions.TryAdd(devicePath, subscription))
        {
            subscription.Dispose();
        }
    }

    public async ValueTask StopAsync()
    {
        if (!discovering)
        {
            return;
        }

#pragma warning disable CA1031
        try
        {
            await adapter.StopDiscoveryAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
#pragma warning restore CA1031

        addedSubscription?.Dispose();
        addedSubscription = null;

        removedSubscription?.Dispose();
        removedSubscription = null;

        foreach (var (_, value) in devicePropertySubscriptions)
        {
            value.Dispose();
        }
        devicePropertySubscriptions.Clear();

        discovering = false;
    }

    private static string? TryGetString(IDictionary<string, object>? props, string key)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (props == null || !props.TryGetValue(key, out var value) || (value is null))
        {
            return null;
        }

        return value as string;
    }

    private static short? TryGetInt16(IDictionary<string, object>? props, string key)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (props == null || !props.TryGetValue(key, out var value) || (value is null))
        {
            return null;
        }

        return value switch
        {
            short s => s,
            int i => (short)i,
            long l => (short)l,
            _ => null
        };
    }

    private static Dictionary<string, byte[]>? TryGetServiceData(IDictionary<string, object> props)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (!props.TryGetValue("ServiceData", out var value) || (value is null))
        {
            return null;
        }

        if (value is IDictionary<string, byte[]> direct)
        {
            var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, bytes) in direct)
            {
                AddServiceData(res, key, bytes);
            }
            return res;
        }

        if (value is IDictionary<string, object> objectDictionary)
        {
            var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, obj) in objectDictionary)
            {
                if (obj is byte[] bytes)
                {
                    AddServiceData(res, key, bytes);
                }
                else if (obj is IEnumerable<byte> eb)
                {
                    AddServiceData(res, key, eb.ToArray());
                }
            }
            return res;
        }

        return null;
    }

    private static void AddServiceData(IDictionary<string, byte[]> serviceData, string uuid, byte[] bytes)
    {
        serviceData[uuid] = bytes;

        const string bluetoothBaseSuffix = "-0000-1000-8000-00805f9b34fb";
        if (uuid.Length == 36
            && uuid.StartsWith("0000", StringComparison.OrdinalIgnoreCase)
            && uuid.EndsWith(bluetoothBaseSuffix, StringComparison.OrdinalIgnoreCase))
        {
            serviceData[uuid[4..8]] = bytes;
        }
        else if (uuid.Length == 4)
        {
            serviceData[$"0000{uuid}-0000-1000-8000-00805f9b34fb"] = bytes;
        }
    }

    private static Dictionary<ushort, byte[]>? TryGetManufacturerData(IDictionary<string, object> props)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (!props.TryGetValue("ManufacturerData", out var value) || (value is null))
        {
            return null;
        }

        if (value is IDictionary<ushort, byte[]> direct)
        {
#pragma warning disable IDE0028
            return new(direct);
#pragma warning restore IDE0028
        }

        if (value is IDictionary<ushort, object> objectDictionary)
        {
            var res = new Dictionary<ushort, byte[]>();
            foreach (var (key, obj) in objectDictionary)
            {
                if (obj is byte[] bytes)
                {
                    res[key] = bytes;
                }
                else if (obj is IEnumerable<byte> eb)
                {
                    res[key] = eb.ToArray();
                }
            }
            return res;
        }

        return null;
    }
}
