﻿using Newtonsoft.Json.Linq;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.BitGet.BitGetFutures.Entity;
using OsEngine.Market.Servers.Entity;
using OsEngine.Market.Servers.ZB.EntityCreators;
using OsEngine.Market.Services;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.ZB
{
    public sealed class ZbServer : AServer
    {
        public ZbServer()
        {
            ServerRealization = new ZbServerRealization();
            CreateParameterString(OsLocalization.Market.ServerParamPublicKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParameterSecretKey, "");
        }

        /// <summary>
        /// instrument history query
        /// запрос истории по инструменту
        /// </summary>
        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        {
            return ((ZbServerRealization)ServerRealization).GetCandleHistory(nameSec, tf);
        }
    }

    public sealed class ZbServerRealization : IServerRealization
    {
        private string _publicKey;
        private string _secretKey;

        private WsSource _wsSource;

        private const string WsUri = "wss://api.zb.cn/websocket";

        private readonly ZbSecurityCreator _securitiesCreator;
        private readonly ZbPortfolioCreator _portfoliosCreator;
        private readonly ZbMarketDepthCreator _marketDepthCreator;
        private readonly ZbTradesCreator _tradesCreator;
        private readonly ZbOrderCreator _orderCreator;

        private readonly List<Order> _trackedOrders = new List<Order>();

        private readonly ConcurrentQueue<string> _queueMessagesReceivedFromExchange = new ConcurrentQueue<string>();

        private CancellationTokenSource _cancelTokenSource;

        public ServerType ServerType { get { return ServerType.Zb; } }
        public ServerConnectStatus ServerStatus { get; set; }
        public List<IServerParameter> ServerParameters { get; set; }
        public DateTime ServerTime { get; set; }

        /// <summary>
        /// словарь таймфреймов, поддерживаемых этой биржей
        /// </summary>
        private readonly Dictionary<int, string> _supportedIntervals;

        private const string UriForCandles = "http://api.zb.plus/data/v1/kline?";
        public ZbServerRealization()
        {
            ServerStatus = ServerConnectStatus.Disconnect;

            _securitiesCreator = new ZbSecurityCreator();
            _portfoliosCreator = new ZbPortfolioCreator("Zb Wallet");
            _marketDepthCreator = new ZbMarketDepthCreator();
            _tradesCreator = new ZbTradesCreator();
            _orderCreator = new ZbOrderCreator();

            _supportedIntervals = CreateIntervalDictionary();
        }

        private Dictionary<int, string> CreateIntervalDictionary()
        {
            var dictionary = new Dictionary<int, string>();

            dictionary.Add(1, "1min");
            dictionary.Add(3, "3min");
            dictionary.Add(5, "5min");
            dictionary.Add(15, "15min");
            dictionary.Add(30, "30min");
            dictionary.Add(60, "1hour");
            dictionary.Add(120, "2hour");
            dictionary.Add(240, "4hour");
            dictionary.Add(360, "6hour");
            dictionary.Add(720, "12hour");
            dictionary.Add(1440, "1day");
            dictionary.Add(4320, "3day");
            dictionary.Add(100080, "1week");

            return dictionary;
        }

        public void Connect(WebProxy proxy)
        {
            try
            {
                Initialize();
                ServerStatus = ServerConnectStatus.Connect;
            }
            catch (Exception e)
            {
                SendLogMessage("Zb connect error: " + e.Message, LogMessageType.Error);
            }
        }

        private void Initialize()
        {
            _cancelTokenSource = new CancellationTokenSource();

            _publicKey = ((ServerParameterString)ServerParameters[0]).Value;
            _secretKey = ((ServerParameterPassword)ServerParameters[1]).Value;

            if (string.IsNullOrEmpty(_publicKey) || string.IsNullOrEmpty(_secretKey))
            {
                throw new ArgumentException("Invalid key, connection terminated!");
            }

            _orderCreator.NewTrackedOrder += OrderCreatorOnNewTrackedOrder;
            _orderCreator.DeleteTrackedOrder += OrderCreatorOnDeleteTrackedOrder;
            _orderCreator.NewMyTrade += OrderCreatorOnNewMyTrade;

            _wsSource = new WsSource(WsUri);
            _wsSource.MessageEvent += WsSourceOnMessageEvent;
            _wsSource.Start();

            StartMessageReader();
        }

        private void OrderCreatorOnNewTrackedOrder(Order order)
        {
            _trackedOrders.Add(order);
        }

        private void OrderCreatorOnDeleteTrackedOrder(Order order)
        {
            _trackedOrders.Remove(order);
        }

        private void OrderCreatorOnNewMyTrade(MyTrade myTrade)
        {
            MyTradeEvent(myTrade);
        }

        private void WsSourceOnMessageEvent(WsMessageType msgType, string data)
        {
            switch (msgType)
            {
                case WsMessageType.Opened:
                    ConnectEvent();
                    GetSecuritiesInfo();
                    StartOrderSender();
                    StartPortfolioRequester();
                    break;
                case WsMessageType.Closed:
                    DisconnectEvent();
                    break;
                case WsMessageType.StringData:
                    _queueMessagesReceivedFromExchange.Enqueue(data);
                    break;
                case WsMessageType.Error:
                    SendLogMessage(data, LogMessageType.Error);
                    break;
                default:
                    throw new NotSupportedException(data);
            }
        }

        private void GetSecuritiesInfo()
        {
            const string query = "{'event':'addChannel','channel':'markets'}";
            _wsSource.SendMessage(query);
        }

        private void StartOrderSender()
        {
            Task.Run(() => OrdersSender(_cancelTokenSource.Token), _cancelTokenSource.Token);
        }

        private async void OrdersSender(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, token);

                    if (_trackedOrders.Count != 0)
                    {
                        foreach (var trackedOrder in _trackedOrders)
                        {
                            GetOrderState(trackedOrder);
                        }
                    }
                    else
                    {
                        await Task.Delay(1000, token);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("MessageReader error: " + exception, LogMessageType.Error);
                }
            }
        }

        private void StartPortfolioRequester()
        {
            Task.Run(() => PortfolioRequester(_cancelTokenSource.Token), _cancelTokenSource.Token);
        }

        private async void PortfolioRequester(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, token);

                    GetPortfolios();
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("MessageReader error: " + exception, LogMessageType.Error);
                }
            }
        }

        private void GetOrderState(Order order)
        {
            JsonObject jsonContent = new JsonObject();

            jsonContent.Add("accesskey", _publicKey);
            jsonContent.Add("channel", order.SecurityNameCode + "_getorder");
            jsonContent.Add("event", "addChannel");
            jsonContent.Add("id", order.NumberMarket);
            jsonContent.Add("no", order.NumberUser);

            var sign = SingData(_secretKey, jsonContent.ToString());

            jsonContent.Add("sign", sign);

            _wsSource?.SendMessage(jsonContent.ToString());
        }

        public void GetAllActivOrders()
        {

        }

        public void GetOrderStatus(Order order)
        {

        }

        private void StartMessageReader()
        {
            Task.Run(() => MessageReader(_cancelTokenSource.Token), _cancelTokenSource.Token);
        }

        private async void MessageReader(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_queueMessagesReceivedFromExchange.IsEmpty)
                    {
                        string mes;

                        if (_queueMessagesReceivedFromExchange.TryDequeue(out mes))
                        {
                            var jt = JToken.Parse(mes);

                            var channel = jt.SelectToken("channel").Value<string>();

                            if (string.IsNullOrEmpty(channel))
                            {
                                continue;
                            }

                            if (channel == "getaccountinfo")
                            {
                                PortfolioEvent(_portfoliosCreator.Create(mes));
                            }
                            else if (channel == "markets")
                            {
                                SecurityEvent(_securitiesCreator.Create(mes));
                            }
                            else if (channel.EndsWith("_depth"))
                            {
                                MarketDepthEvent(_marketDepthCreator.Create(mes));
                            }
                            else if (channel.EndsWith("_trades"))
                            {
                                foreach (var trade in _tradesCreator.Create(mes))
                                {
                                    NewTradesEvent(trade);
                                }
                            }
                            else if (channel.EndsWith("_order") || channel.EndsWith("_getorder"))
                            {
                                var order = _orderCreator.Create(mes);

                                if (order != null) { MyOrderEvent(order); }
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(20);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("MessageReader error: " + exception, LogMessageType.Error);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_wsSource != null)
                {
                    UnInitialize();
                }

                if (_cancelTokenSource != null && !_cancelTokenSource.IsCancellationRequested)
                {
                    _cancelTokenSource.Cancel();
                }
            }
            catch (Exception e)
            {
                SendLogMessage("Zb dispose error: " + e, LogMessageType.Error);
            }
            ServerStatus = ServerConnectStatus.Disconnect;
        }

        private void UnInitialize()
        {
            _orderCreator.NewTrackedOrder -= OrderCreatorOnNewTrackedOrder;
            _orderCreator.DeleteTrackedOrder -= OrderCreatorOnDeleteTrackedOrder;
            _orderCreator.NewMyTrade -= OrderCreatorOnNewMyTrade;

            _wsSource.Dispose();
            _wsSource.MessageEvent -= WsSourceOnMessageEvent;
            _wsSource = null;
        }

        public void GetPortfolios()
        {
            JsonObject jsonContent = new JsonObject();

            jsonContent.Add("accesskey", _publicKey);
            jsonContent.Add("channel", "getaccountinfo");
            jsonContent.Add("event", "addChannel");
            jsonContent.Add("no", 1);

            var sing = SingData(_secretKey, jsonContent.ToString());

            jsonContent.Add("sign", sing);

            _wsSource.SendMessage(jsonContent.ToString());
        }

        public void GetSecurities() { }

        public void Subscrible(Security security)
        {
            SubscribeMarketDepth(security.Name);
            SubscribeTrades(security.Name);
        }

        private void SubscribeMarketDepth(string security)
        {
            JsonObject jsonContent = new JsonObject();
            jsonContent.Add("event", "addChannel");
            jsonContent.Add("channel", security + "_depth");
            _wsSource?.SendMessage(jsonContent.ToString());
        }

        private void SubscribeTrades(string security)
        {
            JsonObject jsonContent = new JsonObject();
            jsonContent.Add("event", "addChannel");
            jsonContent.Add("channel", security + "_trades");
            _wsSource?.SendMessage(jsonContent.ToString());
        }

        public void SendOrder(Order order)
        {
            JsonObject jsonContent = new JsonObject();

            jsonContent.Add("accesskey", _publicKey);
            jsonContent.Add("acctType", 0);
            jsonContent.Add("amount", order.Volume);
            jsonContent.Add("channel", order.SecurityNameCode + "_order");
            jsonContent.Add("event", "addChannel");
            jsonContent.Add("no", order.NumberUser);
            jsonContent.Add("price", order.Price);
            jsonContent.Add("tradeType", order.Side == Side.Buy ? 1 : 0);

            var sign = SingData(_secretKey, jsonContent.ToString());

            jsonContent.Add("sign", sign);

            _orderCreator.AddMyOrder(order);

            _wsSource?.SendMessage(jsonContent.ToString());
        }

        public void ChangeOrderPrice(Order order, decimal newPrice)
        {

        }

        public bool CancelOrder(Order order)
        {
            JsonObject jsonContent = new JsonObject();

            jsonContent.Add("accesskey", _publicKey);
            jsonContent.Add("channel", order.SecurityNameCode + "_cancelorder");
            jsonContent.Add("event", "addChannel");
            jsonContent.Add("id", order.NumberMarket);
            jsonContent.Add("no", order.NumberUser);

            var sign = SingData(_secretKey, jsonContent.ToString());

            jsonContent.Add("sign", sign);

            _wsSource?.SendMessage(jsonContent.ToString());
            return true;
        }

        private string SingData(string secretKey, string data)
        {
            byte[] keyInBytes = Encoding.UTF8.GetBytes(GetHashSecretKey(secretKey));
            byte[] dataInBytes = Encoding.UTF8.GetBytes(data);

            var md5 = new HMACMD5(keyInBytes);
            byte[] hash = md5.ComputeHash(dataInBytes);

            var result = BitConverter.ToString(hash).Replace("-", string.Empty).ToLower();

            return result;
        }

        private string GetHashSecretKey(string secretKey)
        {
            byte[] computedHash;

            using (var hash = new SHA1CryptoServiceProvider())
            {
                computedHash = hash.ComputeHash(Encoding.UTF8.GetBytes(secretKey));
            }

            var sb = new StringBuilder();

            foreach (byte b in computedHash) sb.AppendFormat("{0:x2}", b);

            return sb.ToString();
        }

        public List<Candle> GetCandleHistory(string security, TimeSpan interval)
        {
            int oldInterval = Convert.ToInt32(interval.TotalMinutes);

            var needIntervalForQuery =
                CandlesCreator.DetermineAppropriateIntervalForRequest(oldInterval, _supportedIntervals, out var needInterval);

            var jsonCandles = RequestCandlesFromExchange(needIntervalForQuery, security);

            var oldCandles = CreateCandlesFromJson(jsonCandles);

            if (oldInterval == needInterval)
            {
                return oldCandles;
            }

            var newCandles = CandlesCreator.CreateCandlesRequiredInterval(needInterval, oldInterval, oldCandles);

            return newCandles;
        }

        private string RequestCandlesFromExchange(string needIntervalForQuery, string security)
        {
            Uri uri = new Uri(UriForCandles + $"market={security}&type={needIntervalForQuery}");

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            var httpWebRequest = (HttpWebRequest)WebRequest.Create(uri);

            HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse();

            if (httpWebResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception("Failed to get candles on the instrument " + security);
            }

            string responseMsg;

            using (var stream = httpWebResponse.GetResponseStream())
            {
                using (StreamReader reader = new StreamReader(stream ?? throw new InvalidOperationException()))
                {
                    responseMsg = reader.ReadToEnd();
                }
            }

            httpWebResponse.Close();

            return responseMsg;
        }

        private List<Candle> CreateCandlesFromJson(string jsonCandles)
        {
            var candles = new List<Candle>();

            var js = JToken.Parse(jsonCandles);

            foreach (var jtCandle in js["data"])
            {
                var candle = new Candle();

                candle.TimeStart = TimeManager.GetDateTimeFromTimeStamp(jtCandle[0].Value<long>());
                candle.Open = jtCandle[1].Value<decimal>();
                candle.High = jtCandle[2].Value<decimal>();
                candle.Low = jtCandle[3].Value<decimal>();
                candle.Close = jtCandle[4].Value<decimal>();
                candle.Volume = jtCandle[5].Value<decimal>();

                candles.Add(candle);
            }

            return candles;
        }


        public event Action<Order> MyOrderEvent;
        public event Action<MyTrade> MyTradeEvent;
        public event Action<List<Portfolio>> PortfolioEvent;
        public event Action<List<Security>> SecurityEvent;
        public event Action<MarketDepth> MarketDepthEvent;
        public event Action<Trade> NewTradesEvent;
        public event Action ConnectEvent;
        public event Action DisconnectEvent;
        public event Action<string, LogMessageType> LogMessageEvent;
        public event Action<OptionMarketDataForConnector> AdditionalMarketDataEvent;

        private void SendLogMessage(string message, LogMessageType logMessageType)
        {
            LogMessageEvent(message, logMessageType);
        }

        public void CancelAllOrders()
        {
           
        }

        public void CancelAllOrdersToSecurity(Security security)
        {

        }

        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            return null;
        }

        public List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            return null;
        }

        public void GetOrdersState(List<Order> orders)
        {
           
        }

        public void ResearchTradesToOrders(List<Order> orders)
        {
            
        }

        public List<Candle> GetLastCandleHistory(Security security, TimeFrameBuilder timeFrameBuilder, int candleCount)
        {
            throw new NotImplementedException();
        }

        public bool SubscribeNews()
        {
            return false;
        }

        public event Action<News> NewsEvent;
    }
}

