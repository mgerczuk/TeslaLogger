using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace TeslaLogger
{
    internal class TelemetryConnection
    {
        private Car car;
        Thread t;
        CancellationTokenSource cts = new CancellationTokenSource();
        Random r = new Random();

        bool connect;

        public TelemetryParser parser;
        private MqttClient mqttClient;
        private readonly string clientId;


        void Log(string message)
        {
            car.Log("*** FTMQTT: " + message);
        }


        public TelemetryConnection(Car car, string brokerHostName)
        {
            this.car = car;
            if (car == null)
                return;

            parser = new TelemetryParser(car);

            mqttClient = new MqttClient(brokerHostName);

            mqttClient.MqttMsgPublishReceived += MqttClient_MqttMsgPublishReceived;
            mqttClient.ConnectionClosed += MqttClient_ConnectionClosed;

            clientId = Guid.NewGuid().ToString();
            mqttClient.Connect(clientId);
            mqttClient.Subscribe(new[] { "telemetry/#" }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE });

            t = new Thread(() => { Run(); });
            t.Start();
        }

        private void MqttClient_MqttMsgPublishReceived(object sender, uPLibrary.Networking.M2Mqtt.Messages.MqttMsgPublishEventArgs e)
        {
            var topic = e.Topic;
            var msg = System.Text.Encoding.UTF8.GetString(e.Message);
            parser.handleMessage(msg);
        }

        private void MqttClient_ConnectionClosed(object sender, EventArgs e)
        {
        }

        public void CloseConnection()
        {
        }

        public void StartConnection()
        {
        }

        private void Run()
        {
            while (true)
            {
                Thread.Sleep(1000);

                if (!mqttClient.IsConnected)
                {
                    Logfile.Log("MQTT: Reconnect");
                    mqttClient.Connect(clientId);
                }
            }
        }

        private async Task ReceiveAsync(WebSocket socket)
        {
            var buffer = new ArraySegment<byte>(new byte[1024]);
            WebSocketReceiveResult result;

            String data;

            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer, cts.Token);
                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    throw new Exception("CLOSE");

                ms.Seek(0, System.IO.SeekOrigin.Begin);

                data = Encoding.UTF8.GetString(ms.ToArray());
            }

            parser.handleMessage(data);
        }
    }
}