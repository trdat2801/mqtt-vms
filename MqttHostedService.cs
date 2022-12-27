using Microsoft.Extensions.Hosting;
//using Serilog;
using System.Threading;
using System.Threading.Tasks;

namespace Project.App.Mqtt
{
    public class MqttHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            //Log.Information("Start mqtt host service");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            //Log.Information("Stop mqtt host service");
            return Task.CompletedTask;
        }
    }
}
