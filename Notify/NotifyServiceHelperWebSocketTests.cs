using AGVSystemCommonNet6.Notify;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace AGVSystemTests.Notify
{
    /// <summary>
    /// NotifyServiceHelper.WebsocketNotification（GET api/event）：
    /// 每條連線只有一個 pending ReceiveAsync（不論通知幾則）、依序送出、有界 Channel DropOldest、SendAsync 逾時、
    /// 任何結束路徑都取消訂閱 OnMessage 並讓 ActiveConnections 回到原值、斷線後重新連線可正常收通知。
    /// 以假 WebSocket 在同一個行程內驗證，不需要網路。
    /// </summary>
    [TestClass]
    public class NotifyServiceHelperWebSocketTests
    {
        /// <summary>模擬瀏覽器端：從不送資料，只會 Close 或突然斷線。</summary>
        private sealed class FakeWebSocket : WebSocket
        {
            private volatile int _state = (int)WebSocketState.Open;
            private readonly TaskCompletionSource<WebSocketReceiveResult> _peer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _pendingReceives;
            private int _maxConcurrentReceives;
            private int _receiveCalls;
            private int _sendAttempts;
            private readonly ConcurrentQueue<string> _sent = new();

            public Func<int, CancellationToken, Task>? SendBehavior; // 參數：第幾次送出（從 1 開始）
            public int CloseOutputCalls;
            public bool Disposed;
            public readonly TaskCompletionSource FirstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int ReceiveCalls => Volatile.Read(ref _receiveCalls);
            public int MaxConcurrentReceives => Volatile.Read(ref _maxConcurrentReceives);
            public int PendingReceives => Volatile.Read(ref _pendingReceives);
            public int SendAttempts => Volatile.Read(ref _sendAttempts);
            public List<string> Sent => _sent.ToList();

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override string? SubProtocol => null;
            public override WebSocketState State => (WebSocketState)_state;

            public void PeerSendsClose()
            {
                _state = (int)WebSocketState.CloseReceived;
                _peer.TrySetResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, ""));
            }

            /// <summary>不送 Close 就斷線（kill 行程／拔網路線）。</summary>
            public void PeerDropsConnection()
            {
                _state = (int)WebSocketState.Aborted;
                _peer.TrySetException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
            }

            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _receiveCalls);
                int pending = Interlocked.Increment(ref _pendingReceives);
                int max;
                while (pending > (max = Volatile.Read(ref _maxConcurrentReceives)) && Interlocked.CompareExchange(ref _maxConcurrentReceives, pending, max) != max) { }
                try
                {
                    var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
                    var done = await Task.WhenAny(_peer.Task, cancelled).ConfigureAwait(false);
                    if (done == cancelled)
                    {
                        _state = (int)WebSocketState.Aborted; // 與 ManagedWebSocket 相同：取消 receive 會 abort
                        throw new OperationCanceledException(cancellationToken);
                    }
                    return await _peer.Task.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingReceives);
                }
            }

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                int n = Interlocked.Increment(ref _sendAttempts);
                FirstSendStarted.TrySetResult();
                if (State != WebSocketState.Open)
                    throw new WebSocketException(WebSocketError.InvalidState);
                if (SendBehavior != null)
                    await SendBehavior(n, cancellationToken).ConfigureAwait(false);
                _sent.Enqueue(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref CloseOutputCalls);
                _state = State == WebSocketState.CloseReceived ? (int)WebSocketState.Closed : (int)WebSocketState.CloseSent;
                return Task.CompletedTask;
            }

            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
                => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

            public override void Abort() => _state = (int)WebSocketState.Aborted;

            public override void Dispose()
            {
                Disposed = true;
                if (State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                    _state = (int)WebSocketState.Closed;
            }
        }

        private sealed class FakeWebSocketFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public FakeWebSocketFeature(WebSocket socket) => _socket = socket;
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
        }

        private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("逾時：" + because);
                await Task.Delay(10);
            }
        }

        private static string Msg(string json) => JsonConvert.DeserializeObject<NotifyServiceHelper.NotifyMessage>(json)!.message;

        private int _baseConnections;
        private int _baseHandlers;

        [TestInitialize]
        public void Setup()
        {
            _baseConnections = NotifyServiceHelper.ActiveConnections;
            _baseHandlers = NotifyServiceHelper.OnMessageHandlerCount;
        }

        private Task StartConnection(FakeWebSocket ws, CancellationToken aborted = default, TimeSpan? sendTimeout = null, int capacity = NotifyServiceHelper.OutboxCapacity)
            => NotifyServiceHelper.RunConnectionAsync(ws, aborted, sendTimeout ?? NotifyServiceHelper.DefaultSendTimeout, capacity);

        private async Task AssertConnectionGone(Task connection, string because)
        {
            var finished = await Task.WhenAny(connection, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(connection, finished, "連線處理沒有結束：" + because);
            await connection; // 不可丟例外
            Assert.AreEqual(_baseConnections, NotifyServiceHelper.ActiveConnections, "ActiveConnections 要回到原值：" + because);
            Assert.AreEqual(_baseHandlers, NotifyServiceHelper.OnMessageHandlerCount, "OnMessage 要取消訂閱：" + because);
        }

        [TestMethod]
        public async Task ManyNotifications_ExactlyOneReceivePerConnection_SentInOrder()
        {
            var ws = new FakeWebSocket();
            var connection = StartConnection(ws);
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 1, "連線建立");
            Assert.AreEqual(_baseHandlers + 1, NotifyServiceHelper.OnMessageHandlerCount);

            for (int i = 0; i < 200; i++)
                await NotifyServiceHelper.INFO($"n{i}");
            await WaitUntil(() => ws.Sent.Count == 200, "200 則通知送達");

            Assert.AreEqual(1, ws.ReceiveCalls, "修改前每則通知都會呼叫一次 ReceiveAsync（永遠不會完成，持續累積）");
            Assert.AreEqual(1, ws.MaxConcurrentReceives);
            CollectionAssert.AreEqual(Enumerable.Range(0, 200).Select(i => $"n{i}").ToArray(), ws.Sent.Select(Msg).ToArray(), "依序送出");
            var first = JsonConvert.DeserializeObject<Dictionary<string, object>>(ws.Sent[0])!;
            CollectionAssert.AreEquivalent(new[] { "type", "message", "show" }, first.Keys.ToArray(), "前端 JSON 格式不變");

            ws.PeerSendsClose();
            await AssertConnectionGone(connection, "對方 Close");
            Assert.AreEqual(1, ws.CloseOutputCalls, "回應 Close");
            Assert.AreEqual(0, ws.PendingReceives);
        }

        [TestMethod]
        public async Task SendFailure_UnsubscribesAndEndsConnection_LaterNotificationsIgnored()
        {
            var ws = new FakeWebSocket { SendBehavior = (_, _) => throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely) };
            var connection = StartConnection(ws);
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 1, "連線建立");

            await NotifyServiceHelper.WARNING("boom");
            await AssertConnectionGone(connection, "Send 失敗（修改前只 Dispose，不取消訂閱、也不結束 request）");

            for (int i = 0; i < 10; i++)
                await NotifyServiceHelper.INFO("after");
            Assert.AreEqual(1, ws.SendAttempts, "已結束的連線不會再被嘗試送出");
            Assert.AreEqual(0, ws.PendingReceives, "receive 迴圈也結束了");
        }

        [TestMethod]
        public async Task SendTimeout_EndsConnection()
        {
            var ws = new FakeWebSocket { SendBehavior = (_, token) => Task.Delay(Timeout.Infinite, token) };
            var connection = StartConnection(ws, sendTimeout: TimeSpan.FromMilliseconds(200));
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 1, "連線建立");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await NotifyServiceHelper.INFO("stuck");
            await AssertConnectionGone(connection, "SendAsync 逾時");
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
        }

        [TestMethod]
        public void DefaultSendTimeout_Is5Seconds_AndCapacity256()
        {
            Assert.AreEqual(TimeSpan.FromSeconds(5), NotifyServiceHelper.DefaultSendTimeout);
            Assert.AreEqual(256, NotifyServiceHelper.OutboxCapacity);
        }

        [TestMethod]
        public async Task SlowClient_BoundedOutbox_DropsOldest_KeepsNewest256()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ws = new FakeWebSocket { SendBehavior = (n, token) => n == 1 ? gate.Task.WaitAsync(token) : Task.CompletedTask };
            var connection = StartConnection(ws, sendTimeout: TimeSpan.FromSeconds(30));
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 1, "連線建立");

            await NotifyServiceHelper.INFO("m0");
            await ws.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // m0 卡在送出中
            for (int i = 1; i < 300; i++)
                await NotifyServiceHelper.INFO($"m{i}");                         // 299 則排隊，上限 256
            gate.SetResult();

            await WaitUntil(() => ws.Sent.Count == 257, "1 則送出中 + 256 則排隊");
            await Task.Delay(100);
            var sent = ws.Sent.Select(Msg).ToList();
            Assert.AreEqual(257, sent.Count, "超過上限的不會無限累積");
            Assert.AreEqual("m0", sent[0]);
            Assert.AreEqual("m44", sent[1], "最舊的 m1..m43 被丟掉");
            Assert.AreEqual("m299", sent[^1], "最新的一定保留");
            CollectionAssert.AreEqual(Enumerable.Range(44, 256).Select(i => $"m{i}").ToArray(), sent.Skip(1).ToArray());

            ws.PeerSendsClose();
            await AssertConnectionGone(connection, "Close");
        }

        [TestMethod]
        public async Task RequestAborted_EndsConnection_CancelsPendingReceive()
        {
            using var aborted = new CancellationTokenSource();
            var ws = new FakeWebSocket();
            var connection = StartConnection(ws, aborted.Token);
            await WaitUntil(() => ws.PendingReceives == 1, "receive 迴圈啟動");

            aborted.Cancel();
            await AssertConnectionGone(connection, "HttpContext.RequestAborted");
            Assert.AreEqual(0, ws.PendingReceives, "pending receive 被取消，不會殘留");
        }

        [TestMethod]
        public async Task PeerDropsWithoutClose_NoNotificationNeeded_ConnectionEnds()
        {
            var ws = new FakeWebSocket();
            var connection = StartConnection(ws);
            await WaitUntil(() => ws.PendingReceives == 1, "receive 迴圈啟動");

            ws.PeerDropsConnection();
            await AssertConnectionGone(connection, "修改前要等到下一則通知才會發現斷線");
            Assert.AreEqual(0, ws.SendAttempts);
        }

        [TestMethod]
        public async Task Reconnect_AfterDisconnect_NewConnectionWorks_CountersReturnToBaseline()
        {
            var first = new FakeWebSocket();
            var c1 = StartConnection(first);
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 1, "第一條連線");
            await NotifyServiceHelper.INFO("before");
            await WaitUntil(() => first.Sent.Count == 1, "第一條收到");
            first.PeerDropsConnection();
            await AssertConnectionGone(c1, "第一條斷線");

            var second = new FakeWebSocket(); // 前端 onclose 1 秒後重新連線
            var c2 = StartConnection(second);
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 1, "重新連線");
            await NotifyServiceHelper.SUCCESS("after");
            await WaitUntil(() => second.Sent.Count == 1, "新連線收到通知");
            Assert.AreEqual("after", Msg(second.Sent[0]));
            Assert.AreEqual(1, first.Sent.Count, "舊連線不會再收到");

            second.PeerSendsClose();
            await AssertConnectionGone(c2, "第二條關閉");
        }

        [TestMethod]
        public async Task ReconnectStorm_200Connections_HalfDroppedWithoutClose_NoLeak()
        {
            var tasks = new List<Task>();
            var sockets = new List<FakeWebSocket>();
            for (int i = 0; i < 200; i++)
            {
                var ws = new FakeWebSocket();
                sockets.Add(ws);
                tasks.Add(StartConnection(ws));
            }
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 200, "200 條連線");

            var notifier = Task.Run(async () => { for (int i = 0; i < 50; i++) { await NotifyServiceHelper.INFO($"s{i}"); await Task.Yield(); } });
            for (int i = 0; i < sockets.Count; i++)
            {
                if (i % 2 == 0) sockets[i].PeerDropsConnection();
                else sockets[i].PeerSendsClose();
            }
            await notifier;

            var all = Task.WhenAll(tasks);
            Assert.AreSame(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.AreEqual(_baseConnections, NotifyServiceHelper.ActiveConnections);
            Assert.AreEqual(_baseHandlers, NotifyServiceHelper.OnMessageHandlerCount);
            Assert.IsTrue(sockets.All(s => s.ReceiveCalls == 1 && s.PendingReceives == 0), "每條連線只有一個 receive，且都已結束");
        }

        [TestMethod]
        public async Task WebsocketNotification_HttpEntryPoint_UsesRequestAborted_AndDisposesSocket()
        {
            var ws = new FakeWebSocket();
            using var aborted = new CancellationTokenSource();
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(ws));
            context.RequestAborted = aborted.Token;

            var request = NotifyServiceHelper.WebsocketNotification(context);
            await WaitUntil(() => NotifyServiceHelper.ActiveConnections == _baseConnections + 1, "Accept 後註冊");
            await NotifyServiceHelper.ERROR("hello");
            await WaitUntil(() => ws.Sent.Count == 1, "收到通知");

            aborted.Cancel();
            await AssertConnectionGone(request, "瀏覽器分頁關閉（RequestAborted）");
            Assert.IsTrue(ws.Disposed, "socket 一定被 Dispose");
        }

        [TestMethod]
        public async Task WebsocketNotification_NonWebSocketRequest_Returns400()
        {
            var context = new DefaultHttpContext();
            await NotifyServiceHelper.WebsocketNotification(context);
            Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
            Assert.AreEqual(_baseHandlers, NotifyServiceHelper.OnMessageHandlerCount);
        }

        [TestMethod]
        public void LogConnectionSummary_DoesNotThrow()
        {
            NotifyServiceHelper.LogConnectionSummary();
        }
    }
}
