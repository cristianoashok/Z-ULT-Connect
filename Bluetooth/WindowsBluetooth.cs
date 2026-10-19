// WindowsBluetooth.cs
// ---------------------------------------------------------------------------
// Finds the headphones and opens their control channel.
//
// Sony headphones expose a classic Bluetooth serial channel (RFCOMM) with the
// service ID below. Windows already knows it from pairing, so we ask Windows
// for "paired devices that offer this service".
//
// Two ways to connect, tried in order:
//   1. The modern Windows Bluetooth API (Windows.Devices.Bluetooth.Rfcomm).
//   2. A classic Winsock Bluetooth socket - the exact method the other
//      Windows client (SonyHeadphonesClient) uses, which worked on this PC.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using ZUltConnect.Protocol;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace ZUltConnect.Bluetooth
{
    /// <summary>A pair of Sony headphones Windows knows about.</summary>
    public sealed record FoundHeadphones(string Name, string ServiceId, ulong Address)
    {
        public override string ToString() => Name;
    }

    public static class WindowsBluetooth
    {
        /// <summary>Sony's control-channel service ID (same for WH-1000XM5 and ULT Wear).</summary>
        public static readonly Guid SonyServiceUuid = new("956C7B26-D49A-4BA8-B03F-B17D393CB6E2");

        /// <summary>Lists paired Bluetooth devices that offer Sony's control channel.</summary>
        public static async Task<List<FoundHeadphones>> FindAsync()
        {
            string selector = RfcommDeviceService.GetDeviceSelector(RfcommServiceId.FromUuid(SonyServiceUuid));
            DeviceInformationCollection infos = await DeviceInformation.FindAllAsync(selector);

            var found = new List<FoundHeadphones>();
            foreach (DeviceInformation info in infos)
            {
                string name = info.Name;
                ulong address = 0;
                try
                {
                    using RfcommDeviceService? service = await RfcommDeviceService.FromIdAsync(info.Id);
                    if (service?.Device != null)
                    {
                        name = service.Device.Name;
                        address = service.Device.BluetoothAddress;
                    }
                }
                catch
                {
                    // Not fatal - we can still try to connect by ID.
                }
                found.Add(new FoundHeadphones(string.IsNullOrWhiteSpace(name) ? "Sony headphones" : name, info.Id, address));
            }
            return found;
        }

        /// <summary>Opens the control channel. Throws with a readable message if it can't.</summary>
        public static async Task<IByteLink> ConnectAsync(FoundHeadphones headphones, Action<string> log)
        {
            Exception? firstError = null;
            try
            {
                log("• Connecting with the Windows Bluetooth API...");
                RfcommDeviceService? service = await RfcommDeviceService.FromIdAsync(headphones.ServiceId);
                if (service == null)
                    throw new InvalidOperationException("Windows couldn't open the headphones' control service.");

                var socket = new StreamSocket();
                await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                                          SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                log("• Connected.");
                return new WinRtLink(service, socket);
            }
            catch (Exception ex)
            {
                firstError = ex;
                log($"• That didn't work ({ex.Message.Trim()}).");
            }

            if (headphones.Address != 0)
            {
                log("• Trying the classic Winsock way...");
                try
                {
                    var link = await Task.Run(() => WinsockLink.Connect(headphones.Address, SonyServiceUuid));
                    log("• Connected.");
                    return link;
                }
                catch (Exception ex)
                {
                    log($"• That didn't work either ({ex.Message.Trim()}).");
                }
            }

            throw new InvalidOperationException(
                "Couldn't open the headphones' control channel. Make sure they're on and connected to this PC, " +
                "and close any other headphone app (like SonyHeadphonesClient) that may be using it.", firstError);
        }
    }

    // -----------------------------------------------------------------------
    // Link #1: modern Windows Bluetooth API
    // -----------------------------------------------------------------------
    internal sealed class WinRtLink : IByteLink
    {
        private readonly RfcommDeviceService _service;
        private readonly StreamSocket _socket;
        private readonly Windows.Storage.Streams.Buffer _readBuffer = new(2048);

        public WinRtLink(RfcommDeviceService service, StreamSocket socket)
        {
            _service = service;
            _socket = socket;
        }

        public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct)
        {
            IBuffer result = await _socket.InputStream
                .ReadAsync(_readBuffer, _readBuffer.Capacity, InputStreamOptions.Partial)
                .AsTask(ct);
            int n = (int)Math.Min(result.Length, (uint)buffer.Length);
            if (n > 0) result.CopyTo(0, buffer, 0, n);
            return n;
        }

        public async Task WriteAsync(byte[] data, CancellationToken ct)
        {
            await _socket.OutputStream.WriteAsync(data.AsBuffer()).AsTask(ct);
            await _socket.OutputStream.FlushAsync().AsTask(ct);
        }

        public void Dispose()
        {
            try { _socket.Dispose(); } catch { }
            try { _service.Dispose(); } catch { }
        }
    }

    // -----------------------------------------------------------------------
    // Link #2: classic Winsock Bluetooth socket (AF_BTH / RFCOMM)
    // -----------------------------------------------------------------------
    internal sealed class WinsockLink : IByteLink
    {
        private const AddressFamily AfBth = (AddressFamily)32;
        private const ProtocolType BthProtoRfcomm = (ProtocolType)3;

        private readonly Socket _socket;
        private readonly NetworkStream _stream;

        private WinsockLink(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: true);
        }

        public static WinsockLink Connect(ulong address, Guid service)
        {
            var socket = new Socket(AfBth, SocketType.Stream, BthProtoRfcomm);
            try
            {
                socket.Connect(new BluetoothEndPoint(address, service));
                return new WinsockLink(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct) =>
            await _stream.ReadAsync(buffer.AsMemory(), ct);

        public async Task WriteAsync(byte[] data, CancellationToken ct) =>
            await _stream.WriteAsync(data.AsMemory(), ct);

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
        }

        /// <summary>
        /// Windows' SOCKADDR_BTH (30 bytes, packed):
        /// family(2) | Bluetooth address(8) | service GUID(16) | port(4, 0 = look up by GUID)
        /// </summary>
        private sealed class BluetoothEndPoint : EndPoint
        {
            private readonly ulong _address;
            private readonly Guid _service;

            public BluetoothEndPoint(ulong address, Guid service)
            {
                _address = address;
                _service = service;
            }

            public override AddressFamily AddressFamily => AfBth;

            public override SocketAddress Serialize()
            {
                var sa = new SocketAddress(AfBth, 30);
                byte[] addr = BitConverter.GetBytes(_address);
                for (int i = 0; i < 8; i++) sa[2 + i] = addr[i];
                byte[] guid = _service.ToByteArray();
                for (int i = 0; i < 16; i++) sa[10 + i] = guid[i];
                for (int i = 0; i < 4; i++) sa[26 + i] = 0;
                return sa;
            }

            public override EndPoint Create(SocketAddress socketAddress) => this;
        }
    }
}
