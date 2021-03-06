﻿using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.Server
{
  internal sealed class HttpClient : Logging, IRequest, IDisposable
  {

    private readonly uint BEGIN_TIMEOUT = 30;
    private string body;
    private uint bodyBytes = 0;
    private readonly byte[] buffer = new byte[2048];
    private const int BUFFER_SIZE = 1 << 16;
    private static readonly Regex bytes = new Regex(@"^bytes=(\d+)(?:-(\d+)?)?$", RegexOptions.Compiled);
    private readonly TcpClient client;
    private static IHandler Error404 = new StaticHandler(new StringResponse(HttpCodes.NOT_FOUND, "<!doctype html><title>Not found!</title><h1>Not found!</h1><p>The requested resource was not found!</p>"));
    private static IHandler Error416 = new StaticHandler(new StringResponse(HttpCodes.RANGE_NOT_SATISFIABLE, "<!doctype html><title>Requested Range not satisfiable!</title><h1>Requested Range not satisfiable!</h1><p>Nice try, but do not try again :p</p>"));
    private static IHandler Error500 = new StaticHandler(new StringResponse(HttpCodes.INTERNAL_ERROR, "<!doctype html><title>Internal Server Error</title><h1>Internal Server Error</h1><p>Something is very rotten in the State of Denmark!</p>"));
    private bool hasHeaders = false;
    private readonly IHeaders headers = new Headers();
    private DateTime lastActivity;
    private string method;
    private readonly HttpServer owner;
    private string path;
    private readonly uint READ_TIMEOUT = (uint)TimeSpan.FromMinutes(1).TotalSeconds;
    private MemoryStream readStream;
    private uint requestCount = 0;
    private IResponse response;
    private HttpStates state;
    private readonly NetworkStream stream;
    private readonly uint WRITE_TIMEOUT = (uint)TimeSpan.FromMinutes(180).TotalSeconds;



    internal enum HttpStates
    {
      ACCEPTED,
      READBEGIN,
      READING,
      WRITEBEGIN,
      WRITING,
      CLOSED
    }



    public HttpClient(HttpServer aOwner, TcpClient aClient)
    {
      State = HttpStates.ACCEPTED;
      lastActivity = DateTime.Now;

      owner = aOwner;
      client = aClient;
      stream = client.GetStream();
      client.Client.UseOnlyOverlappedIO = true;

      RemoteEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
      LocalEndPoint = client.Client.LocalEndPoint as IPEndPoint;
    }



    public string Body
    {
      get { return body; }
    }

    public IHeaders Headers
    {
      get { return headers; }
    }

    public bool IsATimeout
    {
      get
      {
        var diff = (DateTime.Now - lastActivity).TotalSeconds;
        switch (state) {
          case HttpStates.ACCEPTED:
          case HttpStates.READBEGIN:
          case HttpStates.WRITEBEGIN:
            return diff > BEGIN_TIMEOUT;
          case HttpStates.READING:
            return diff > READ_TIMEOUT;
          case HttpStates.WRITING:
            // DLNA renderers might suspend the download when having buffered enough
            // and resume the same stream latter when buffer is emptied
            return diff > WRITE_TIMEOUT;
          case HttpStates.CLOSED:
            return true;
          default:
            throw new ApplicationException("Invalid state");
        }
      }
    }

    public IPEndPoint LocalEndPoint
    {
      get;
      private set;
    }

    public string Method
    {
      get { return method; }
    }

    public string Path
    {
      get { return path; }
    }

    public IPEndPoint RemoteEndpoint
    {
      get;
      private set;
    }

    public HttpStates State
    {
      get { return state; }
      private set
      {
        lastActivity = DateTime.Now;
        state = value;
      }
    }




    public void Dispose()
    {
      Close();
      if (readStream != null) {
        readStream.Dispose();
      }
    }

    public void Start()
    {
      ReadNext();
    }

    public override string ToString()
    {
      return RemoteEndpoint.ToString();
    }

    private void Read()
    {
      try {
        stream.BeginRead(buffer, 0, buffer.Length, ReadCallback, 0);
      }
      catch (IOException ex) {
        Warn(String.Format("{0} - Failed to BeginRead", this), ex);
        Close();
      }
    }

    private void ReadCallback(IAsyncResult result)
    {
      if (state == HttpStates.CLOSED) {
        return;
      }

      State = HttpStates.READING;

      int read = 0;
      try {
        read = stream.EndRead(result);
        if (read < 0) {
          throw new HttpException("Client did not send anything");
        }
        DebugFormat("{0} - Read {1} bytes", this, read);
        readStream.Write(buffer, 0, read);
        lastActivity = DateTime.Now;
      }
      catch (Exception) {
        if (!IsATimeout) {
          WarnFormat("{0} - Failed to read data", this);
          Close();
        }
        return;
      }

      try {
        if (!hasHeaders) {
          readStream.Seek(0, SeekOrigin.Begin);
          StreamReader reader = new StreamReader(readStream);
          for (var line = reader.ReadLine(); line != null; line = reader.ReadLine()) {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) {
              hasHeaders = true;
              readStream = new MemoryStream();
              if (headers.ContainsKey("content-length") && uint.TryParse(headers["content-length"], out bodyBytes)) {
                if (bodyBytes > (1 << 20)) {
                  throw new IOException("Body too long");
                }
                var bytes = Encoding.ASCII.GetBytes(reader.ReadToEnd());
                readStream.Write(bytes, 0, bytes.Length);
                DebugFormat("Must read body bytes {0}", bodyBytes);
              }
              else {
                readStream = new MemoryStream();
              }
              break;
            }
            if (method == null) {
              var parts = line.Split(new char[] { ' ' }, 3);
              method = parts[0].Trim().ToUpper();
              path = parts[1].Trim();
              DebugFormat("{0} - {1} request for {2}", this, method, path);
            }
            else {
              var parts = line.Split(new char[] { ':' }, 2);
              headers[parts[0]] = Uri.UnescapeDataString(parts[1]).Trim();
            }
          }
        }
        if (bodyBytes != 0 && bodyBytes > readStream.Length) {
          DebugFormat("{0} - Bytes to go {1}", this, bodyBytes - readStream.Length);
          Read();
          return;
        }
        using (readStream) {
          body = Encoding.UTF8.GetString(readStream.ToArray());
          Debug(body);
          Debug(headers);
        }
        SetupResponse();
      }
      catch (Exception ex) {
        Warn(String.Format("{0} - Failed to process request", this), ex);
        response = Error500.HandleRequest(this);
        SendResponse();
      }
    }

    private void ReadNext()
    {
      method = null;
      headers.Clear();
      hasHeaders = false;
      body = null;
      bodyBytes = 0;
      readStream = new MemoryStream();

      ++requestCount;
      State = HttpStates.READBEGIN;

      Read();
    }

    private void SendResponse()
    {
      var responseBody = response.Body;
      var st = response.Status;

      long contentLength = -1;
      string clf;
      if (!response.Headers.TryGetValue("Content-Length", out clf) || !long.TryParse(clf, out contentLength)) {
        try {
          contentLength = responseBody.Length - responseBody.Position;
          if (contentLength < 0) {
            throw new InvalidDataException();
          }
          response.Headers["Content-Length"] = contentLength.ToString();
        }
        catch (Exception) {
          // pass
        }
      }

      string ar;
      if (st == HttpCodes.OK && contentLength > 0 && headers.TryGetValue("Range", out ar)) {
        try {
          var m = bytes.Match(ar);
          if (!m.Success) {
            throw new InvalidDataException("Not parsed!");
          }
          var totalLength = contentLength;
          long start = 0, end = totalLength - 1;
          if (!long.TryParse(m.Groups[1].Value, out start) || start < 0) {
            throw new InvalidDataException("Not parsed");
          }
          if (m.Groups.Count != 3 || !long.TryParse(m.Groups[2].Value, out end) || end <= start || end >= totalLength) {
            end = totalLength - 1;
          }
          if (start >= end) {
            responseBody.Close();
            response = Error416.HandleRequest(this);
            SendResponse();
            return;
          }

          if (start > 0) {
            responseBody.Seek(start, SeekOrigin.Current);
          }
          contentLength = end - start + 1;
          response.Headers["Content-Length"] = contentLength.ToString();
          response.Headers.Add("Content-Range", String.Format("bytes {0}-{1}/{2}", start, end, totalLength));
          st = HttpCodes.PARTIAL;
        }
        catch (Exception ex) {
          Warn(String.Format("{0} - Failed to process range request!", this), ex);
        }
      }

      var hb = new StringBuilder();
      hb.AppendFormat("HTTP/1.1 {0} {1}\r\n", (uint)st, HttpPhrases.Phrases[st]);
      hb.Append(response.Headers.HeaderBlock);
      hb.Append("\r\n");

      var rs = new ConcatenatedStream();
      try {
        var headerStream = new MemoryStream(Encoding.ASCII.GetBytes(hb.ToString()));
        rs.AddStream(headerStream);
        if (method != "HEAD" && responseBody != null) {
          rs.AddStream(responseBody);
        }
        InfoFormat("{0} - {1} response for {2}", this, (uint)st, path);
        state = HttpStates.WRITING;
        new StreamPump(rs, stream, (pump, result) =>
        {
          pump.Input.Close();
          pump.Input.Dispose();
          if (result == StreamPumpResult.Delivered) {
            DebugFormat("{0} - Done writing response", this);

            string conn;
            if (headers.TryGetValue("connection", out conn) && conn.ToLower() == "keep-alive") {
              ReadNext();
              return;
            }
          }
          Close();
        }, BUFFER_SIZE);
      }
      catch (Exception) {
        rs.Dispose();
        throw;
      }
    }

    private void SetupResponse()
    {
      State = HttpStates.WRITEBEGIN;
      try {
        var handler = owner.FindHandler(path);
        if (handler == null) {
          throw new Http404Exception();
        }
        response = handler.HandleRequest(this);
        if (response == null) {
          throw new ArgumentException("Handler did not return a response");
        }
      }
      catch (Http404Exception ex) {
        Info(String.Format("{0} - Got a 404: {1}", this, this.path), ex);
        response = Error404.HandleRequest(this);
      }
      catch (Exception ex) {
        Warn(String.Format("{0} - Failed to process response", this), ex);
        response = Error500.HandleRequest(this);
      }
      SendResponse();
    }

    internal void Close()
    {
      State = HttpStates.CLOSED;

      DebugFormat("{0} - Closing connection after {1} requests", this, requestCount);
      try {
        client.Close();
      }
      catch (Exception) { }
      owner.RemoveClient(this);
    }
  }
}
