using System;
using System.Collections.Generic;
using System.IO;
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
        private const int SECONDS = 10;

        private readonly bool run = true;
        private readonly Thread thread;
        private readonly string token;

        public TeslaCanSync(string token)
        {
            this.token = token;

            thread = new Thread(Start);
            thread.Start();
        }

        private void Start()
        {
            Logfile.Log("Start refactored TeslaCAN Thread!");

            while (run)
            {
                try
                {
                    var data = GetTeslaCanData().Result;

                    if (data == null)
                    {
                        // car sleeping...
                        Thread.Sleep(20000);
                    }
                    else
                    {
                        SaveData(data);

                        var lag = DateTime.Now - data.Timestamp;
                        if (lag < TimeSpan.FromSeconds(SECONDS))
                            Thread.Sleep((int) (SECONDS - lag.TotalSeconds + 0.5) * 1000);
                    }
                }
                catch (Exception ex)
                {
                    Logfile.Log("TeslaCAN: " + ex.Message);
                    Logfile.WriteException(ex.ToString());
                    Thread.Sleep(20000);
                }
            }
        }

        private async Task<Data> GetTeslaCanData()
        {
            HttpClient client = new HttpClient();
            FormUrlEncodedContent content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("t", token)
            });

            DateTime start = DateTime.UtcNow;
            HttpResponseMessage result =
                await client.PostAsync("http://teslacan.fritz.box:8080/get_scanmytesla", content);
            var resultContent = await result.Content.ReadAsStringAsync();

            DBHelper.AddMothershipDataToDB("teslacan.fritz.box:8080/get_scanmytesla", start, (int) result.StatusCode);

            if (resultContent == "not found") return null;

            string temp = resultContent;
            var i = temp.IndexOf("\r\n");
            string id = temp.Substring(0, i);

            temp = temp.Substring(i + 2);

            i = temp.IndexOf("\r\n");
            string date = temp.Substring(0, i);
            temp = temp.Substring(i + 2);

            dynamic j = new JavaScriptSerializer().DeserializeObject(temp);

            var data = new Data
            {
                Timestamp = DateTime.Parse(j["d"]),
                Values = (Dictionary<string, object>) j["dict"]
            };

            return data;
        }

        private static void SaveData(Data data)
        {
            DBHelper.currentJSON.lastScanMyTeslaReceived = data.Timestamp;
            DBHelper.currentJSON.CreateCurrentJSON();

            StringBuilder sb = new StringBuilder();
            sb.Append("INSERT INTO `can` (`datum`, `id`, `val`) VALUES ");
            bool first = true;

            string sqlDate = data.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (KeyValuePair<string, object> line in data.Values)
            {
                if (line.Value.ToString().Contains("Infinity") || line.Value.ToString().Contains("NaN"))
                {
                    continue;
                }

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
                sb.Append(Convert.ToDouble(line.Value).ToString(Tools.ciEnUS));
                sb.Append(")");
            }

            using (MySqlConnection con = new MySqlConnection(DBHelper.DBConnectionstring))
            {
                con.Open();
                MySqlCommand cmd = new MySqlCommand(sb.ToString(), con);
                cmd.ExecuteNonQuery();

                try
                {
                    string lastscanmyteslafilepaht = Path.Combine(FileManager.GetExecutingPath(), "LASTSCANMYTESLA");
                    File.WriteAllText(lastscanmyteslafilepaht, sqlDate);
                }
                catch (Exception)
                { }
            }
        }

        private class Data
        {
            public Dictionary<string, object> Values;
            public DateTime Timestamp;
        }
    }
}