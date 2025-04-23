using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;

namespace TeslaLogger
{
    [SuppressMessage("Design", "CA1031:Keine allgemeinen Ausnahmetypen abfangen", Justification = "<Pending>")]
    public class TeslaCanSync
    {
        private const int Seconds = 5;
        private readonly Car car;
        private readonly string logDir;
        private readonly string url = "http://teslacan-esp.lan";

        private bool run = true;
        private Thread thread;

        internal TeslaCanSync(Car c)
        {
            if (c != null)
            {
                car = c;
                url = $"http://teslacan-{c.CarInDB}.lan";
                c.Log($"TeslaCanSync: Connecting to {url}");

                logDir = Path.Combine(AppContext.BaseDirectory, "logs", $"teslacan-{c.CarInDB}");

                thread = new Thread(Start);
                thread.Name = "TeslaCAN_" + car.CarInDB;
                thread.Start();
            }
        }

        private void Start()
        {
            if (!Tools.UseScanMyTesla())
            {
                car.Log("TeslaCAN disabled!");
                return;
            }

            car.Log("Start refactored TeslaCAN Thread!");

            while (run)
            {
                var connected = false;
                while (run && !connected)
                {
                    try
                    {
                        using (var ping = new Ping())
                        {
                            var reply = ping.Send(url.Substring(7));
                            connected = reply?.Status == IPStatus.Success;
                        }
                    }
                    catch (PingException)
                    {
                    }
                    catch (SocketException)
                    {
                    }
                    catch (Exception ex)
                    {
                        car.Log("TeslaCAN: Ping " + ex.Message);
                    }

                    if (!connected)
                        Thread.Sleep(2000);
                }

                if (run && connected) GetLogFiles().Wait();

                while (run && connected)
                    try
                    {
                        var data = GetTeslaCanData().Result;

                        if (data == null || data.Count == 0)
                        {
                            // car sleeping...
                            connected = false;
                        }
                        else
                        {
                            foreach (var d in data) SaveData(d);

                            var lag = DateTime.Now - data.Last().Timestamp;
                            if (lag < TimeSpan.FromSeconds(Seconds))
                                Thread.Sleep((int)(Seconds - lag.TotalSeconds + 0.5) * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!((ex as AggregateException)?.InnerExceptions[0] is HttpRequestException))
                        {
                            car.CreateExceptionlessClient(ex).Submit();
                            car.Log("TeslaCAN: " + ex.Message);
                            Logfile.WriteException(ex.ToString());
                        }

                        connected = false;
                    }
            }
        }

        private async Task GetLogFiles()
        {
            try
            {
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                using (var client = new HttpClient())
                {
                    var result = await GetFiles(client).ConfigureAwait(true);
                    if (result != null)
                        foreach (var file in result.Files)
                            if (file.Name.EndsWith(".txt", StringComparison.InvariantCulture) &&
                                file.Name != "log.txt" ||
                                file.Name.EndsWith(".zip", StringComparison.InvariantCulture))
                            {
                                var destFile = Path.Combine(logDir, file.Name);
                                if (!System.IO.File.Exists(destFile))
                                    await DownloadFile(client, new Uri(url + file.Path), destFile).ConfigureAwait(true);
                            }
                }
            }
            catch (Exception)
            {
                car.Log("TeslaCAN: Error getting log files");
                throw;
            }
        }

        private async Task<FilesResult> GetFiles(HttpClient client)
        {
            var requestUri = new Uri(url + "/api/files?path=/sd/logs");
            var responseMessage = await client.GetAsync(requestUri).ConfigureAwait(true);
            if (!responseMessage.IsSuccessStatusCode)
            {
                car.Log($"TeslaCAN: Error getting {requestUri}");
                return null;
            }

            var resultContent = await responseMessage.Content.ReadAsStringAsync().ConfigureAwait(true);
            var result = JsonConvert.DeserializeObject<FilesResult>(resultContent);
            return result;
        }

        private async Task DownloadFile(HttpClient client, Uri requestUri, string destFile)
        {
            var responseMessage = await client.GetAsync(requestUri).ConfigureAwait(true);
            if (responseMessage.IsSuccessStatusCode)
                using (var fs = new FileStream(destFile, FileMode.CreateNew))
                {
                    await responseMessage.Content.CopyToAsync(fs).ConfigureAwait(true);
                }
            else
                car.Log($"TeslaCAN: Error getting {requestUri}");
        }

        private async Task<IList<Data>> GetTeslaCanData()
        {
            using (var client = new HttpClient())
            {
                var start = DateTime.UtcNow;
                var result = await client.GetAsync(new Uri(url + "/getdata?limit=12")).ConfigureAwait(true);
                var resultContent = await result.Content.ReadAsStringAsync().ConfigureAwait(true);

                DBHelper.AddMothershipDataToDB(url + "/getdata", start, (int)result.StatusCode, car.CarInDB);

                try
                {
                    dynamic j = JsonConvert.DeserializeObject(resultContent);

                    var arr = new List<Data>();
                    foreach (var j1 in j)
                    {
                        var data = new Data
                        {
                            Timestamp = DateTime.Parse(j1["d"].ToString()),
                            Values = j1["dict"].ToObject<Dictionary<string, object>>()
                        };
                        arr.Add(data);
                    }

                    return arr;
                }
                catch (Exception)
                {
                    car.Log("TeslaCAN: Error parsing JSON:\n" + resultContent);
                    throw;
                }
            }
        }

        private void SaveData(Data data)
        {
            car.CurrentJSON.lastScanMyTeslaReceived = data.Timestamp;
            car.CurrentJSON.CreateCurrentJSON();

            StringBuilder sb = new StringBuilder();
            sb.Append("INSERT INTO `can` (`datum`, `id`, `val`, CarId) VALUES ");
            bool first = true;

            string sqlDate = data.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", Tools.ciEnUS);

            foreach (var line in data.Values)
            {
                if (line.Value.ToString().Contains("Infinity") || line.Value.ToString().Contains("NaN"))
                {
                    continue;
                }

                switch (line.Key)
                {
                    case "2":
                        car.CurrentJSON.SMTCellTempAvg = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "5":
                        car.CurrentJSON.SMTCellMinV = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "6":
                        car.CurrentJSON.SMTCellAvgV = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "7":
                        car.CurrentJSON.SMTCellMaxV = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "9":
                        car.CurrentJSON.SMTACChargeTotal = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "11":
                        car.CurrentJSON.SMTDCChargeTotal = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "27":
                        car.CurrentJSON.SMTCellImbalance = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "28":
                        car.CurrentJSON.SMTBMSmaxCharge = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "29":
                        car.CurrentJSON.SMTBMSmaxDischarge = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "442":
                        if (Convert.ToDouble(line.Value, Tools.ciEnUS) == 287.6) // SNA - Signal not Available
                        {
                            car.CurrentJSON.SMTSpeed = 0;
                            car.Log("SMT Speed: Signal not Available");
                        }
                        else
                        {
                            car.CurrentJSON.SMTSpeed = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        }

                        break;
                    case "43":
                        car.CurrentJSON.SMTBatteryPower = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    case "71":
                        car.CurrentJSON.SMTNominalFullPack = Convert.ToDouble(line.Value, Tools.ciEnUS);
                        break;
                    default:
                        break;
                }


                if (first)
                {
                    first = false;
                }
                else
                {
                    sb.Append(",");
                }

                sb.Append("('");
                sb.Append(sqlDate);
                sb.Append("',");
                sb.Append(line.Key);
                sb.Append(",");
                sb.Append(Convert.ToDouble(line.Value, Tools.ciEnUS).ToString(Tools.ciEnUS));
                sb.Append(",");
                sb.Append(car.CarInDB);
                sb.Append(")");
            }

            using (MySqlConnection con = new MySqlConnection(DBHelper.DBConnectionstring))
            {
                con.Open();
#pragma warning disable CA2100 // SQL-Abfragen auf Sicherheitsrisiken überprüfen
                using (MySqlCommand cmd = new MySqlCommand(sb.ToString(), con))
#pragma warning restore CA2100 // SQL-Abfragen auf Sicherheitsrisiken überprüfen
                {
                    try
                    {
                        _ = SQLTracer.TraceNQ(cmd, out _);
                    }
                    catch (MySqlException ex)
                    {
                        if (ex.Message.Contains("Duplicate entry"))
                            car.Log("TeslaCAN: " + ex.Message);
                        else
                            throw;
                    }

                    try
                    {
                        using (MySqlConnection con2 = new MySqlConnection(DBHelper.DBConnectionstring))
                        {
                            con2.Open();
                            using (MySqlCommand cmd2 = new MySqlCommand("update cars set lastscanmytesla=@lastscanmytesla where id=@id", con2))
                            {
                                cmd2.Parameters.AddWithValue("@id", car.CarInDB);
                                cmd2.Parameters.AddWithValue("@lastscanmytesla", DateTime.Now);
                                _ = SQLTracer.TraceNQ(cmd2, out _);
                            }
                        }
                    }
                    catch (Exception)
                    { }
                }
            }
        }

        public void StopThread()
        {
            run = false;
        }

        public void KillThread()
        {
            try
            {
                thread?.Abort();
                thread = null;
            }
            catch (Exception ex)
            {
                Debug.Write(ex.ToString());
            }
        }

        private class Data
        {
            public DateTime Timestamp;
            public Dictionary<string, object> Values;
        }

        public class File
        {
            [JsonProperty("date")] public string Date { get; set; }

            [JsonProperty("isDir")] public bool IsDir { get; set; }

            [JsonProperty("name")] public string Name { get; set; }

            [JsonProperty("path")] public string Path { get; set; }

            [JsonProperty("size")] public int Size { get; set; }
        }

        public class FilesResult
        {
            [JsonProperty("files")] public List<File> Files { get; set; }

            [JsonProperty("freeSpaceBytes")] public long FreeSpaceBytes { get; set; }

            [JsonProperty("path")] public string Path { get; set; }
        }
    }
}