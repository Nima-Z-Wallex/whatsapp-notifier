using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace ReceiptSenderToWhatsApp
{
    public class GreenApiConfig
    {
        public string? IdInstance { get; set; }
        public string? ApiTokenInstance { get; set; }
        public string? SenderNumber { get; set; }
        public string? DestinationChat { get; set; }
        public string? TronApiKey { get; set; }
    }

    public class ApiRequest
    {
        public string Address { get; set; }
        public TaskCompletionSource<string> ResponseTask { get; set; }
        public string ApiUrl { get; set; }

        public ApiRequest(string address, string apiUrl)
        {
            Address = address;
            ApiUrl = apiUrl;
            ResponseTask = new TaskCompletionSource<string>();
        }
    }

    public class ApiRequestManager
    {
        private readonly Channel<ApiRequest> _requestChannel;
        private readonly HttpClient _httpClient;
        private readonly int _maxDailyRequests;
        private readonly ConcurrentQueue<DateTime> _requestTimestamps;
        private DateTime _lastRequestTime = DateTime.MinValue;
        private readonly int _totalAddresses;
        private bool _isRunning = true;

        public ApiRequestManager(int maxDailyRequests, int totalAddresses)
        {
            _maxDailyRequests = maxDailyRequests;
            _totalAddresses = totalAddresses;
            _requestTimestamps = new ConcurrentQueue<DateTime>();
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Add("TRON-PRO-API-KEY", "93c0e4f1-be35-4347-bdff-feb6264648d8");  // Add this line

            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            };
            _requestChannel = Channel.CreateUnbounded<ApiRequest>(channelOptions);

            _ = ProcessRequestsAsync();
        }

        public async Task<string> QueueRequest(string address, string apiUrl)
        {
            var request = new ApiRequest(address, apiUrl);
            await _requestChannel.Writer.WriteAsync(request);
            return await request.ResponseTask.Task;
        }

        public void Stop()
        {
            _isRunning = false;
            _requestChannel.Writer.Complete();
        }

        private async Task ProcessRequestsAsync()
        {
            var dynamicInterval = TimeSpan.FromDays(1).TotalMilliseconds / (_maxDailyRequests / _totalAddresses);
            var minInterval = TimeSpan.FromMinutes(1).TotalMilliseconds;
            var interval = Math.Max(minInterval, dynamicInterval);

            Console.WriteLine($"API Request Manager initialized:");
            Console.WriteLine($"- Dynamic interval: {dynamicInterval:F2}ms");
            Console.WriteLine($"- Minimum interval: {minInterval:F2}ms");
            Console.WriteLine($"- Active interval: {interval:F2}ms");
            Console.WriteLine($"- Requests per address per day: {(_maxDailyRequests / _totalAddresses):F2}");

            while (_isRunning)
            {
                try
                {
                    var request = await _requestChannel.Reader.ReadAsync();
                    var now = DateTime.UtcNow;

                    while (_requestTimestamps.TryPeek(out DateTime oldest) &&
                           (now - oldest).TotalDays >= 1)
                    {
                        _requestTimestamps.TryDequeue(out _);
                    }

                    var timeSinceLastRequest = (now - _lastRequestTime).TotalMilliseconds;
                    if (timeSinceLastRequest < interval)
                    {
                        var delay = interval - timeSinceLastRequest;
                        Console.WriteLine($"Rate limit: Waiting {delay:F2}ms before processing request for address {request.Address}");
                        await Task.Delay(TimeSpan.FromMilliseconds(delay));
                    }

                    if (_requestTimestamps.Count >= _maxDailyRequests)
                    {
                        request.ResponseTask.SetException(new Exception("Daily API limit reached"));
                        continue;
                    }

                    try
                    {
                        Console.WriteLine($"Processing API request for address {request.Address}");
                        var response = await _httpClient.GetAsync(request.ApiUrl);
                        var content = await response.Content.ReadAsStringAsync();
                        response.EnsureSuccessStatusCode();

                        _lastRequestTime = DateTime.UtcNow;
                        _requestTimestamps.Enqueue(_lastRequestTime);

                        request.ResponseTask.SetResult(content);
                        Console.WriteLine($"Successfully processed request for address {request.Address}");
                    }
                    catch (Exception ex)
                    {
                        request.ResponseTask.SetException(ex);
                        Console.WriteLine($"Error processing request for address {request.Address}: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Channel processing error: {ex.Message}");
                }
            }
        }
    }

    public class TronscanClient
    {
        private readonly System.Timers.Timer _timer;
        private readonly string _apiUrlBase;
        private readonly GreenApiConfig _greenApiConfig;
        private readonly ApiRequestManager _apiRequestManager;
        private readonly HttpClient _httpClient;
        private int _logCount = 0;
        private int _messageCount = 0;
        private bool _isFirstFetch = true;
        private readonly HashSet<string> _processedTransactionIds = new HashSet<string>();
        private readonly ConcurrentQueue<JToken> _transactionQueue = new ConcurrentQueue<JToken>();
        private readonly string _monitoredAddress;

        public TronscanClient(string address, GreenApiConfig greenApiConfig, ApiRequestManager apiRequestManager)
        {
            Console.WriteLine($"Initializing TronscanClient for address {address} sending to group {greenApiConfig.DestinationChat}...");
            _httpClient = new HttpClient();
            _apiUrlBase = "https://apilist.tronscanapi.com/api/filter/trc20/transfers?limit=50&start=0&sort=-timestamp&count=true&filterTokenValue=0";
            _greenApiConfig = greenApiConfig;
            _monitoredAddress = address;
            _apiRequestManager = apiRequestManager;
            _timer = new System.Timers.Timer(1 * 60 * 1000);
            _timer.Elapsed += async (sender, e) => await FetchAndQueueNewTransactions();
        }

        public void StartPolling()
        {
            Console.WriteLine($"Starting polling services for address {_monitoredAddress}...");
            _timer.Start();
            FetchAndQueueNewTransactions().Wait();
        }

        public void StopPolling()
        {
            Console.WriteLine($"Stopping polling services for address {_monitoredAddress}...");
            _timer.Stop();
        }

        private async Task FetchAndQueueNewTransactions()
        {
            try
            {
                _logCount++;
                Console.WriteLine($"Log Number: {_logCount} for address {_monitoredAddress}");
                Console.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"Fetching Transactions for {_monitoredAddress}...");
                Console.WriteLine("##-----------------------------##");

                var apiUrl = $"{_apiUrlBase}&relatedAddress={_monitoredAddress}";
                var content = await _apiRequestManager.QueueRequest(_monitoredAddress, apiUrl);
                Console.WriteLine($"API Response received successfully for {_monitoredAddress}");

                var transactions = JArray.Parse(JObject.Parse(content)["token_transfers"]?.ToString() ?? "[]");

                if (_isFirstFetch)
                {
                    foreach (var transaction in transactions)
                    {
                        string? transactionId = transaction["transaction_id"]?.ToString();
                        if (!string.IsNullOrEmpty(transactionId))
                        {
                            _processedTransactionIds.Add(transactionId);
                        }
                    }
                    _isFirstFetch = false;
                    Console.WriteLine($"First fetch completed for {_monitoredAddress}, no transactions logged.");
                    return;
                }

                var newTransactions = transactions
                    .Where(tx =>
                    {
                        bool isValidTransaction = tx?["tokenInfo"]?["tokenId"]?.ToString() == "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t" &&
                                                tx["to_address"]?.ToString() == _monitoredAddress &&
                                                !_processedTransactionIds.Contains(tx["transaction_id"]?.ToString() ?? string.Empty);
                        decimal amount = decimal.Parse(tx?["quant"]?.ToString() ?? "0") / 1_000_000m;
                        return isValidTransaction && amount > 3;
                    })
                    .ToList();

                foreach (var transaction in newTransactions)
                {
                    string transactionId = transaction["transaction_id"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(transactionId))
                    {
                        _processedTransactionIds.Add(transactionId);
                        _transactionQueue.Enqueue(transaction);
                    }
                }

                await ProcessQueuedTransactions();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching transactions for {_monitoredAddress}: {ex.Message}");
            }
        }
        private async Task ProcessQueuedTransactions()
        {
            try
            {
                if (!_transactionQueue.IsEmpty)
                {
                    Console.WriteLine($"Processing queue for {_monitoredAddress}. Current size: {_transactionQueue.Count}");
                }

                while (_transactionQueue.TryDequeue(out var transaction))
                {
                    string message = FormatTransactionMessage(transaction);
                    await SendToWhatsApp(message);
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing queued transactions for {_monitoredAddress}: {ex.Message}");
            }
        }

        private string FormatTransactionMessage(JToken tx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("🔄  تراکنش دریافتی جدید   🔄");

            decimal amount = decimal.Parse(tx["quant"]?.ToString() ?? "0") / 1_000_000m;
            sb.AppendLine($"💰  مبلغ واریزی :");
            sb.AppendLine($"{amount:F2} USDT");
            sb.AppendLine($"📩 واریزشده به  :");
            sb.AppendLine($"{tx["to_address"]}");
            sb.AppendLine($"📤 از:");
            sb.AppendLine($"{tx["from_address"]}");
            sb.AppendLine($"🔗هش :");
            sb.AppendLine($"{tx["transaction_id"]}");
            sb.AppendLine($"⏱️ تاریخ:{UnixTimeToDateTime(long.Parse(tx["block_ts"]?.ToString() ?? "0"))}");
            sb.AppendLine("------------------------------------------------");
            return sb.ToString();
        }

        private async Task SendToWhatsApp(string message)
        {
            try
            {
                var url = $"https://7103.api.green-api.com/waInstance{_greenApiConfig.IdInstance}/sendMessage/{_greenApiConfig.ApiTokenInstance}";
                var payload = new
                {
                    chatId = $"{_greenApiConfig.DestinationChat}@g.us",
                    message = message,
                    linkPreview = false
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _httpClient.PostAsync(url, content);
                var result = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _messageCount++;
                    Console.WriteLine($"✅ WhatsApp message sent successfully for {_monitoredAddress}!");
                    Console.WriteLine($"📩 Total Messages Sent: {_messageCount}");
                    Console.WriteLine($"API Response: {result}");
                }
                else
                {
                    Console.WriteLine($"❌ Failed to send WhatsApp message for {_monitoredAddress}. Status: {response.StatusCode}");
                    Console.WriteLine($"Error Response: {result}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending WhatsApp message for {_monitoredAddress}: {ex.Message}");
            }
        }

        private static DateTime UnixTimeToDateTime(long unixTime)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixTime).LocalDateTime;
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("Starting Tron USDT Deposit Monitor...");
            Console.WriteLine($"Application Start Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var envVars = File.ReadAllLines(".env")
                .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains('='))
                .ToDictionary(
                    line => line.Substring(0, line.IndexOf('=')),
                    line => line.Substring(line.IndexOf('=') + 1)
                );

            var baseConfig = new GreenApiConfig
            {
                IdInstance = envVars["ID_INSTANCE"],
                ApiTokenInstance = envVars["API_TOKEN_INSTANCE"],
                SenderNumber = envVars["SENDER_NUMBER"]
            };

            var addressGroupsJson = File.ReadAllText("addresses-groups.json");
            var jsonObject = JObject.Parse(addressGroupsJson);

            var clients = new List<TronscanClient>();
            var allAddresses = new List<string>();

            foreach (var property in jsonObject.Properties())
            {
                string groupId = property.Name;
                var addresses = property.Value.ToObject<List<string>>();
                if (addresses != null)
                {
                    allAddresses.AddRange(addresses);
                }
            }

            Console.WriteLine($"Total number of addresses to monitor: {allAddresses.Count}");
            Console.WriteLine("Monitored addresses:");
            foreach (var address in allAddresses)
            {
                Console.WriteLine($"- {address}");
            }

            var apiRequestManager = new ApiRequestManager(100000, allAddresses.Count);
            Console.WriteLine($"API Request Manager initialized with {allAddresses.Count} addresses");

            foreach (var property in jsonObject.Properties())
            {
                string groupId = property.Name;
                var addresses = property.Value.ToObject<List<string>>();
                if (addresses != null)
                {
                    Console.WriteLine($"Configuring group {groupId} with {addresses.Count} addresses");
                    foreach (var address in addresses)
                    {
                        var groupConfig = new GreenApiConfig
                        {
                            IdInstance = baseConfig.IdInstance,
                            ApiTokenInstance = baseConfig.ApiTokenInstance,
                            SenderNumber = baseConfig.SenderNumber,
                            DestinationChat = groupId
                        };

                        clients.Add(new TronscanClient(address, groupConfig, apiRequestManager));
                    }
                }
            }

            Console.WriteLine("Starting all monitoring clients...");
            clients.ForEach(client => client.StartPolling());
            Console.WriteLine($"All {clients.Count} monitoring clients started");

            var autoResetEvent = new System.Threading.AutoResetEvent(false);
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                Console.WriteLine("Shutting down...");
                Console.WriteLine($"Application Stop Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                apiRequestManager.Stop();
                clients.ForEach(client => client.StopPolling());
                autoResetEvent.Set();
            };

            autoResetEvent.WaitOne();
        }
    }
}