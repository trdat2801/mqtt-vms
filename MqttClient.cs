using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace Project.App.Mqtt
{
    [Table("vms_mqtt_client")]
    public class MqttClient
    {
        [Key]
        [Column("client_id")]
        public string ClientId { get; set; }

        [Column("latest_time")]
        public DateTime LatestTime { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
        [NotMapped]
        public bool Stored { get; set; } = true;
    }
}
