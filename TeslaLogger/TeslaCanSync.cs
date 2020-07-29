using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MySql.Data.MySqlClient;

namespace TeslaLogger
{
    public class TeslaCanSync
    {
        private const int SECONDS = 5;

        private readonly string token;

        public TeslaCanSync(string token)
        {
            this.token = token;

            new Thread(Start).Start();
        }

        private void Start()
        {
            Logfile.Log("Start refactored TeslaCAN Thread!");

            while (true)
            {
                try
                {
                    var data = GetTeslaCanData().Result;

                    if (data == null || data.Count == 0)
                    {
                        // car sleeping...
                        Thread.Sleep(20000);
                    }
                    else
                    {
                        foreach (var d in data) SaveData(d);

                        var lag = DateTime.Now - data.Last().Timestamp;
                        if (lag < TimeSpan.FromSeconds(SECONDS))
                            Thread.Sleep((int) (SECONDS - lag.TotalSeconds + 0.5) * 1000);
                    }
                }
                catch (Exception ex)
                {
                    if (!((ex as AggregateException)?.InnerExceptions[0] is HttpRequestException))
                    {
                        Logfile.Log("TeslaCAN: " + ex.Message);
                        Logfile.WriteException(ex.ToString());
                    }

                    Thread.Sleep(20000);
                }
            }
        }

        private async Task<IList<Data>> GetTeslaCanData()
        {
            var client = new HttpClient();

            var start = DateTime.UtcNow;
            var result = await client.GetAsync("http://teslacan.fritz.box:8080/getdata?limit=12");
            var resultContent = await result.Content.ReadAsStringAsync();

            DBHelper.AddMothershipDataToDB("teslacan.fritz.box:8080/getdata", start, (int) result.StatusCode);

            dynamic j = new JavaScriptSerializer().DeserializeObject(resultContent);

            var arr = new List<Data>();
            foreach (var j1 in j)
            {
                var data = new Data
                {
                    Timestamp = DateTime.Parse(j1["d"]),
                    Values = (Dictionary<string, object>) j1["dict"]
                };
                arr.Add(data);
            }

            return arr;
        }

        private static void SaveData(Data data)
        {
            DBHelper.currentJSON.lastScanMyTeslaReceived = data.Timestamp;
            DBHelper.currentJSON.CreateCurrentJSON();

            var sb = new StringBuilder();
            sb.Append("INSERT INTO `can` (`datum`, `id`, `val`) VALUES ");
            var first = true;

            var sqlDate = data.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (var line in data.Values)
            {
                if (line.Value.ToString().Contains("Infinity") || line.Value.ToString().Contains("NaN")) continue;

                switch (line.Key)
                {
                    case "2":
                        DBHelper.currentJSON.SMTCellTempAvg = Convert.ToDouble(line.Value);
                        break;
                    case "5":
                        DBHelper.currentJSON.SMTCellMinV = Convert.ToDouble(line.Value);
                        break;
                    case "6":
                        DBHelper.currentJSON.SMTCellAvgV = Convert.ToDouble(line.Value);
                        break;
                    case "7":
                        DBHelper.currentJSON.SMTCellMaxV = Convert.ToDouble(line.Value);
                        break;
                    case "28":
                        DBHelper.currentJSON.SMTBMSmaxCharge = Convert.ToDouble(line.Value);
                        break;
                    case "29":
                        DBHelper.currentJSON.SMTBMSmaxDischarge = Convert.ToDouble(line.Value);
                        break;
                    case "442":
                        if (Convert.ToDouble(line.Value) == 287.6) // SNA - Signal not Available
                        {
                            DBHelper.currentJSON.SMTSpeed = 0;
                            Logfile.Log("SMT Speed: Signal not Available");
                        }
                        else
                        {
                            DBHelper.currentJSON.SMTSpeed = Convert.ToDouble(line.Value);
                        }

                        break;
                    case "43":
                        DBHelper.currentJSON.SMTBatteryPower = Convert.ToDouble(line.Value);
                        break;
                }


                if (first)
                    first = false;
                else
                    sb.Append(",");

                sb.Append("('");
                sb.Append(sqlDate);
                sb.Append("',");
                sb.Append(line.Key);
                sb.Append(",");
                sb.Append(Convert.ToDouble(line.Value).ToString(Tools.ciEnUS));
                sb.Append(")");
            }

            using (var con = new MySqlConnection(DBHelper.DBConnectionstring))
            {
                con.Open();
                var cmd = new MySqlCommand(sb.ToString(), con);
                cmd.ExecuteNonQuery();

                try
                {
                    var lastscanmyteslafilepaht = Path.Combine(FileManager.GetExecutingPath(), "LASTSCANMYTESLA");
                    File.WriteAllText(lastscanmyteslafilepaht, sqlDate);
                }
                catch (Exception)
                {
                }
            }
        }

        private class Data
        {
            public DateTime Timestamp;
            public Dictionary<string, object> Values;
        }
    }
}