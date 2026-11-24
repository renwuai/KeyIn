using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace KeyLend;

// 同时保存两种文案，界面切换语言时可以直接重绘当前蓝牙状态。
internal sealed record RadioText(string Zh, string En)
{
    internal string Display => UiLanguage.T(Zh, En);
}

internal sealed class RadioException(RadioText detail) : InvalidOperationException(detail.Display)
{
    internal RadioText Detail { get; } = detail;
}

internal sealed record PhoneChoice(string Id, string Name, string Detail);

internal sealed class RadioLink : IDisposable
{
    // 清除绑定时保留当时的订阅客户端，恢复操作不能凭设备 ID 在重连后重新寻找目标。
    private sealed record ClearedKeyboard(string Id, RadioGatt Profile, BluetoothLEDevice Device,
        GattSubscribedClient Client, GattSession Session)
    {
        internal volatile bool Expired;
    }

    private static readonly RadioText Checking = new("正在检查电脑蓝牙…", "Checking Bluetooth on this PC…");
    private static readonly RadioText BluetoothOff = new("电脑蓝牙已关闭。打开后将自动恢复键盘广播。",
        "Bluetooth is off on this PC. Turn it on to resume keyboard advertising.");
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly string BindingFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyLend", "device.id");

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2500 };
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly Dictionary<string, BluetoothLEDevice> devices = new(IdComparer);
    private readonly HashSet<string> suspended = new(IdComparer);
    private SynchronizationContext? ui;
    private Radio? radio;
    private string? adapterId;
    private RadioGatt? profile;
    private string? binding, target;
    private bool bindingLoaded, classicPhone, checkingClassic, refreshAgain, disposed;
    private TaskCompletionSource? refreshing;
    private long nextClassicCheck, nextServiceAttempt, serviceEpoch;
    private volatile ClearedKeyboard? clearedKeyboard;
    private bool togglingClearedKeyboard;

    internal bool Ready => target != null;
    internal bool HasBinding => binding != null;
    internal IReadOnlyList<PhoneChoice> Candidates { get; private set; } = Array.Empty<PhoneChoice>();
    internal bool CanEject { get; private set; }
    internal bool CanToggleClearedKeyboard => !togglingClearedKeyboard && ClearedRouteIsLive(clearedKeyboard);
    private RadioText statusText = Checking;
    internal string Status => statusText.Display;
    internal long Generation { get; private set; }
    internal event Action? Changed;

    internal Task StartAsync()
    {
        if (disposed) throw new ObjectDisposedException(nameof(RadioLink));
        if (ui == null)
        {
            ui = SynchronizationContext.Current ?? throw new InvalidOperationException(
                UiLanguage.T("请从窗口线程启动蓝牙服务", "Start the Bluetooth service on the window thread."));
            timer.Tick += Tick;
            timer.Start();
        }
        return RefreshAsync();
    }

    private void Tick(object? sender, EventArgs args)
    {
        // 较慢的系统枚举完成后再轮询，避免计时器一直延长同一轮刷新。
        if (refreshing == null) _ = RefreshAsync();
    }

    private void Post(Action action)
    {
        if (disposed) return;
        try { ui?.Post(_ => { if (!disposed) action(); }, null); }
        catch (InvalidOperationException) { /* 窗口消息循环已结束。 */ }
    }

    private void ScheduleRefresh() => Post(() => _ = RefreshAsync());

    // 系统事件与定时检查共用一次串行刷新，异步枚举期间收到的新事件会补查一次。
    private Task RefreshAsync()
    {
        if (disposed) return Task.CompletedTask;
        if (refreshing != null)
        {
            refreshAgain = true;
            return refreshing.Task;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshing = completion;
        _ = RefreshLoopAsync(completion);
        return completion.Task;
    }

    private async Task RefreshLoopAsync(TaskCompletionSource completion)
    {
        try
        {
            do
            {
                refreshAgain = false;
                try { await CheckAsync(); }
                catch (Exception ex)
                {
                    if (!disposed)
                    {
                        ForgetClearedKeyboard();
                        UpdateCandidates([]);
                        // 系统关闭 GATT 对象或适配器 RPC 中断时才重建服务。
                        if (ex.HResult is unchecked((int)0x80000013) or unchecked((int)0x800706BA))
                            DropProfile();
                        nextServiceAttempt = Environment.TickCount64 + 4000;
                        var detail = ex is RadioException error ? error.Detail : new RadioText(ex.Message, ex.Message);
                        SetState(null, false, new($"蓝牙检查失败。{detail.Zh} (0x{ex.HResult:X8})",
                            $"Bluetooth check failed. {detail.En} (0x{ex.HResult:X8})"));
                    }
                }
            } while (refreshAgain && !disposed);
        }
        finally
        {
            refreshing = null;
            completion.TrySetResult();
        }
    }

    private async Task CheckAsync()
    {
        if (!bindingLoaded)
        {
            try { binding = File.ReadAllText(BindingFile).Trim(); }
            catch (FileNotFoundException) { binding = null; }
            catch (DirectoryNotFoundException) { binding = null; }
            if (binding?.Length == 0)
            {
                SetState(null, false, new("绑定记录为空，请在设置中清除绑定。",
                    "The saved device record is empty. Clear the binding in Settings."));
                return;
            }
            bindingLoaded = true;
        }

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (disposed) return;
        if (!IdComparer.Equals(adapter?.DeviceId, adapterId) || radio == null)
        {
            DropProfile();
            SetState(null, false, Checking);
            if (radio != null) radio.StateChanged -= RadioChanged;
            radio = null;
            adapterId = adapter?.DeviceId;
            var found = adapter == null ? null : await adapter.GetRadioAsync();
            if (disposed) return;
            radio = found;
            if (radio != null) radio.StateChanged += RadioChanged;
            nextServiceAttempt = 0;
        }
        if (adapter == null || radio == null || radio.State != RadioState.On || !adapter.IsPeripheralRoleSupported)
        {
            DropProfile();
            RadioText status = adapter == null ? new("未找到电脑蓝牙适配器。", "No Bluetooth adapter was found on this PC.")
                : radio == null ? new("无法读取蓝牙开关，请检查 Windows 蓝牙设置。",
                    "Couldn't read the Bluetooth switch. Check Windows Bluetooth settings.")
                : radio.State == RadioState.Off ? BluetoothOff
                : radio.State != RadioState.On ? new("电脑蓝牙不可用，请检查 Windows 蓝牙设置和硬件开关。",
                    "Bluetooth is unavailable. Check Windows Bluetooth settings and the hardware switch.")
                : new("此电脑的蓝牙适配器或驱动不支持低功耗蓝牙外设模式。",
                    "This PC’s Bluetooth adapter or driver doesn't support Bluetooth LE peripheral mode.");
            SetState(null, false, status);
            return;
        }

        if (profile == null)
        {
            if (Environment.TickCount64 < nextServiceAttempt) return;
            long epoch = serviceEpoch;
            var created = await RadioGatt.CreateAsync(ScheduleRefresh, (id, pause) => SuspendChanged(epoch, id, pause));
            if (disposed || epoch != serviceEpoch || radio.State != RadioState.On)
            {
                created.Dispose();
                return;
            }
            profile = created;
        }

        RefreshClassicHint();
        var currentProfile = profile;
        var ids = SubscriptionIds();
        if (clearedKeyboard != null && !ClearedRouteIsLive(clearedKeyboard)) ForgetClearedKeyboard();
        UpdateCandidates(Candidates.Where(row => ids.Contains(row.Id, IdComparer) && Connected(row.Id)).ToArray());
        foreach (var removed in devices.Keys.Except(ids, IdComparer).ToArray())
        {
            devices[removed].ConnectionStatusChanged -= DeviceChanged;
            devices[removed].Dispose();
            devices.Remove(removed);
            suspended.Remove(removed);
        }
        foreach (var id in ids.Where(id => !devices.ContainsKey(id)))
        {
            BluetoothLEDevice? device;
            try { device = await BluetoothLEDevice.FromIdAsync(id); }
            catch (Exception)
            {
                if (disposed || currentProfile != profile) return;
                continue; // 一台设备暂不可读时，继续识别其他已订阅设备。
            }
            if (disposed || currentProfile != profile || radio.State != RadioState.On)
            {
                device?.Dispose();
                return;
            }
            if (device == null) continue;
            devices.Add(id, device);
            device.ConnectionStatusChanged += DeviceChanged;
        }
        await EvaluateAsync();
    }

    private string[] SubscriptionIds() => profile!.Keyboard.SubscribedClients
        .Where(c => c.Session.SessionStatus == GattSessionStatus.Active)
        .Select(c => c.Session.DeviceId.Id).Distinct(IdComparer).ToArray();

    private bool Connected(string id) => devices.TryGetValue(id, out var device)
        && device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    private void UpdateCandidates(PhoneChoice[] choices)
    {
        if (Candidates.SequenceEqual(choices)) return;
        Candidates = Array.AsReadOnly(choices);
        if (!disposed) Changed?.Invoke();
    }

    private void RemoveCandidate(string id) =>
        UpdateCandidates(Candidates.Where(row => !IdComparer.Equals(row.Id, id)).ToArray());

    private PhoneChoice[] DescribeDevices(string[] online)
    {
        // 只显示地址末三段；同名且末段也相同时显示完整地址。
        var rows = online.Select(id =>
        {
            var device = devices[id];
            string name = string.IsNullOrWhiteSpace(device.Name) ? "Bluetooth LE" : device.Name;
            string hex = device.BluetoothAddress.ToString("X12");
            string address = string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
            return new PhoneChoice(id, name, address);
        }).OrderBy(row => row.Name, IdComparer).ThenBy(row => row.Id, IdComparer).ToArray();
        return rows.Select((row, index) =>
        {
            string suffix = row.Detail[^8..];
            bool ambiguous = rows.Any(other => !IdComparer.Equals(other.Id, row.Id)
                && IdComparer.Equals(other.Name, row.Name) && other.Detail.EndsWith(suffix, StringComparison.Ordinal));
            if (!ambiguous) return row with { Detail = $"…:{suffix}" };
            // 驱动未提供地址时多个设备可能都返回零，用固定排序的编号补充区分。
            bool sameAddress = rows.Any(other => !IdComparer.Equals(other.Id, row.Id)
                && IdComparer.Equals(other.Name, row.Name) && other.Detail == row.Detail);
            return sameAddress ? row with { Detail = $"{row.Detail} · #{index + 1}" } : row;
        }).ToArray();
    }

    private async Task EvaluateAsync()
    {
        if (profile == null || radio?.State != RadioState.On) return;
        var ids = SubscriptionIds();
        // Windows 可能保留离线的 CCCD。只有输入订阅、活动会话和 LE 链路同时存在才可用。
        var online = ids.Where(Connected).ToArray();
        UpdateCandidates(DescribeDevices(online));
        suspended.RemoveWhere(id => devices.ContainsKey(id) && !Connected(id));
        if (online.Length == 0)
        {
            // 广播服务用于发现。对端连上后，即使广播停止，输入通道仍可继续工作。
            var broadcast = profile.CheckIdleAdvertising(ids.All(id => devices.ContainsKey(id)));
            if (broadcast != null)
            {
                SetState(null, false, broadcast);
                return;
            }
            var hint = new RadioText($"在 iPhone 的“设置 → 蓝牙”中点按「{Environment.MachineName}」。",
                $"On your iPhone, open Settings → Bluetooth and tap “{Environment.MachineName}”.");
            SetState(null, false, classicPhone ? new($"手机已连接经典蓝牙，键盘通道尚未就绪。{hint.Zh}",
                    $"Phone connected via Bluetooth Classic. Keyboard connection pending. {hint.En}")
                : binding == null ? new($"正在广播蓝牙键盘。{hint.Zh}", $"Keyboard is discoverable. {hint.En}")
                : new($"等待已绑定的 iPhone 回连。{hint.Zh}", $"Waiting for your saved iPhone to reconnect. {hint.En}"));
            return;
        }
        if (binding == null)
        {
            SetState(null, false, new("请选择接收键盘输入的设备。", "Choose the device that will receive keyboard input."));
            return;
        }
        string id = binding;
        if (!online.Contains(id, IdComparer))
        {
            SetState(null, false, new("已绑定的 iPhone 离线。要使用当前连接的设备，请先在设置中清除绑定。",
                "Your saved iPhone is offline. Clear the binding in Settings to use the connected device."));
            return;
        }
        if (suspended.Contains(id))
        {
            SetState(null, false, new("iPhone 已暂停键盘输入，等待恢复。", "Keyboard input is paused on your iPhone."));
            return;
        }
        var service = profile;
        long generation = Generation;
        if (target == null)
        {
            // 重连时远端可能还认为某个修饰键按着，先释放全部键再开放捕获。
            if (!await SendToAsync(service, id, generation, false, [new byte[8]])) return;
            if (!CurrentRoute(service, id, generation)) return;
        }
        bool eject = FindClient(service.Consumer, id) != null;
        string name = devices[id].Name;
        if (string.IsNullOrWhiteSpace(name)) name = "Bluetooth LE";
        SetState(id, eject, new($"键盘已连接 · {name}", $"Keyboard connected · {name}"));
    }

    private void SetState(string? id, bool eject, RadioText status)
    {
        if (disposed) return;
        bool routeChanged = !IdComparer.Equals(id, target);
        if (!routeChanged && eject == CanEject && status == statusText) return;
        if (routeChanged)
        {
            Generation++;
            profile?.SetReportTarget(id);
        }
        target = id;
        CanEject = eject;
        statusText = status;
        Changed?.Invoke();
    }

    private void RadioChanged(Radio sender, object args)
    {
        bool off = sender.State != RadioState.On;
        if (off && sender == radio && clearedKeyboard is { } previous) previous.Expired = true;
        Post(() =>
        {
            if (sender != radio) return;
            if (off)
            {
                DropProfile();
                SetState(null, false, BluetoothOff);
            }
            nextServiceAttempt = 0;
            _ = RefreshAsync();
        });
    }

    private void DeviceChanged(BluetoothLEDevice sender, object args)
    {
        // 在原生回调中失效，即使快速回连发生在下一次 UI 轮询之前也不能沿用权限。
        if (clearedKeyboard is { } previous && previous.Device == sender) previous.Expired = true;
        string id = sender.DeviceId;
        bool offline = sender.ConnectionStatus != BluetoothConnectionStatus.Connected;
        Post(() =>
        {
            if (!devices.TryGetValue(id, out var current) || current != sender) return;
            if (offline && clearedKeyboard is { } cleared && cleared.Device == sender) ForgetClearedKeyboard();
            if (offline) RemoveCandidate(id);
            if (offline && IdComparer.Equals(target, id))
                SetState(null, false, new("iPhone 的键盘连接已断开，等待回连。",
                    "Keyboard disconnected. Waiting for your iPhone to reconnect."));
            if (offline) suspended.Remove(id);
            _ = RefreshAsync();
        });
    }

    private void SuspendChanged(long epoch, string id, bool paused) => Post(() =>
    {
        if (epoch != serviceEpoch) return;
        if (paused) suspended.Add(id);
        else suspended.Remove(id);
        _ = RefreshAsync();
    });

    private void RefreshClassicHint()
    {
        if (checkingClassic || Environment.TickCount64 < nextClassicCheck) return;
        nextClassicCheck = Environment.TickCount64 + 5000;
        _ = CheckClassicAsync();
    }

    private async Task CheckClassicAsync()
    {
        checkingClassic = true;
        try
        {
            // 经典蓝牙耳机或鼠标不应触发手机回连提示；设备主类别 2 表示电话。
            const string major = "System.Devices.Aep.Bluetooth.Cod.Major";
            var query = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var peers = await DeviceInformation.FindAllAsync(query, new[] { major },
                DeviceInformationKind.AssociationEndpoint);
            bool found = peers.Any(peer => (peer.Properties.TryGetValue(major, out var kind) && kind is ushort code && code == 2)
                || peer.Name.Contains("iPhone", StringComparison.OrdinalIgnoreCase));
            if (!disposed && classicPhone != found)
            {
                classicPhone = found;
                _ = RefreshAsync();
            }
        }
        catch (Exception) { classicPhone = false; /* 经典链路提示不影响 HID 输入。 */ }
        finally { checkingClassic = false; }
    }

    internal async Task ResetBindingAsync()
    {
        if (disposed) return;
        // 清除后立即按现有订阅重算，手机无需重新订阅。
        try { File.Delete(BindingFile); }
        catch (DirectoryNotFoundException) { }
        CaptureClearedKeyboard();
        binding = null;
        bindingLoaded = true;
        Generation++;
        SetState(null, false, new("绑定已清除，正在识别已连接的键盘订阅者…",
            "Binding cleared. Checking which device is connected to the keyboard…"));
        await RefreshAsync();
    }

    internal async Task<string?> SelectDeviceAsync(string id)
    {
        // 如果原设备的弹出键已按下，先让这次操作向原订阅者发完抬起，再建立新绑定。
        if (togglingClearedKeyboard)
        {
            await sendLock.WaitAsync();
            sendLock.Release();
        }
        if (HasBinding)
            return UiLanguage.T("请先清除已保存的绑定。", "Clear the saved binding first.");
        string offline = UiLanguage.T("设备已离线，请从当前列表重新选择。",
            "The device went offline. Choose a device from the current list.");
        try
        {
            // 按钮可能显示上一次枚举的快照；提交前直接向当前服务确认订阅和 LE 连接。
            if (disposed || !bindingLoaded || !Candidates.Any(row => IdComparer.Equals(row.Id, id))
                || profile == null || radio?.State != RadioState.On || !Connected(id) || FindClient(profile.Keyboard, id) == null)
            {
                RemoveCandidate(id);
                await RefreshAsync();
                return offline;
            }
            // 写入相同卷的临时文件后再改名，已有的绑定（包括其他实例写入的）不被覆盖。
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(BindingFile)!, "_temp"));
            string temp = Path.Combine(directory.FullName, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temp, id);
                File.Move(temp, BindingFile);
            }
            finally { File.Delete(temp); }
            binding = id;
            ForgetClearedKeyboard();
            Changed?.Invoke();
            await RefreshAsync();
            return null;
        }
        catch (Exception ex)
        {
            UpdateCandidates([]);
            await RefreshAsync();
            return UiLanguage.T($"设备绑定失败。{ex.Message}", $"Couldn't save the device binding. {ex.Message}");
        }
    }

    private static GattSubscribedClient? FindClient(GattLocalCharacteristic input, string id) =>
        input.SubscribedClients.FirstOrDefault(c => c.Session.SessionStatus == GattSessionStatus.Active
            && IdComparer.Equals(c.Session.DeviceId.Id, id));

    internal Task<bool> SendAsync(byte[] report) =>
        report.Length == 8 ? SendReportsAsync(false, [report.ToArray()]) : Task.FromResult(false);

    internal Task<bool> EjectAsync() => SendReportsAsync(true, [[1], [0]]);

    private void CaptureClearedKeyboard()
    {
        ForgetClearedKeyboard();
        var id = target;
        var service = profile;
        if (id == null || service == null || !CanEject || !IdComparer.Equals(id, binding)) return;
        try
        {
            if (!CurrentRoute(service, id, Generation)) return;
            var client = FindClient(service.Consumer, id);
            if (client == null) return;
            var route = new ClearedKeyboard(id, service, devices[id], client, client.Session);
            clearedKeyboard = route;
            route.Session.SessionStatusChanged += ClearedSessionChanged;
        }
        catch (Exception) { ForgetClearedKeyboard(); }
    }

    private void ClearedSessionChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        if (args.Status != GattSessionStatus.Closed || clearedKeyboard is not { } route || route.Session != sender) return;
        route.Expired = true;
        ScheduleRefresh();
    }

    private bool ClearedRouteIsLive(ClearedKeyboard? route)
    {
        if (route == null || route.Expired || !ReferenceEquals(route, clearedKeyboard) || disposed || binding != null
            || profile != route.Profile) return false;
        try
        {
            if (radio?.State == RadioState.On && devices.TryGetValue(route.Id, out var device) && device == route.Device
                && device.ConnectionStatus == BluetoothConnectionStatus.Connected
                && route.Session.SessionStatus == GattSessionStatus.Active
                && FindClient(route.Profile.Keyboard, route.Id) != null && FindClient(route.Profile.Consumer, route.Id) != null)
                return !suspended.Contains(route.Id);
        }
        catch (Exception) { /* 蓝牙对象可能已在系统端关闭。 */ }
        route.Expired = true;
        return false;
    }

    private void ForgetClearedKeyboard()
    {
        if (clearedKeyboard is not { } route) return;
        clearedKeyboard = null;
        route.Expired = true;
        try { route.Session.SessionStatusChanged -= ClearedSessionChanged; }
        catch (Exception) { /* 会话可能已关闭。 */ }
        if (!disposed) Changed?.Invoke();
    }

    internal async Task<bool> ToggleClearedKeyboardAsync()
    {
        var route = clearedKeyboard;
        if (route == null || togglingClearedKeyboard) return false;
        togglingClearedKeyboard = true;
        try
        {
            Changed?.Invoke();
            await sendLock.WaitAsync();
            try
            {
                // 此入口只允许发送一次 Eject 按下和抬起，始终使用解绑时捕获的客户端。
                foreach (byte value in new byte[] { 1, 0 })
                {
                    if (!ClearedRouteIsLive(route)) return false;
                    var result = await route.Profile.Consumer.NotifyValueAsync(RadioGatt.Buffer([value]), route.Client);
                    if (result.Status != GattCommunicationStatus.Success) return false;
                }
                return true;
            }
            finally { sendLock.Release(); }
        }
        catch (Exception) { return false; }
        finally
        {
            togglingClearedKeyboard = false;
            if (ReferenceEquals(clearedKeyboard, route)) ForgetClearedKeyboard();
        }
    }

    private Task<bool> SendReportsAsync(bool consumer, byte[][] reports)
    {
        var id = target;
        var service = profile;
        if (disposed || id == null || service == null || (consumer && !CanEject)) return Task.FromResult(false);
        return SendToAsync(service, id, Generation, consumer, reports);
    }

    private bool CurrentRoute(RadioGatt service, string id, long generation)
    {
        if (disposed || generation != Generation || service != profile || radio?.State != RadioState.On
            || !IdComparer.Equals(binding, id) || !Connected(id) || suspended.Contains(id)) return false;
        return FindClient(service.Keyboard, id) != null;
    }

    private async Task<bool> SendToAsync(RadioGatt service, string id, long generation, bool consumer, byte[][] reports)
    {
        await sendLock.WaitAsync();
        try
        {
            foreach (var report in reports)
            {
                // 每次发送重新检查订阅集合，排队中的报告不可跨目标或跨重连发送。
                if (!CurrentRoute(service, id, generation)) return false;
                var input = consumer ? service.Consumer : service.Keyboard;
                var client = FindClient(input, id);
                if (client == null) return false;
                var result = await input.NotifyValueAsync(RadioGatt.Buffer(report), client);
                if (disposed || generation != Generation || service != profile) return false;
                if (result.Status != GattCommunicationStatus.Success)
                {
                    RemoveCandidate(id);
                    SetState(null, false, new($"键盘发送中断（{result.Status}），正在检查连接。",
                        $"Keyboard input interrupted ({result.Status}). Checking the connection."));
                    return false;
                }
                service.Record(id, consumer, report);
            }
            return true;
        }
        catch (Exception)
        {
            if (!disposed && Generation == generation)
            {
                RemoveCandidate(id);
                SetState(null, false, new("键盘发送中断，正在检查连接。", "Keyboard input interrupted. Checking the connection."));
            }
            return false;
        }
        finally { sendLock.Release(); }
    }

    private void DropProfile()
    {
        ForgetClearedKeyboard();
        UpdateCandidates([]);
        serviceEpoch++;
        profile?.Dispose();
        profile = null;
        foreach (var device in devices.Values)
        {
            device.ConnectionStatusChanged -= DeviceChanged;
            device.Dispose();
        }
        devices.Clear();
        suspended.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Generation++;
        target = null;
        CanEject = false;
        timer.Stop();
        timer.Dispose();
        if (radio != null) radio.StateChanged -= RadioChanged;
        DropProfile();
    }
}
