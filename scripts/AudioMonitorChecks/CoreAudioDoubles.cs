using System.Collections.Concurrent;
using NAudio.CoreAudioApi.Interfaces;

// Replaces device access only. Production notification scheduling and lifecycle code is source-linked.
namespace NAudio.CoreAudioApi
{
    public enum DataFlow { Render, Capture, All }
    public enum Role { Console, Multimedia, Communications }
    public enum DeviceState { Active, Disabled }
    public struct PropertyKey { }
    public delegate void AudioEndpointVolumeNotificationDelegate(object data);

    public sealed class AudioEndpointVolume(string deviceId) : IDisposable
    {
        private static readonly ConcurrentBag<AudioEndpointVolume> Instances = new();
        private AudioEndpointVolumeNotificationDelegate? _callbacks;
        public static int Subscribers;

        private readonly string _deviceId = deviceId;
        private bool _registered;
        public event AudioEndpointVolumeNotificationDelegate OnVolumeNotification
        {
            add
            {
                if (!_registered) { Instances.Add(this); _registered = true; }
                _callbacks += value;
                Interlocked.Increment(ref Subscribers);
            }
            remove { _callbacks -= value; Interlocked.Decrement(ref Subscribers); }
        }
        public static void RaiseAll()
        {
            foreach (var instance in Instances) instance._callbacks?.Invoke(new());
        }
        public static void Raise(string deviceId)
        {
            foreach (var instance in Instances)
                if (instance._deviceId == deviceId) instance._callbacks?.Invoke(new());
        }
        public void Dispose() { }
    }

    public sealed class MMDevice(string id, DataFlow flow, DeviceState state = DeviceState.Active) : IDisposable
    {
        private AudioEndpointVolume? _volume;
        public string ID => id;
        public DeviceState State => state;
        public DataFlow DataFlow => flow;
        public AudioEndpointVolume AudioEndpointVolume => _volume ??= new(id);
        public void Dispose() => _volume?.Dispose();
    }

    public sealed class MMDeviceEnumerator : IDisposable
    {
        public static int Created, Registered, Unregistered, Disposed, Reads;
        public static bool FailRegister;
        public static bool DistinctDefaultRoles;
        public static readonly ConcurrentDictionary<string, (DataFlow Flow, DeviceState State)> Devices = new();
        public static IMMNotificationClient? Client;

        public MMDeviceEnumerator() => Interlocked.Increment(ref Created);
        public MMDevice GetDefaultAudioEndpoint(DataFlow flow, Role role)
        {
            Interlocked.Increment(ref Reads);
            return new MMDevice(flow + (DistinctDefaultRoles ? role.ToString() : ""), flow);
        }
        public MMDevice GetDevice(string id)
        {
            Interlocked.Increment(ref Reads);
            if (Devices.TryGetValue(id, out var device)) return new(id, device.Flow, device.State);
            if (id == "Render") return new(id, DataFlow.Render);
            if (id == "Capture") return new(id, DataFlow.Capture);
            throw new InvalidOperationException("Fixture endpoint is absent.");
        }
        public int RegisterEndpointNotificationCallback(IMMNotificationClient client)
        {
            if (FailRegister) return unchecked((int)0x80004005);
            Client = client;
            Interlocked.Increment(ref Registered);
            return 0;
        }
        public int UnregisterEndpointNotificationCallback(IMMNotificationClient client)
        {
            Client = null;
            Interlocked.Increment(ref Unregistered);
            return 0;
        }
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }
}

namespace NAudio.CoreAudioApi.Interfaces
{
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged(string deviceId, DeviceState state);
        void OnDeviceAdded(string deviceId);
        void OnDeviceRemoved(string deviceId);
        void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId);
        void OnPropertyValueChanged(string deviceId, PropertyKey key);
    }
}
