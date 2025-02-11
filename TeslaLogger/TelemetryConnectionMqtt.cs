using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using Timer = System.Timers.Timer;

namespace TeslaLogger
{
    internal class TelemetryConnectionMqtt
    {
        private readonly Car car;
        private readonly string clientId;
        private readonly AutoResetEvent closedEvent = new AutoResetEvent(false);
        private readonly Config config;

        private readonly Dictionary<string, Payload> currentPayload = new Dictionary<string, Payload>();

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

        /// <summary>
        ///     Convert a JSON string from snake case to camel case
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="msg">The input JSON string</param>
        /// <returns>The output JSON string</returns>
        private static string TransformNamingStrategy<T>(string msg)
        {
            return SerializeCamelCase(DeserializeSnakeCase<T>(msg));
        }

        private static T DeserializeSnakeCase<T>(string msg)
        {
            var obj = JsonConvert.DeserializeObject<T>(msg, new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new SnakeCaseNamingStrategy()
                }
            });
            return obj;
        }

        private static string SerializeCamelCase<T>(T obj)
        {
            var json = JsonConvert.SerializeObject(obj, new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy(true, true)
                }
            });
            return json;
        }

        private void MqttClient_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            var topic = e.Topic.Split('/');
            var msg = Encoding.UTF8.GetString(e.Message);

            if (topic.Length < 3)
            {
                Logfile.Log($"MQTT: invalid topic '{e.Topic}' msg='{msg}'");
                return;
            }

            switch (topic[2])
            {
                case "v":
                    if (topic.Length < 4)
                    {
                        Logfile.Log($"MQTT: invalid topic '{e.Topic}' msg='{msg}'");
                        return;
                    }

                    HandleVehicleValues(msg, topic[1], topic[3]);

                    break;

                case "alerts":
                    parser.handleMessage(TransformNamingStrategy<VehicleAlerts>(msg));
                    break;

                case "connectivity":
                    // ignore
                    TransformNamingStrategy<ConnectivityEvent>(msg);
                    break;

                case "errors":
                    // ignore
                    TransformNamingStrategy<VehicleErrors>(msg);
                    break;

                default:
                    Logfile.Log($"MQTT: unhandled topic '{e.Topic}' msg='{msg}'");
                    break;
            }
        }

        private void HandleVehicleValues(string msg, string vin, string key)
        {
            var obj = DeserializeSnakeCase<Topic>(msg);

            Payload payload;
            if (!currentPayload.TryGetValue(vin, out payload))
            {
                payload = new Payload
                {
                    CreatedAt = obj.CreatedAt,
                    Vin = vin,
                    Data = new List<Datum>(),
                    Timeout = new Timer(5000) { AutoReset = false }
                };
                payload.Timeout.Elapsed += delegate
                {
                    lock (payload)
                    {
                        if (payload.Data.Count > 0)
                        {
                            parser.handleMessage(SerializeCamelCase(payload));
                            payload.Data.Clear();
                        }
                    }
                };
                currentPayload[vin] = payload;
            }

            lock (payload)
            {
                if (payload.CreatedAt != obj.CreatedAt)
                {
                    if (payload.Data.Count > 0)
                    {
                        parser.handleMessage(SerializeCamelCase(payload));
                        payload.Data.Clear();
                    }

                    payload.CreatedAt = obj.CreatedAt;
                }

                payload.Data.Add(new Datum { Key = key, Value = obj.Value });
                payload.Timeout.Stop();
                payload.Timeout.Start();
            }
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

        #region JSON Classes for connectivity

        public class ConnectivityEvent
        {
            public string ConnectionId;
            public DateTime CreatedAt;
            public string NetworkInterface;
            public string Status;
            public string Vin;
        }

        #endregion

        #region JSON Classes for v

        public class Datum
        {
            public string Key;
            public Value Value;
        }

        public class LocationValue
        {
            public double Latitude;
            public double Longitude;
        }

        public class Payload
        {
            public List<Datum> Data;
            public DateTime CreatedAt;
            public string Vin;

            [JsonIgnore] public Timer Timeout;
        }

        public class Topic
        {
            public DateTime CreatedAt;
            public Value Value;
        }

        public class Value
        {
            [JsonProperty("Invalid", NullValueHandling = NullValueHandling.Ignore)]
            public bool? Invalid;

            [JsonProperty("LocationValue", NullValueHandling = NullValueHandling.Ignore)]
            public LocationValue LocationValue;

            [JsonProperty("StringValue", NullValueHandling = NullValueHandling.Ignore)]
            public string StringValue;
        }

        #endregion

        #region JSON Classes for alerts

        public class VehicleAlert
        {
            public string Name;
            public List<int?> Audiences;
            public DateTime? StartedAt;
            public DateTime? EndedAt;
        }

        public class VehicleAlerts
        {
            public List<VehicleAlert> Alerts;
            public DateTime CreatedAt;
            public string Vin;
        }

        #endregion

        #region JSON Classes for errors

        public class VehicleError
        {
            //map<string, string> tags = 3;
            public string Body;
            public string CreatedAt;
            public string Name;
        }

        public class VehicleErrors
        {
            public DateTime CreatedAt;
            public List<VehicleError> Errors;
            public string Vin;
        }

        #endregion
    }
}