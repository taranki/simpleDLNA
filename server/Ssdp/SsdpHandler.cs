﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NMaier.SimpleDlna.Utilities;
using Threading = System.Threading;
using Timers = System.Timers;

namespace NMaier.SimpleDlna.Server.Ssdp
{
  internal sealed class SsdpHandler : Logging, IDisposable
  {

    private readonly UdpClient client = new UdpClient();
    private readonly Threading.AutoResetEvent datagramPosted = new Threading.AutoResetEvent(false);
    private const int DATAGRAMS_PER_MESSAGE = 3;
    private readonly Dictionary<Guid, List<UpnpDevice>> devices = new Dictionary<Guid, List<UpnpDevice>>();
    private readonly Queue<Datagram> messageQueue = new Queue<Datagram>();
    private readonly Timers.Timer notificationTimer = new Timers.Timer(60000);
    private readonly Timers.Timer queueTimer = new Timers.Timer(1000);
    private static readonly Random random = new Random();
    private bool running = true;
    const string SSDP_ADDR = "239.255.255.250";
    private static readonly IPEndPoint SSDP_ENDP = new IPEndPoint(IPAddress.Parse(SSDP_ADDR), SSDP_PORT);
    private static readonly IPAddress SSDP_IP = IPAddress.Parse(SSDP_ADDR);
    const int SSDP_PORT = 1900;



    public SsdpHandler()
    {
      notificationTimer.Elapsed += Tick;
      notificationTimer.Enabled = true;

      queueTimer.Elapsed += ProcessQueue;

      client.Client.UseOnlyOverlappedIO = true;
      client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
      client.ExclusiveAddressUse = false;
      client.Client.Bind(new IPEndPoint(IPAddress.Any, SSDP_PORT));
      client.JoinMulticastGroup(SSDP_IP, 2);
      Info("SSDP service started");
      Receive();
    }




    public void Dispose()
    {
      Debug("Disposing SSDP");
      running = false;
      while (messageQueue.Count != 0) {
        datagramPosted.WaitOne();
      }

      client.DropMulticastGroup(SSDP_IP);

      notificationTimer.Enabled = false;
      queueTimer.Enabled = false;
      notificationTimer.Dispose();
      queueTimer.Dispose();

    }

    private void ProcessQueue(object sender, Timers.ElapsedEventArgs e)
    {
      lock (messageQueue) {
        while (messageQueue.Count != 0) {
          var msg = messageQueue.Peek();
          if (msg != null && (running || msg.Sticky)) {
            msg.Send();
            if (msg.SendCount > DATAGRAMS_PER_MESSAGE) {
              messageQueue.Dequeue();
            }
            break;
          }
          else {
            messageQueue.Dequeue();
          }
        }
      }
      datagramPosted.Set();
      queueTimer.Enabled = messageQueue.Count != 0;
      queueTimer.Interval = random.Next(50, running ? 300 : 100);
    }

    private void Receive()
    {
      try {
        client.BeginReceive(new AsyncCallback(ReceiveCallback), null);
      }
      catch (ObjectDisposedException) { }
    }

    private void ReceiveCallback(IAsyncResult result)
    {
      try {
        var endpoint = new IPEndPoint(IPAddress.None, SSDP_PORT);
        var received = client.EndReceive(result, ref endpoint);
#if DUMP_ALL_SSDP
        DebugFormat("{0} - SSDP Received a datagram", endpoint);
#endif

        using (var reader = new StreamReader(new MemoryStream(received), Encoding.ASCII)) {
          var proto = reader.ReadLine().Trim();
          var method = proto.Split(new char[] { ' ' }, 2)[0];
          var headers = new Headers();
          for (var line = reader.ReadLine(); line != null; line = reader.ReadLine()) {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) {
              break;
            }
            var parts = line.Split(new char[] { ':' }, 2);
            headers[parts[0]] = parts[1].Trim();
          }
#if DUMP_ALL_SSDP
          DebugFormat("{0} - Datagram method: {1}", endpoint, method);
          Debug(headers);
#endif
          if (method == "M-SEARCH") {
            RespondToSearch(endpoint, headers["st"]);
          }
        }
      }
      catch (Exception ex) {
        Warn("Failed to read SSDP message", ex);
      }
      Receive();
    }

    private void SendDatagram(IPEndPoint endpoint, string msg, bool sticky)
    {
      if (!running) {
        return;
      }
      var dgram = new Datagram(endpoint, msg, sticky);
      lock (messageQueue) {
        if (messageQueue.Count == 0) {
          dgram.Send();
        }
        messageQueue.Enqueue(dgram);
      }
      queueTimer.Enabled = true;
    }

    private void SendSearchResponse(IPEndPoint endpoint, UpnpDevice dev)
    {
      var headers = new RawHeaders();
      var method = "HTTP/1.1 200 OK\r\n";
      headers.Add("CACHE-CONTROL", "max-age = 720");
      headers.Add("DATE", DateTime.Now.ToString("R"));
      headers.Add("EXT", "");
      headers.Add("LOCATION", dev.Descriptor.ToString());
      headers.Add("SERVER", HttpServer.Signature);
      headers.Add("ST", dev.Type);
      headers.Add("USN", dev.USN);

      SendDatagram(endpoint, method + headers.HeaderBlock + "\r\n", false);
      InfoFormat("{1} - Responded to a {0} request", dev.Type, endpoint);
    }

    private void Tick(object sender, Timers.ElapsedEventArgs e)
    {
      Debug("Sending SSDP notifications!");
      notificationTimer.Interval = random.Next(5000, 30000);
      NotifyAll();
    }

    internal void NotifyAll()
    {
      Debug("NotifyAll");
      foreach (var dl in devices.Values) {
        foreach (var d in dl) {
          NotifyDevice(d, "alive", false);
        }
      }
    }

    internal void NotifyDevice(UpnpDevice dev, string type, bool sticky)
    {
      Debug("NotifyDevice");
      var headers = new RawHeaders();
      var method = "NOTIFY * HTTP/1.1\r\n";
      headers.Add("HOST", "239.255.255.250:1900");
      headers.Add("CACHE-CONTROL", "max-age = 720");
      headers.Add("LOCATION", dev.Descriptor.ToString());
      headers.Add("SERVER", HttpServer.Signature);
      headers.Add("NTS", "ssdp:" + type);
      headers.Add("NT", dev.Type);
      headers.Add("USN", dev.USN);

      SendDatagram(SSDP_ENDP, method + headers.HeaderBlock + "\r\n", sticky);
      DebugFormat("{0} said {1}", dev.USN, type);
    }

    internal void RegisterNotification(Guid UUID, Uri Descriptor)
    {
      List<UpnpDevice> list;
      if (!devices.TryGetValue(UUID, out list)) {
        devices.Add(UUID, list = new List<UpnpDevice>());
      }
      foreach (var t in new string[] { "upnp:rootdevice", "urn:schemas-upnp-org:device:MediaServer:1", "urn:schemas-upnp-org:service:ContentDirectory:1", "uuid:" + UUID.ToString() }) {
        list.Add(new UpnpDevice(UUID, t, Descriptor));
      }

      NotifyAll();
      DebugFormat("Registered mount {0}", UUID);
    }

    internal void RespondToSearch(IPEndPoint endpoint, string req)
    {
      if (req == "ssdp:all") {
        req = null;
      }

      Debug("RespondToSearch");
      foreach (var dl in devices.Values) {
        foreach (var d in dl) {
          if (!string.IsNullOrEmpty(req) && req != d.Type) {
            continue;
          }
          SendSearchResponse(endpoint, d);
        }
      }
    }

    internal void UnregisterNotification(Guid UUID)
    {
      List<UpnpDevice> dl;
      if (!devices.TryGetValue(UUID, out dl)) {
        return;
      }
      foreach (var d in dl) {
        NotifyDevice(d, "byebye", true);
      }
      devices.Remove(UUID);
      DebugFormat("Unregistered mount {0}", UUID);
    }
  }
}
