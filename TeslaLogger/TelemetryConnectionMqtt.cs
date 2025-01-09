using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Exceptions;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace TeslaLogger
{
    internal class TelemetryConnectionMqtt
    {
        private readonly Car car;
        private readonly string clientId;
        private readonly AutoResetEvent closedEvent = new AutoResetEvent(false);
        private readonly Config config;

        private readonly MqttClient mqttClient;
        private readonly Thread t;

        public TelemetryParser parser;
        private Random r = new Random();


        public TelemetryConnectionMqtt(Car car, Config config)
        {
            this.car = car;
            this.config = config;
            clientId = Guid.NewGuid().ToString();

            if (car == null)
                return;

            parser = new TelemetryParser(car);

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
            return null;
        }

        private bool UserCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }


        private void Log(string message)
        {
            car.Log("*** FTMQTT: " + message);
        }

        private void MqttClient_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            var topic = e.Topic;
            var msg = Encoding.UTF8.GetString(e.Message);
            parser.handleMessage(msg);
        }

        private void MqttClient_ConnectionClosed(object sender, EventArgs e)
        {
            closedEvent.Set();
        }

        public void CloseConnection()
        {
        }

        public void StartConnection()
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

            mqttClient.Subscribe(new[] { $"{config.Topic}/#" }, new[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
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