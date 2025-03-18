using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace TeslaLogger
{
    internal class TelemetryConnectionMqtt : TelemetryConnection
    {
        private readonly Car car;
        private readonly string clientId;
        private readonly AutoResetEvent closedEvent = new AutoResetEvent(false);
        private readonly Config config;

        private readonly MqttClient mqttClient;
        private readonly Thread t;

        private Random r = new Random();


        public TelemetryConnectionMqtt(Car car)
        {
            this.car = car;
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("fleet-telemetry-mqtt.json"));
            if (config == null) throw new Exception("Invalid fleet-telemetry-mqtt.json file!");

            clientId = Guid.NewGuid().ToString();

            if (car == null)
                return;

            parser = new TelemetryParser(car);
            parser.InitFromDB();

            mqttClient = new MqttClient(config.Hostname, config.Port, config.Secure, MqttSslProtocols.TLSv1_2,
                UserCertificateValidationCallback, UserCertificateSelectionCallback);

            mqttClient.MqttMsgPublishReceived += MqttClient_MqttMsgPublishReceived;
            mqttClient.ConnectionClosed += MqttClient_ConnectionClosed;

            t = new Thread(Run);
            t.Start();

            if (Connect())
            {
                Logfile.Log($"MQTT: Connect {config.Hostname} success!");
            }
            else
            {
                Logfile.Log($"MQTT: Connect {config.Hostname} failed!");
                closedEvent.Set();
            }
        }

        private X509Certificate UserCertificateSelectionCallback(object sender, string targethost,
            X509CertificateCollection localCertificates, X509Certificate remoteCertificate, string[] acceptableIssuers)
        {
            // no client certificate necessary
            return null;
        }

        private bool UserCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return sslPolicyErrors == SslPolicyErrors.None;
        }


        private void Log(string message)
        {
            car.Log("*** FTMQTT: " + message);
        }


        private void MqttClient_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            var topic = e.Topic.Split('/');
            var msg = Encoding.UTF8.GetString(e.Message);

            if (topic.Length > 3)
            {
                Log("Legacy MQTT message received");
                return;
            }

            switch (topic[2])
            {
                case "v":
                    parser.handleMessage(msg);
                    break;

                case "alerts":
                    parser.handleMessage(msg);
                    break;

                case "connectivity":
                    // ignore
                    break;

                case "errors":
                    // ignore
                    break;

                default:
                    Logfile.Log($"MQTT: unhandled topic '{e.Topic}' msg='{msg}'");
                    break;
            }
        }

        private void MqttClient_ConnectionClosed(object sender, EventArgs e)
        {
            closedEvent.Set();
        }

        public override void CloseConnection()
        {
        }

        public override void StartConnection()
        {
        }

        private bool Connect()
        {
            try
            {
                mqttClient.Connect(clientId, config.User, config.Password);
            }
            catch (Exception)
            {
                return false;
            }

            if (!mqttClient.IsConnected) return false;

            mqttClient.Subscribe(new[] { $"{config.Topic}/{car.Vin}/#" },
                new[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
            return true;
        }

        private void Run()
        {
            while (true)
            {
                closedEvent.WaitOne();

                while (true)
                {
                    Thread.Sleep(1000);
                    Logfile.Log($"MQTT: Connect {config.Hostname} retry...");
                    if (Connect())
                    {
                        Logfile.Log($"MQTT: Connect {config.Hostname} success!");
                        break;
                    }
                }
            }
        }

        public class Config
        {
            [JsonProperty("hostname")] public string Hostname;
            [JsonProperty("password")] public string Password;
            [JsonProperty("port")] public int Port;
            [JsonProperty("secure")] public bool Secure;
            [JsonProperty("topic")] public string Topic;
            [JsonProperty("user")] public string User;
        }
    }
}