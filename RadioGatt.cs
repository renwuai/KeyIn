using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace KeyLend;

// HID 报告协议。报告编号由 0x2908 描述符给出，通知的值只含报告内容。
internal sealed class RadioGatt : IDisposable
{
    internal GattLocalCharacteristic Keyboard { get; private set; } = null!;
    internal GattLocalCharacteristic Consumer { get; private set; } = null!;
    internal GattServiceProvider Hid { get; private set; } = null!;
    private GattServiceProvider? battery;
    private GattLocalCharacteristic? control;
    private GattLocalCharacteristic? leds;
    private GattLocalCharacteristic? batteryLevel;
    private readonly Action<string, bool> suspend;
    private readonly Action changed;
    private volatile string? advertisingError;
    private long nextAdvertisingAttempt;
    private int advertisingRetryMs = 10000;
    private readonly object reportLock = new();
    private string? reportOwner;
    private byte[] keys = new byte[8];
    private byte[] media = new byte[1];
    private byte ledMask;

    private static readonly byte[] ReportMap =
    [
        0x05,0x01, 0x09,0x06, 0xA1,0x01, 0x85,0x01,         // 键盘，报告 1
        0x05,0x07, 0x19,0xE0, 0x29,0xE7, 0x15,0x00, 0x25,0x01,
        0x75,0x01, 0x95,0x08, 0x81,0x02,                     // 八个修饰键位
        0x75,0x08, 0x95,0x01, 0x81,0x01,                     // 保留字节
        0x19,0x00, 0x29,0xA4, 0x15,0x00, 0x26,0xA4,0x00,
        0x75,0x08, 0x95,0x06, 0x81,0x00,                     // 六个并发普通键
        0x05,0x08, 0x19,0x01, 0x29,0x05, 0x15,0x00, 0x25,0x01,
        0x75,0x01, 0x95,0x05, 0x91,0x02, 0x75,0x03, 0x95,0x01,
        0x91,0x01, 0xC0,                                     // 五个键盘指示灯和三个填充位
        0x05,0x0C, 0x09,0x01, 0xA1,0x01, 0x85,0x02,         // 消费者控制，报告 2
        0x09,0xB8, 0x15,0x00, 0x25,0x01, 0x75,0x01, 0x95,0x01,
        0x81,0x02, 0x75,0x07, 0x95,0x01, 0x81,0x01, 0xC0    // Eject 位及七个填充位
    ];

    private RadioGatt(Action changed, Action<string, bool> suspend)
    {
        this.changed = changed;
        this.suspend = suspend;
    }

    internal static async Task<RadioGatt> CreateAsync(Action changed, Action<string, bool> suspend)
    {
        var profile = new RadioGatt(changed, suspend);
        try
        {
            profile.Hid = await ServiceAsync(0x1812);
            var hid = profile.Hid.Service;
            await CharacteristicAsync(hid, 0x2A4A, GattCharacteristicProperties.Read, [0x11, 0x01, 0x00, 0x02]);
            await CharacteristicAsync(hid, 0x2A4B, GattCharacteristicProperties.Read, ReportMap);
            profile.control = await CharacteristicAsync(hid, 0x2A4C, GattCharacteristicProperties.WriteWithoutResponse);
            profile.control.WriteRequested += profile.ControlWritten;
            profile.Keyboard = await profile.InputAsync(1);
            profile.leds = await CharacteristicAsync(hid, 0x2A4D, GattCharacteristicProperties.Read
                | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse);
            await AddReferenceAsync(profile.leds, 1, 2);
            profile.leds.ReadRequested += profile.LedsRead;
            profile.leds.WriteRequested += profile.LedsWritten;
            profile.Consumer = await profile.InputAsync(2);

            profile.Hid.AdvertisementStatusChanged += profile.AdvertisementChanged;
            profile.StartKeyboardAdvertising();
            // 由键盘独占可连接广播。电池服务只发布到现有 GATT 连接，不申请无线广播。
            try
            {
                profile.battery = await ServiceAsync(0x180F);
                profile.batteryLevel = await CharacteristicAsync(profile.battery.Service, 0x2A19, GattCharacteristicProperties.Read);
                profile.batteryLevel.ReadRequested += BatteryRead;
                profile.battery.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = false, IsDiscoverable = false
                });
            }
            catch (Exception) { profile.StopBattery(); }
            return profile;
        }
        catch
        {
            profile.Dispose();
            throw;
        }
    }

    private async Task<GattLocalCharacteristic> InputAsync(byte id)
    {
        var input = await CharacteristicAsync(Hid.Service, 0x2A4D,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify);
        await AddReferenceAsync(input, id, 1);
        input.ReadRequested += ReportRead;
        input.SubscribedClientsChanged += SubscribersChanged;
        return input;
    }

    private static async Task AddReferenceAsync(GattLocalCharacteristic input, byte id, byte type)
    {
        var descriptor = await input.CreateDescriptorAsync(Uuid(0x2908), new GattLocalDescriptorParameters
        {
            StaticValue = Buffer([id, type]), ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
        });
        Check(descriptor.Error, new("创建 HID 报告引用失败", "Couldn't create the HID report reference"));
    }

    private void SubscribersChanged(GattLocalCharacteristic sender, object args) => changed();
    private void AdvertisementChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
            advertisingError = $"{args.Error} ({(int)args.Error})";
        changed();
    }

    private void StartKeyboardAdvertising()
    {
        nextAdvertisingAttempt = Environment.TickCount64 + advertisingRetryMs;
        try
        {
            Hid.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsDiscoverable = true, IsConnectable = true
            });
        }
        catch (Exception ex) { advertisingError = $"{ex.Message} (0x{ex.HResult:X8})"; }
    }

    // 只有没有可用输入连接时才检查广播。保留服务句柄，让 Windows 有时间完成异步广播请求。
    internal RadioText? CheckIdleAdvertising(bool canRetry)
    {
        var state = Hid.AdvertisementStatus;
        if (state is GattServiceProviderAdvertisementStatus.Started
            or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
        {
            advertisingError = null;
            advertisingRetryMs = 10000;
            return null;
        }
        if (canRetry && Environment.TickCount64 >= nextAdvertisingAttempt)
        {
            advertisingRetryMs = Math.Min(30000, advertisingRetryMs * 2);
            Stop(Hid);
            StartKeyboardAdvertising();
        }
        string? detail = advertisingError;
        return detail != null ? new($"Windows 蓝牙广播 {state} · {detail}。", $"Windows Bluetooth advertising: {state} · {detail}")
            : state == GattServiceProviderAdvertisementStatus.Created
                ? new("正在启动蓝牙键盘广播…", "Starting Bluetooth keyboard advertising…")
                : new($"Windows 蓝牙广播 {state}，正在等待重试。", $"Windows Bluetooth advertising: {state}. Waiting to retry.");
    }

    private static Guid Uuid(ushort id) => new($"{id:X8}-0000-1000-8000-00805f9b34fb");

    private static async Task<GattServiceProvider> ServiceAsync(ushort id)
    {
        var result = await GattServiceProvider.CreateAsync(Uuid(id));
        Check(result.Error, new($"创建蓝牙服务 {id:X4} 失败", $"Couldn't create Bluetooth service {id:X4}"));
        return result.ServiceProvider;
    }

    private static async Task<GattLocalCharacteristic> CharacteristicAsync(GattLocalService service,
        ushort id, GattCharacteristicProperties properties, byte[]? value = null)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
        };
        if (value != null) parameters.StaticValue = Buffer(value);
        var result = await service.CreateCharacteristicAsync(Uuid(id), parameters);
        Check(result.Error, new($"创建蓝牙特征 {id:X4} 失败", $"Couldn't create Bluetooth characteristic {id:X4}"));
        return result.Characteristic;
    }

    private static void Check(BluetoothError error, RadioText failure)
    {
        if (error != BluetoothError.Success)
            throw new RadioException(new($"{failure.Zh}（{error}）", $"{failure.En} ({error})"));
    }

    internal static IBuffer Buffer(byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    internal void SetReportTarget(string? id)
    {
        lock (reportLock)
        {
            reportOwner = id;
            keys = new byte[8];
            media = new byte[1];
            ledMask = 0;
        }
    }

    internal void Record(string owner, bool consumer, byte[] value)
    {
        lock (reportLock)
        {
            if (!string.Equals(reportOwner, owner, StringComparison.OrdinalIgnoreCase)) return;
            if (consumer) media = value;
            else keys = value;
        }
    }

    private void ReportRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var keyboard = sender == Keyboard;
        Reply(args, () =>
        {
            lock (reportLock)
            {
                return string.Equals(reportOwner, args.Session.DeviceId.Id, StringComparison.OrdinalIgnoreCase)
                    ? (keyboard ? keys : media) : new byte[keyboard ? 8 : 1];
            }
        });
    }

    private static void BatteryRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args) =>
        Reply(args, () => [(byte)Math.Clamp((int)Math.Round(SystemInformation.PowerStatus.BatteryLifePercent * 100), 0, 100)]);

    private static async void Reply(GattReadRequestedEventArgs args, Func<byte[]> read)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request == null) return;
            var value = read();
            if (request.Offset > value.Length) request.RespondWithProtocolError(0x07);
            else request.RespondWithValue(Buffer(value[(int)request.Offset..]));
        }
        catch (Exception) { /* 远端可能在异步读取期间关闭 ATT 请求。 */ }
        finally { deferral.Complete(); }
    }

    private void LedsRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args) =>
        Reply(args, () =>
        {
            lock (reportLock)
                return [string.Equals(reportOwner, args.Session.DeviceId.Id, StringComparison.OrdinalIgnoreCase) ? ledMask : (byte)0];
        });

    private void LedsWritten(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args) =>
        AcceptByte(args, (id, value) =>
        {
            // 所有设备均能发现并订阅服务，只有选定主机的输出可改变其指示灯报告。
            lock (reportLock)
            {
                if (string.Equals(reportOwner, id, StringComparison.OrdinalIgnoreCase)) ledMask = (byte)(value & 0x1F);
            }
        });

    private void ControlWritten(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args) =>
        AcceptByte(args, (id, value) => { if (value <= 1) suspend(id, value == 0); });

    private static async void AcceptByte(GattWriteRequestedEventArgs args, Action<string, byte> accept)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request == null) return;
            if (request.Offset != 0 || request.Value.Length != 1)
            {
                if (request.Option == GattWriteOption.WriteWithResponse)
                    request.RespondWithProtocolError(request.Offset != 0 ? (byte)0x07 : (byte)0x0D);
                return;
            }
            using var reader = DataReader.FromBuffer(request.Value);
            accept(args.Session.DeviceId.Id, reader.ReadByte());
            if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();
        }
        catch (Exception) { /* 断连可能使挂起命令失效。 */ }
        finally { deferral.Complete(); }
    }

    public void Dispose()
    {
        SetReportTarget(null);
        if (Keyboard != null) { Keyboard.ReadRequested -= ReportRead; Keyboard.SubscribedClientsChanged -= SubscribersChanged; }
        if (Consumer != null) { Consumer.ReadRequested -= ReportRead; Consumer.SubscribedClientsChanged -= SubscribersChanged; }
        if (control != null) control.WriteRequested -= ControlWritten;
        if (leds != null) { leds.ReadRequested -= LedsRead; leds.WriteRequested -= LedsWritten; }
        if (Hid != null) Hid.AdvertisementStatusChanged -= AdvertisementChanged;
        Stop(Hid);
        StopBattery();
    }

    private void StopBattery()
    {
        if (batteryLevel != null) batteryLevel.ReadRequested -= BatteryRead;
        batteryLevel = null;
        Stop(battery);
        battery = null;
    }

    private static void Stop(GattServiceProvider? provider)
    {
        try { provider?.StopAdvertising(); }
        catch (Exception) { /* 系统已移除适配器时，广播也已失效。 */ }
    }
}
