using System;
using System.Threading;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace TeslaLogger
{
    internal class TelemetryConnectionMqtt : TelemetryConnection
    {
        private MqttClient mqttClient;
        private Thread mqttThread;
        private string clientId;

        public TelemetryConnectionMqtt(Car car) : base(car)
        {
        }

        #region Overrides of TelemetryConnection

        public override void Start()
        {
            mqttClient = new MqttClient("teslalogger.lan");

            mqttClient.MqttMsgPublishReceived += client_MqttMsgPublishReceived;
            mqttClient.ConnectionClosed += MqttClientOnConnectionClosed;

            clientId = Guid.NewGuid().ToString();
            mqttClient.Connect(clientId);
            mqttClient.Subscribe(new[] { "telemetry/#" }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE });

            mqttThread = new Thread(RunMqtt);
            mqttThread.Start();
        }

        #endregion

        private void RunMqtt()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(1000);

                if (!mqttClient.IsConnected)
                {
                    Logfile.Log("MQTT: Reconnect");
                    mqttClient.Connect(clientId);
                }
            }
        }

        private void client_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
        }

        private void MqttClientOnConnectionClosed(object sender, EventArgs e)
        {
        }
    }
}