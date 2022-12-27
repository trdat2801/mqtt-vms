using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
//using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Project.Modules.Devices.Entities;
using Project.App.DesignPatterns.Reponsitories;
using Microsoft.EntityFrameworkCore;
using Project.App.Databases;
using StackExchange.Redis;
using Project.Modules.Devices.Jira.V3.Services;
using Project.Modules.Devices.Jira.V2.Services;

namespace Project.App.Mqtt
{
    public interface IMqttSingletonService
    {
        Task PingMessage(string topic, string payload, bool retain = true);
        Task PingBytes(string topic, byte[] payload, bool retain = true);
        Task SubscribeNewTopicAsync(string chanel);

        Task UnSubscribeTopicAsync(string chanel);
        string GetMqttClientId();
    }

    public class MqttSingletonService : IMqttSingletonService
    {
        private readonly IConfiguration Configuration;
        private readonly IMqttClient MqttClient;
        private readonly List<string> topics = new();
        private readonly IRepositoryWrapperMariaDB RepositoryWrapperMariaDB;
        private IDatabase Redis;
        private static string MqttClientId = string.Empty;
        private readonly IMiraSubcribeServiceV2 MiraSubcribeServiceV2;
        private readonly IMiraSubcribeServiceV3 MiraSubcribeServiceV3;
        private Queue<MqttApplicationMessage> store = new Queue<MqttApplicationMessage>();
        private static List<string> subscribeTopics = new List<string>();
        private byte Idx;
        public readonly static string TOPIC = "mbs/be";
        private readonly IServiceScopeFactory serviceScopeFactory;

        private async Task<(List<byte> keys, byte pos)> FindKeys()
        {
            List<byte> data = new List<byte>();
            byte pos = 0;
            for (byte i = 0; i < 255; i++)
            {
                if (await Redis.KeyExistsAsync("vms/be/" + i))
                {
                    if (i == this.Idx)
                    {
                        pos = (byte) data.Count;
                    }
                    data.Add(i);
                }
            }
            return (data, pos);
        }

        public MqttSingletonService(IConfiguration configuration, IRepositoryWrapperMariaDB RepositoryWrapperMariaDB, IServiceProvider serviceProvider, IRedisDatabaseProvider redisDatabaseProvider, IMiraSubcribeServiceV3 MiraSubcribeServiceV3, IMiraSubcribeServiceV2 MiraSubcribeServiceV2, IServiceScopeFactory serviceScopeFactory)
        {
            this.serviceScopeFactory = serviceScopeFactory;
            //Redis = redisDatabaseProvider.GetDatabase();
            this.RepositoryWrapperMariaDB = RepositoryWrapperMariaDB;
            Configuration = configuration;
            this.MiraSubcribeServiceV2 = MiraSubcribeServiceV2;
            this.MiraSubcribeServiceV3 = MiraSubcribeServiceV3;
            MqttClient mqttClient = RepositoryWrapperMariaDB.MqttClients.FindAll().OrderBy(x => x.LatestTime).FirstOrDefault();
            //Configuration["MQTT:ClientId"] + Guid.NewGuid().ToString()
            MqttClientOptionsBuilder options = new MqttClientOptionsBuilder()
                                 .WithClientId(mqttClient.ClientId)
                                 .WithTcpServer(Configuration["MQTT:Server"], int.Parse(Configuration["MQTT:Port"]))
                                 .WithWillMessage(new MqttApplicationMessage() { Payload = new byte[] { 0, 255 }, Topic = TOPIC }); 
            if (!(string.IsNullOrEmpty(Configuration["MQTT:UserName"]) || string.IsNullOrEmpty(Configuration["MQTT:Password"])))
            {
                Console.WriteLine("MQTT connect UserName: " + DateTime.Now.ToString());
                options = new MqttClientOptionsBuilder()
                                .WithClientId(mqttClient.ClientId)
                                .WithCredentials(Configuration["MQTT:UserName"], Configuration["MQTT:Password"])
                                .WithTcpServer(Configuration["MQTT:Server"], int.Parse(Configuration["MQTT:Port"]))
                                .WithWillMessage(new MqttApplicationMessage() { Payload = new byte[] { 0, 255 }, Topic = TOPIC });
            }
            List<Device> devices = RepositoryWrapperMariaDB.Devices.FindAll()
                .ToList();
            for (int i =0; i< devices.Count();i++)
            {
                topics.Add(devices[i].DeviceCode + "/DTS");
            }

            //topics = Configuration.GetSection("Mqtt:Topic").Get<List<string>>();
            this.MqttClient = this.CreateMqttClient(options, mqttClient);
            IMqttClientOptions _options = options.WithClientId(mqttClient.ClientId).Build();
#if DEBUG
            _options = options.WithClientId(mqttClient.ClientId+"_DEBUG").Build();
#endif
            _ = this.MqttClient.ConnectAsync(_options, CancellationToken.None).GetAwaiter().GetResult();
            Task.Run(MessageReceivedHandler);

        }

        IMqttClient CreateMqttClient(MqttClientOptionsBuilder builder, MqttClient mqttClient)
        {
            IMqttClient MqttClient = new MqttFactory().CreateMqttClient();
            IMqttClientOptions options = builder.WithClientId(mqttClient.ClientId).Build();
#if DEBUG
            options = builder.WithClientId(mqttClient.ClientId + "_DEBUG").Build();
#endif
            MqttClient.UseDisconnectedHandler(async e =>
            {
                subscribeTopics.Clear();
                Console.WriteLine("MQTTDisconnected: " + DateTime.Now.ToString());
                //Log.Information("### DISCONNECTED FROM SERVER ###");
                await Task.Delay(TimeSpan.FromSeconds(3));
                
                try
                {
                    await MqttClient.ConnectAsync(options, CancellationToken.None);
                }
                catch
                {
                    //Log.Information("### RECONNECTING FAILED ###");
                }
            });
            MqttClient.UseApplicationMessageReceivedHandler(async (e) =>
           {
#if DEBUG
                Console.WriteLine( "topic = " + e.ApplicationMessage.Topic + " payload = "+ Encoding.UTF8.GetString(e.ApplicationMessage.Payload));
#endif

               
               if (e.ApplicationMessage.Topic != TOPIC)
               {
                   try
                   {
                       //Task.Run(()=>SubscribeTopicAsync(e.ApplicationMessage.Payload));
                       string payload1 = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                       string[] temp = payload1.Split("=");
                       if (temp[0] == "STS")
                       {


                           string[] topic = e.ApplicationMessage.Topic.Split("/");
                           if (Startup.DeviceStatusHTN.ContainsKey(topic[0]))
                           {
                               Startup.DeviceStatusHTN[topic[0]] = payload1 + "_" + DateTime.UtcNow;
                           }
                           else
                           {
                               Startup.DeviceStatusHTN.Add(topic[0], payload1 + "_" + DateTime.UtcNow);
                           }
                       }
                       //await Task.Delay(TimeSpan.FromSeconds(5));
                   }
                   catch (Exception ex)
                   {
                       Console.WriteLine("ERROR: "+ex.GetBaseException());
                   }
                   return;
               }
               this.store.Enqueue(e.ApplicationMessage);
           });
            MqttClient.UseConnectedHandler(async e =>
            {
                Console.WriteLine("MQTTConnected : " + mqttClient.ClientId + " - Time: " + DateTime.Now.ToString());
                mqttClient.LatestTime = DateTime.UtcNow;
                MqttClientId = mqttClient.ClientId;
                this.RepositoryWrapperMariaDB.MqttClients.Update(mqttClient);
                await this.RepositoryWrapperMariaDB.SaveChangesAsync();
#if DEBUG
                 await MqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("h2/s/+").WithExactlyOnceQoS().Build());
#endif
                if (topics==null || topics.Count == 0)
                {
                    for (byte i = 0; i < 255; i++)
                    {
                        if (!await Redis.KeyExistsAsync("vms/be/" + i))
                        {
                            this.Idx = i;
                            await Redis.StringSetAsync("vms/be/" + i, mqttClient.ClientId, TimeSpan.FromSeconds(3));
                            Console.WriteLine("Idx: " + this.Idx + " | " + mqttClient.ClientId);
                            break;
                        }
                    }
                    (_, byte pos) = await FindKeys();
                    if (pos == 0)
                    {
                        await MqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("h2/will").WithExactlyOnceQoS().Build());
                    }
                    await MqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(TOPIC).WithExactlyOnceQoS().Build());
                    await Task.Delay(1300);
                    await MqttClient.PublishAsync(new MqttApplicationMessage() { Topic = TOPIC, Payload = new byte[] { 1, this.Idx } });
                }
                else
                {
                   
                    foreach (string item in topics)
                    {
                        await MqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(item).WithExactlyOnceQoS().Build());
                    }
                }
                
               
            });
            return MqttClient;
        }

        private async Task SubscribeTopicAsync(byte[] data)
        {
            if (this.MqttClient==null || data == null || data.Length < 1 || !this.MqttClient.IsConnected)
                return;
            if (data[0] == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            (List<byte> keys, byte pos) = await FindKeys();
            List<string> devices = await FindDevices(pos, keys.Count);
            await UnsubscribeAsync(subscribeTopics.ToArray()); 
            subscribeTopics.Clear();
            foreach (var item in devices)
            {
                await this.SubscribeNewTopicAsync(item+"/DTS");
                //await this.SubscribeNewTopicAsync("channels/" + item + "/messages/s");
            }
        }

        public async Task SubscribeNewTopicAsync(string topic)
        {
            if (MqttClient == null || !MqttClient.IsConnected || string.IsNullOrEmpty(topic))
                return;
            await MqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).WithExactlyOnceQoS().Build());
            subscribeTopics.Add(topic);
            Console.WriteLine("Subscribe topic = " + topic);
        }

        public async Task UnSubscribeTopicAsync( string topic)
        {
            List<string> topics = new List<string>();
            topics.Add(topic+"/DTS");
            if (MqttClient == null || !MqttClient.IsConnected || topics == null)
                return;
            Console.WriteLine("UnSubscribe topic = " + topic);
            await MqttClient.UnsubscribeAsync(topics.ToArray());
        }

        public async Task UnsubscribeAsync(params string[] topics)
        {
            if (MqttClient == null || !MqttClient.IsConnected || topics==null || topics.Length==0)
                return;
            await MqttClient.UnsubscribeAsync(topics);
        }

        private async Task<List<string>> FindDevices(int page, int totalPage)
        {
            using IServiceScope serviceScope = serviceScopeFactory.CreateScope();
            IRepositoryWrapperMariaDB RepositoryWrapperMariaDB = serviceScope.ServiceProvider.GetRequiredService<IRepositoryWrapperMariaDB>();
            int count = await RepositoryWrapperMariaDB.Devices.FindByCondition(x => x.DeviceStatus == DeviceStatus.Active && !string.IsNullOrEmpty(x.DeviceCode)).Select(x=>x.DeviceCode).Distinct().CountAsync();
            int number = count / Math.Max(totalPage,1);
            int delta = count % Math.Max(totalPage, 1);
            int skip = page * number;
            if (delta != 0 && page == totalPage-1)
            {
                number += delta;
            }
            Console.WriteLine("INFO: " + count + "|" + number + "|" + skip+" PAGINATION: "+ page+" | "+ totalPage+" | "+ this.Idx);
            List<string> deviceCodes = await RepositoryWrapperMariaDB.Devices.FindByCondition(x => x.DeviceStatus == DeviceStatus.Active && !string.IsNullOrEmpty(x.DeviceCode))
                .OrderBy(x => x.DeviceCode)
                .Skip(skip)
                .Take(number)
                .Select(x => x.DeviceCode)
                .Distinct()
                .ToListAsync();
            return deviceCodes;

        }

        private async Task MessageReceivedHandler()
        {
            while (true)
            {
                if (this.MqttClient!=null && this.MqttClient.IsConnected)
                {
                   await Redis.StringSetAsync("vms/be/" + this.Idx, this.Idx.ToString(), TimeSpan.FromSeconds(3));
                }
                if (this.store.Count > 0)
                {
                    for (int i = 0; i < 100; i++)
                    {
                        if (this.store.Count <= 0)
                        {
                            break;
                        }
                        MqttApplicationMessage message = this.store.Dequeue();
                        if (message != null)
                        {
                            try
                            {
                                //await MiraSubcribeServiceV2.OnMessageHandler(message.Topic, message.Payload, this.RepositoryWrapperMariaDB);
                                await MiraSubcribeServiceV2.OnMessageHandlerNew(message.Topic, message.Payload, this.RepositoryWrapperMariaDB);

                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("V2 ERROR: " + ex.GetBaseException() + " | " + message.Topic);
                            }

                            try
                            {
                                await MiraSubcribeServiceV3.OnMessageHandler(message.Topic, message.Payload);
                            }
                            catch (Exception ex)
                            {

                                Console.WriteLine("V3 ERROR: " + ex.GetBaseException() + " | " + message.Topic);
                            }
                        }
                    }
                }
                await Task.Delay(100);
            }
            Console.WriteLine("END " + this.store.Count);
        }
        public async Task PingMessage(string topic, string payload, bool retain = true)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithExactlyOnceQoS()
                .WithRetainFlag(retain)
                .Build();
            await MqttClient.PublishAsync(message, CancellationToken.None);
        }
        public async Task PingBytes(string topic, byte[] payload, bool retain = true)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithExactlyOnceQoS()
                .WithRetainFlag(retain)
                .Build();
            await MqttClient.PublishAsync(message, CancellationToken.None);
        }

        public string GetMqttClientId()
        {
            return MqttClientId;
        }
    }
}
