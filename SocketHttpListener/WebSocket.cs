using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SocketHttpListener.Net.WebSockets;
using HttpStatusCode = SocketHttpListener.Net.HttpStatusCode;
using WebSocketState = System.Net.WebSockets.WebSocketState;

namespace SocketHttpListener
{
    /// <summary>
    /// Implements the WebSocket interface.
    /// </summary>
    /// <remarks>
    /// The WebSocket class provides a set of methods and properties for two-way communication using
    /// the WebSocket protocol (<see href="http://tools.ietf.org/html/rfc6455">RFC 6455</see>).
    /// </remarks>
    public class WebSocket : IDisposable
    {
        #region Private Fields

        private Action _closeContext;
        private CompressionMethod _compression;
        private WebSocketContext _context;
        private CookieCollection _cookies;
        private AutoResetEvent _exitReceiving;
        private object _forConn;
        private object _forEvent;
        private object _forMessageEventQueue;
        private object _forSend;
        private const string _guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private Queue<MessageEventArgs> _messageEventQueue;
        private string _protocol;
        private volatile WebSocketState _readyState;
        private AutoResetEvent _receivePong;
        private bool _secure;
        private Stream _stream;
        private const string _version = "13";

        #endregion

        #region Internal Fields

        internal const int FragmentLength = 1016; // Max value is int.MaxValue - 14.

        #endregion

        #region Internal Constructors

        // As server
        internal WebSocket(string protocol)
        {
            _protocol = protocol;
        }

        public void SetContext(HttpListenerWebSocketContext context, Action closeContextFn, Stream stream)
        {
            _context = context;

            _closeContext = closeContextFn;
            _secure = context.IsSecureConnection;
            _stream = stream;

            init();
        }

        // In the .NET Framework, this pulls the value from a P/Invoke.  Here we just hardcode it to a reasonable default.
        public static TimeSpan DefaultKeepAliveInterval => TimeSpan.FromSeconds(30);

        #endregion

        /// <summary>
        /// Gets the state of the WebSocket connection.
        /// </summary>
        /// <value>
        /// One of the <see cref="WebSocketState"/> enum values, indicates the state of the WebSocket
        /// connection. The default value is <see cref="WebSocketState.Connecting"/>.
        /// </value>
        public WebSocketState ReadyState => _readyState;

        #region Public Events

        /// <summary>
        /// Occurs when the WebSocket connection has been closed.
        /// </summary>
        public event EventHandler<CloseEventArgs> OnClose;

        /// <summary>
        /// Occurs when the <see cref="WebSocket"/> gets an error.
        /// </summary>
        public event EventHandler<ErrorEventArgs> OnError;

        /// <summary>
        /// Occurs when the <see cref="WebSocket"/> receives a message.
        /// </summary>
        public event EventHandler<MessageEventArgs> OnMessage;

        /// <summary>
        /// Occurs when the WebSocket connection has been established.
        /// </summary>
        public event EventHandler OnOpen;

        #endregion

        #region Private Methods

        private void close(CloseStatusCode code, string reason, bool wait)
        {
            close(new PayloadData(((ushort)code).Append(reason)), !code.IsReserved(), wait);
        }

        private void close(PayloadData payload, bool send, bool wait)
        {
            lock (_forConn)
            {
                if (_readyState == WebSocketState.CloseSent || _readyState == WebSocketState.Closed)
                {
                    return;
                }

                _readyState = WebSocketState.CloseSent;
            }

            var e = new CloseEventArgs(payload);
            e.WasClean =
              closeHandshake(
                  send ? WebSocketFrame.CreateCloseFrame(Mask.Unmask, payload).ToByteArray() : null,
                  wait ? 1000 : 0);

            _readyState = WebSocketState.Closed;
            try
            {
                OnClose.Emit(this, e);
            }
            catch (Exception ex)
            {
                error("An exception has occurred while OnClose.", ex);
            }
        }

        private bool closeHandshake(byte[] frameAsBytes, int millisecondsTimeout)
        {
            var sent = frameAsBytes != null && writeBytes(frameAsBytes);
            var received =
              millisecondsTimeout == 0 ||
              (sent && _exitReceiving != null && _exitReceiving.WaitOne(millisecondsTimeout));

            closeServerResources();

            if (_receivePong != null)
            {
                _receivePong.Dispose();
                _receivePong = null;
            }

            if (_exitReceiving != null)
            {
                _exitReceiving.Dispose();
                _exitReceiving = null;
            }

            var result = sent && received;

            return result;
        }

        // As server
        private void closeServerResources()
        {
            if (_closeContext == null)
                return;

            try
            {
                _closeContext();
            }
            catch (SocketException)
            {
                // it could be unable to send the handshake response
            }

            _closeContext = null;
            _stream = null;
            _context = null;
        }

        private bool concatenateFragmentsInto(Stream dest)
        {
            while (true)
            {
                var frame = WebSocketFrame.Read(_stream, true);
                if (frame.IsFinal)
                {
                    /* FINAL */

                    // CONT
                    if (frame.IsContinuation)
                    {
                        dest.WriteBytes(frame.PayloadData.ApplicationData);
                        break;
                    }

                    // PING
                    if (frame.IsPing)
                    {
                        processPingFrame(frame);
                        continue;
                    }

                    // PONG
                    if (frame.IsPong)
                    {
                        processPongFrame(frame);
                        continue;
                    }

                    // CLOSE
                    if (frame.IsClose)
                        return processCloseFrame(frame);
                }
                else
                {
                    /* MORE */

                    // CONT
                    if (frame.IsContinuation)
                    {
                        dest.WriteBytes(frame.PayloadData.ApplicationData);
                        continue;
                    }
                }

                // ?
                return processUnsupportedFrame(
                  frame,
                  CloseStatusCode.IncorrectData,
                  "An incorrect data has been received while receiving fragmented data.");
            }

            return true;
        }

        // As server
        private HttpResponse createHandshakeCloseResponse(HttpStatusCode code)
        {
            var res = HttpResponse.CreateCloseResponse(code);
            res.Headers["Sec-WebSocket-Version"] = _version;

            return res;
        }

        private MessageEventArgs dequeueFromMessageEventQueue()
        {
            lock (_forMessageEventQueue)
                return _messageEventQueue.Count > 0
                       ? _messageEventQueue.Dequeue()
                       : null;
        }

        private void enqueueToMessageEventQueue(MessageEventArgs e)
        {
            lock (_forMessageEventQueue)
                _messageEventQueue.Enqueue(e);
        }

        private void error(string message, Exception exception)
        {
            try
            {
                if (exception != null)
                {
                    message += ". Exception.Message: " + exception.Message;
                }
                OnError.Emit(this, new ErrorEventArgs(message));
            }
            catch (Exception)
            {
            }
        }

        private void error(string message)
        {
            try
            {
                OnError.Emit(this, new ErrorEventArgs(message));
            }
            catch (Exception)
            {
            }
        }

        private void init()
        {
            _compression = CompressionMethod.None;
            _cookies = new CookieCollection();
            _forConn = new object();
            _forEvent = new object();
            _forSend = new object();
            _messageEventQueue = new Queue<MessageEventArgs>();
            _forMessageEventQueue = ((ICollection)_messageEventQueue).SyncRoot;
            _readyState = WebSocketState.Connecting;
        }

        private void open()
        {
            try
            {
                startReceiving();

                lock (_forEvent)
                {
                    try
                    {
                        if (OnOpen != null)
                        {
                            OnOpen(this, EventArgs.Empty);
                        }
                    }
                    catch (Exception ex)
                    {
                        processException(ex, "An exception has occurred while OnOpen.");
                    }
                }
            }
            catch (Exception ex)
            {
                processException(ex, "An exception has occurred while opening.");
            }
        }

        private bool processCloseFrame(WebSocketFrame frame)
        {
            var payload = frame.PayloadData;
            close(payload, !payload.ContainsReservedCloseStatusCode, false);

            return false;
        }

        private bool processDataFrame(WebSocketFrame frame)
        {
            var e = frame.IsCompressed
                    ? new MessageEventArgs(
                        frame.Opcode, frame.PayloadData.ApplicationData.Decompress(_compression))
                    : new MessageEventArgs(frame.Opcode, frame.PayloadData);

            enqueueToMessageEventQueue(e);
            return true;
        }

        private void processException(Exception exception, string message)
        {
            var code = CloseStatusCode.Abnormal;
            var reason = message;
            if (exception is WebSocketException)
            {
                var wsex = (WebSocketException)exception;
                code = wsex.Code;
                reason = wsex.Message;
            }

            error(message ?? code.GetMessage(), exception);
            if (_readyState == WebSocketState.Connecting)
                Close(HttpStatusCode.BadRequest);
            else
                close(code, reason ?? code.GetMessage(), false);
        }

        private bool processFragmentedFrame(WebSocketFrame frame)
        {
            return frame.IsContinuation // Not first fragment
                   ? true
                   : processFragments(frame);
        }

        private bool processFragments(WebSocketFrame first)
        {
            using (var buff = new MemoryStream())
            {
                buff.WriteBytes(first.PayloadData.ApplicationData);
                if (!concatenateFragmentsInto(buff))
                    return false;

                byte[] data;
                if (_compression != CompressionMethod.None)
                {
                    data = buff.DecompressToArray(_compression);
                }
                else
                {
                    data = buff.ToArray();
                }

                enqueueToMessageEventQueue(new MessageEventArgs(first.Opcode, data));
                return true;
            }
        }

        private bool processPingFrame(WebSocketFrame frame)
        {
            return true;
        }

        private bool processPongFrame(WebSocketFrame frame)
        {
            _receivePong.Set();

            return true;
        }

        private bool processUnsupportedFrame(WebSocketFrame frame, CloseStatusCode code, string reason)
        {
            processException(new WebSocketException(code, reason), null);

            return false;
        }

        private bool processWebSocketFrame(WebSocketFrame frame)
        {
            return frame.IsCompressed && _compression == CompressionMethod.None
                   ? processUnsupportedFrame(
                       frame,
                       CloseStatusCode.IncorrectData,
                       "A compressed data has been received without available decompression method.")
                   : frame.IsFragmented
                     ? processFragmentedFrame(frame)
                     : frame.IsData
                       ? processDataFrame(frame)
                       : frame.IsPing
                         ? processPingFrame(frame)
                         : frame.IsPong
                           ? processPongFrame(frame)
                           : frame.IsClose
                             ? processCloseFrame(frame)
                             : processUnsupportedFrame(frame, CloseStatusCode.PolicyViolation, null);
        }

        private bool send(Opcode opcode, Stream stream)
        {
            lock (_forSend)
            {
                var src = stream;
                var compressed = false;
                var sent = false;
                try
                {
                    if (_compression != CompressionMethod.None)
                    {
                        stream = stream.Compress(_compression);
                        compressed = true;
                    }

                    sent = send(opcode, Mask.Unmask, stream, compressed);
                    if (!sent)
                        error("Sending a data has been interrupted.");
                }
                catch (Exception ex)
                {
                    error("An exception has occurred while sending a data.", ex);
                }
                finally
                {
                    if (compressed)
                        stream.Dispose();

                    src.Dispose();
                }

                return sent;
            }
        }

        private bool send(Opcode opcode, Mask mask, Stream stream, bool compressed)
        {
            var len = stream.Length;

            /* Not fragmented */

            if (len == 0)
                return send(Fin.Final, opcode, mask, new byte[0], compressed);

            var quo = len / FragmentLength;
            var rem = (int)(len % FragmentLength);

            byte[] buff = null;
            if (quo == 0)
            {
                buff = new byte[rem];
                return stream.Read(buff, 0, rem) == rem &&
                       send(Fin.Final, opcode, mask, buff, compressed);
            }

            buff = new byte[FragmentLength];
            if (quo == 1 && rem == 0)
                return stream.Read(buff, 0, FragmentLength) == FragmentLength &&
                       send(Fin.Final, opcode, mask, buff, compressed);

            /* Send fragmented */

            // Begin
            if (stream.Read(buff, 0, FragmentLength) != FragmentLength ||
                !send(Fin.More, opcode, mask, buff, compressed))
                return false;

            var n = rem == 0 ? quo - 2 : quo - 1;
            for (long i = 0; i < n; i++)
                if (stream.Read(buff, 0, FragmentLength) != FragmentLength ||
                    !send(Fin.More, Opcode.Cont, mask, buff, compressed))
                    return false;

            // End
            if (rem == 0)
                rem = FragmentLength;
            else
                buff = new byte[rem];

            return stream.Read(buff, 0, rem) == rem &&
                   send(Fin.Final, Opcode.Cont, mask, buff, compressed);
        }

        private bool send(Fin fin, Opcode opcode, Mask mask, byte[] data, bool compressed)
        {
            lock (_forConn)
            {
                if (_readyState != WebSocketState.Open)
                {
                    return false;
                }

                return writeBytes(
                  WebSocketFrame.CreateWebSocketFrame(fin, opcode, mask, data, compressed).ToByteArray());
            }
        }

        private Task sendAsync(Opcode opcode, Stream stream)
        {
            var completionSource = new TaskCompletionSource<bool>();
            Task.Run(() =>
           {
               try
               {
                   send(opcode, stream);
                   completionSource.TrySetResult(true);
               }
               catch (Exception ex)
               {
                   completionSource.TrySetException(ex);
               }
           });
            return completionSource.Task;
        }

        // As server
        private bool sendHttpResponse(HttpResponse response)
        {
            return writeBytes(response.ToByteArray());
        }

        private void startReceiving()
        {
            if (_messageEventQueue.Count > 0)
                _messageEventQueue.Clear();

            _exitReceiving = new AutoResetEvent(false);
            _receivePong = new AutoResetEvent(false);

            Action receive = null;
            receive = () => WebSocketFrame.ReadAsync(
              _stream,
              true,
              frame =>
              {
                  if (processWebSocketFrame(frame) && _readyState != WebSocketState.Closed)
                  {
                      receive();

                      if (!frame.IsData)
                          return;

                      lock (_forEvent)
                      {
                          try
                          {
                              var e = dequeueFromMessageEventQueue();
                              if (e != null && _readyState == WebSocketState.Open)
                                  OnMessage.Emit(this, e);
                          }
                          catch (Exception ex)
                          {
                              processException(ex, "An exception has occurred while OnMessage.");
                          }
                      }
                  }
                  else if (_exitReceiving != null)
                  {
                      _exitReceiving.Set();
                  }
              },
              ex => processException(ex, "An exception has occurred while receiving a message."));

            receive();
        }

        private bool writeBytes(byte[] data)
        {
            try
            {
                _stream.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region Internal Methods

        // As server
        internal void Close(HttpResponse response)
        {
            _readyState = WebSocketState.CloseSent;
            sendHttpResponse(response);

            closeServerResources();

            _readyState = WebSocketState.Closed;
        }

        // As server
        internal void Close(HttpStatusCode code)
        {
            Close(createHandshakeCloseResponse(code));
        }

        // As server
        public void ConnectAsServer()
        {
            try
            {
                _readyState = WebSocketState.Open;
                open();
            }
            catch (Exception ex)
            {
                processException(ex, "An exception has occurred while connecting.");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Closes the WebSocket connection, and releases all associated resources.
        /// </summary>
        public void Close()
        {
            var msg = _readyState.CheckIfClosable();
            if (msg != null)
            {
                error(msg);

                return;
            }

            var send = _readyState == WebSocketState.Open;
            close(new PayloadData(), send, send);
        }

        /// <summary>
        /// Closes the WebSocket connection with the specified <see cref="CloseStatusCode"/>
        /// and <see cref="string"/>, and releases all associated resources.
        /// </summary>
        /// <remarks>
        /// This method emits a <see cref="OnError"/> event if the size
        /// of <paramref name="reason"/> is greater than 123 bytes.
        /// </remarks>
        /// <param name="code">
        /// One of the <see cref="CloseStatusCode"/> enum values, represents the status code
        /// indicating the reason for the close.
        /// </param>
        /// <param name="reason">
        /// A <see cref="string"/> that represents the reason for the close.
        /// </param>
        public void Close(CloseStatusCode code, string reason)
        {
            byte[] data = null;
            var msg = _readyState.CheckIfClosable() ??
                      (data = ((ushort)code).Append(reason)).CheckIfValidControlData("reason");

            if (msg != null)
            {
                error(msg);

                return;
            }

            var send = _readyState == WebSocketState.Open && !code.IsReserved();
            close(new PayloadData(data), send, send);
        }

        /// <summary>
        /// Sends a binary <paramref name="data"/> asynchronously using the WebSocket connection.
        /// </summary>
        /// <remarks>
        /// This method doesn't wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        public Task SendAsync(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var msg = _readyState.CheckIfOpen();
            if (msg != null)
            {
                throw new Exception(msg);
            }

            return sendAsync(Opcode.Binary, new MemoryStream(data));
        }

        /// <summary>
        /// Sends a text <paramref name="data"/> asynchronously using the WebSocket connection.
        /// </summary>
        /// <remarks>
        /// This method doesn't wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        public Task SendAsync(string data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var msg = _readyState.CheckIfOpen();
            if (msg != null)
            {
                throw new Exception(msg);
            }

            return sendAsync(Opcode.Text, new MemoryStream(Encoding.UTF8.GetBytes(data)));
        }

        #endregion

        #region Explicit Interface Implementation

        /// <summary>
        /// Closes the WebSocket connection, and releases all associated resources.
        /// </summary>
        /// <remarks>
        /// This method closes the WebSocket connection with <see cref="CloseStatusCode.Away"/>.
        /// </remarks>
        void IDisposable.Dispose()
        {
            Close(CloseStatusCode.Away, null);
        }

        #endregion
    }
}
